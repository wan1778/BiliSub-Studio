package ocr

import (
	"bytes"
	"context"
	"math"
	"strings"
	"sync"
	"testing"
	"time"
)

type parallelProbeOCR struct {
	mu        sync.Mutex
	active    int
	maxActive int
}

func (p *parallelProbeOCR) Parallelism() int { return 2 }
func (p *parallelProbeOCR) Run(ctx context.Context, image string) (Result, error) {
	p.mu.Lock()
	p.active++
	if p.active > p.maxActive {
		p.maxActive = p.active
	}
	p.mu.Unlock()
	select {
	case <-ctx.Done():
		return Result{}, ctx.Err()
	case <-time.After(25 * time.Millisecond):
	}
	p.mu.Lock()
	p.active--
	p.mu.Unlock()
	return Result{OK: true, Detected: true, Text: image, Confidence: 0.95}, nil
}

func (p *parallelProbeOCR) MaxActive() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.maxActive
}

func TestHybridOCRCandidatesRunConcurrentlyAndKeepCandidateOrder(t *testing.T) {
	runner := &parallelProbeOCR{}
	candidates := []*scanOCRCandidate{
		{At: 1, FrameIndex: 1, Image: "first"},
		{At: 1.25, FrameIndex: 2, Image: "second"},
	}
	runScanOCRCandidates(context.Background(), runner, scanModeFor("accurate", 1), candidates, 1)
	if runner.MaxActive() != 2 {
		t.Fatalf("hybrid max concurrency=%d want 2", runner.MaxActive())
	}
	if candidates[0].Result.Text != "first" || candidates[1].Result.Text != "second" {
		t.Fatalf("results lost candidate ordering: %+v %+v", candidates[0].Result, candidates[1].Result)
	}
	if candidates[0].Calls != 1 || candidates[1].Calls != 1 {
		t.Fatalf("unexpected call accounting: %d %d", candidates[0].Calls, candidates[1].Calls)
	}
}

func TestScanModesBoundLongVideoSamplingWithoutChangingModel(t *testing.T) {
	cases := []struct {
		name      string
		wantFPS   float64
		wantGuard float64
	}{
		{name: "fast", wantFPS: 1.5, wantGuard: 8},
		{name: "balanced", wantFPS: 2.5, wantGuard: 5},
		{name: "accurate", wantFPS: 4, wantGuard: 3},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			m := scanModeFor(tc.name, 1)
			if m.FPS != tc.wantFPS || m.GuardSeconds != tc.wantGuard {
				t.Fatalf("mode=%s fps/guard=%v/%v want=%v/%v", tc.name, m.FPS, m.GuardSeconds, tc.wantFPS, tc.wantGuard)
			}
		})
	}

	sensitive := scanModeFor("accurate", 0.75)
	if sensitive.DiffTrigger >= 0.10 {
		t.Fatalf("sensitive diff=%v should be below base 0.10", sensitive.DiffTrigger)
	}
}

func TestEdgeSignatureDetectsColoredSubtitleLikeEdges(t *testing.T) {
	w, h := 320, 80
	base := make([]byte, w*h*3)
	withText := append([]byte(nil), base...)
	for y := 28; y < 55; y++ {
		for x := 35; x < 285; x++ {
			if (x/6)%2 == 0 {
				i := (y*w + x) * 3
				withText[i], withText[i+1], withText[i+2] = 255, 210, 20
			}
		}
	}
	a := makeEdgeSignature(base, w, h)
	b := makeEdgeSignature(withText, w, h)
	if d := edgeSignatureDiff(a, b); d < 0.20 {
		t.Fatalf("colored subtitle edge diff=%v too small", d)
	}
	if bytes.Equal(a, b) {
		t.Fatal("signature unchanged")
	}
}

func TestScanSimilarityToleratesMinorOCRNoise(t *testing.T) {
	if got := scanSimilarity("万年老祖归来", "万年老祖 归来！"); got < 0.90 {
		t.Fatalf("similarity=%v", got)
	}
	if got := scanSimilarity("第一句话", "完全不同内容"); got > 0.50 {
		t.Fatalf("different text similarity=%v", got)
	}
}

func TestScanFilterDropsFramesBeforeCPUCropScale(t *testing.T) {
	reg := ScanRegion{X: 0.05, Y: 0.65, W: 0.90, H: 0.29}
	mode := scanModeFor("accurate", 1)
	software := scanFilter(reg, mode, false)
	if !strings.HasPrefix(software, "fps=4,crop=") {
		t.Fatalf("software filter must sample before crop/scale: %q", software)
	}
	hardware := scanFilter(reg, mode, true)
	if !strings.HasPrefix(hardware, "fps=4,hwdownload,format=nv12|p010le|p016le,crop=") {
		t.Fatalf("NVDEC filter must sample CUDA frames before hwdownload: %q", hardware)
	}
}

func TestScanFFmpegArgsEnableCUDAOnlyForNVDEC(t *testing.T) {
	software := strings.Join(scanFFmpegArgs("movie.mp4", 12.5, "fps=4,format=rgb24", false), " ")
	if strings.Contains(software, "-hwaccel") {
		t.Fatalf("software args unexpectedly enable hwaccel: %s", software)
	}
	hardware := strings.Join(scanFFmpegArgs("movie.mp4", 12.5, "fps=4,format=rgb24", true), " ")
	for _, want := range []string{"-hwaccel cuda", "-hwaccel_output_format cuda", "-hwaccel_device 0", "-ss 12.500"} {
		if !strings.Contains(hardware, want) {
			t.Fatalf("NVDEC args missing %q: %s", want, hardware)
		}
	}
}

func TestBuildLiveScanResultIncludesRecentCuesAndPending(t *testing.T) {
	cues := []Cue{{Start: 1, End: 2, Text: "第一句", Conf: .91}, {Start: 3, End: 4, Text: "第二句", Conf: .92}}
	pending := &Cue{Start: 5, End: 5.2, Text: "第三句", Conf: .93}
	got := buildLiveScanResult(cues, pending, 11, 7, 3, 2, 25*time.Millisecond, 10, 2, 1,
		scanDecoderDecision{Mode: "nvdec"},
		120*time.Millisecond, 20*time.Millisecond, 15*time.Millisecond, 80*time.Millisecond,
		5.5, 2.0, 1.3, 45.0, "第三句", 5, .93)
	if got["cue_count"] != 3 || got["ocr_calls"] != 7 || got["frames"] != 11 {
		t.Fatalf("live=%#v", got)
	}
	if got["elapsed_seconds"] != 2.0 || got["progress_percent"] != 45.0 || got["parallelism_selected"] != 1 {
		t.Fatalf("progress telemetry=%#v", got)
	}
	recent, ok := got["recent_cues"].([]Cue)
	if !ok || len(recent) != 3 || recent[2].Text != "第三句" {
		t.Fatalf("recent=%#v", got["recent_cues"])
	}
	if got["last_confidence"] != .93 {
		t.Fatalf("confidence=%#v", got["last_confidence"])
	}
	if got["visual_skips"] != 10 || got["visual_confirmations"] != 2 || got["ocr_retries"] != 1 {
		t.Fatalf("telemetry=%#v", got)
	}
	if got["decoder"] != "nvdec" || got["frame_pipeline_seconds"].(float64) <= 0 || got["visual_seconds"].(float64) <= 0 {
		t.Fatalf("decoder telemetry=%#v", got)
	}
	if got["ocr_calls_per_cue"].(float64) <= 0 {
		t.Fatalf("calls/cue=%#v", got["ocr_calls_per_cue"])
	}
}

func TestSubtitleTrackerRejectsSingleFrameShortASCIIGarbage(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "OV", Confidence: 0.78})
	tracker.Observe(1.2, Result{OK: true, Detected: false})
	tracker.Finish(2.0)
	if got := tracker.Cues(); len(got) != 0 {
		t.Fatalf("single-frame garbage became cue: %#v", got)
	}
}

func TestSubtitleTrackerRejectsForeignScriptGarbage(t *testing.T) {
	for _, text := range []string{"A", "N", "W", "OV", "ILLC", "AIYI", "2/", "铺 U 碎", "AI时代", "HELLO", "カナ", "한글"} {
		tracker := newSubtitleTracker(scanModeFor("balanced", 1))
		for i := 0; i < 8; i++ {
			tracker.Observe(1+float64(i)*0.2, Result{OK: true, Detected: true, Text: text, Confidence: 0.99})
		}
		tracker.Finish(3)
		if got := tracker.Cues(); len(got) != 0 {
			t.Fatalf("foreign/non-Chinese %q became cue: %#v", text, got)
		}
	}
}

func TestSubtitleTrackerForeignNoiseDoesNotCloseActiveChineseCue(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "真正字幕", Confidence: 0.96})
	tracker.Observe(1.2, Result{OK: true, Detected: true, Text: "真正字幕", Confidence: 0.95})
	for i, text := range []string{"A", "ILLC", "铺 U 碎", "2/", "AI时代"} {
		tracker.Observe(2+float64(i)*0.2, Result{OK: true, Detected: true, Text: text, Confidence: 0.99})
	}
	tracker.Observe(3.2, Result{OK: true, Detected: true, Text: "真正字幕", Confidence: 0.94})
	tracker.Finish(4.0)
	got := tracker.Cues()
	if len(got) != 1 || got[0].Text != "真正字幕" {
		t.Fatalf("foreign noise split/replaced active Chinese cue: %#v", got)
	}
	if got[0].End < 3.2 {
		t.Fatalf("active Chinese cue was shortened by foreign noise: %#v", got[0])
	}
}

func TestSubtitleTrackerPreservesChineseWithNumbersAndPunctuation(t *testing.T) {
	for _, text := range []string{"第3集", "2026年", "你好！", "价格100元", "好"} {
		tracker := newSubtitleTracker(scanModeFor("balanced", 1))
		tracker.Observe(1.0, Result{OK: true, Detected: true, Text: text, Confidence: 0.96})
		tracker.Observe(1.2, Result{OK: true, Detected: true, Text: text, Confidence: 0.95})
		tracker.Finish(2.0)
		got := tracker.Cues()
		if len(got) != 1 || got[0].Text != text {
			t.Fatalf("legitimate Chinese %q text was filtered: %#v", text, got)
		}
	}
}

func TestNormalizeChineseSubtitleTextRequiresHanAndRejectsForeignLetters(t *testing.T) {
	for _, text := range []string{"ILLC", "AIYI", "2/", "铺 U 碎", "AI时代", "HELLO", "カナ", "한글"} {
		if got, ok := NormalizeChineseSubtitleText(text); ok || got != "" {
			t.Fatalf("NormalizeChineseSubtitleText(%q)=(%q,%v), want rejected", text, got, ok)
		}
	}
	got, ok := NormalizeChineseSubtitleText("炼化破珠子， ，是巧合")
	if !ok || got != "炼化破珠子，是巧合" {
		t.Fatalf("punctuation normalization=(%q,%v)", got, ok)
	}
	got, ok = NormalizeChineseSubtitleText("第3集")
	if !ok || got != "第3集" {
		t.Fatalf("Chinese+digits normalization=(%q,%v)", got, ok)
	}
}

func TestSubtitleTrackerAcceptsConfirmedSingleCJKCharacter(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "好", Confidence: 0.91})
	if !tracker.NeedsConfirmation() {
		t.Fatal("first real-text candidate should request confirmation")
	}
	tracker.Observe(1.2, Result{OK: true, Detected: true, Text: "好", Confidence: 0.93})
	if tracker.NeedsConfirmation() {
		t.Fatal("confirmed subtitle should clear candidate confirmation")
	}
	tracker.Observe(2.0, Result{OK: true, Detected: false})
	tracker.Observe(2.2, Result{OK: true, Detected: false})
	tracker.Finish(2.5)
	got := tracker.Cues()
	if len(got) != 1 || got[0].Text != "好" {
		t.Fatalf("confirmed one-character CJK subtitle lost: %#v", got)
	}
	if got[0].Start != 1.0 {
		t.Fatalf("cue start=%v want first candidate timestamp 1.0", got[0].Start)
	}
}

func TestSubtitleTrackerDoesNotReplaceActiveCueUntilNewTextConfirmed(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "第一句", Confidence: 0.95})
	tracker.Observe(1.2, Result{OK: true, Detected: true, Text: "第一句", Confidence: 0.94})

	tracker.Observe(3.0, Result{OK: true, Detected: true, Text: "OV", Confidence: 0.81})
	tracker.Observe(3.2, Result{OK: true, Detected: true, Text: "第二句", Confidence: 0.96})
	if !tracker.NeedsConfirmation() {
		t.Fatal("new subtitle candidate should require confirmation")
	}
	tracker.Observe(3.4, Result{OK: true, Detected: true, Text: "第二句", Confidence: 0.95})
	tracker.Finish(5.0)

	got := tracker.Cues()
	if len(got) != 2 {
		t.Fatalf("cues=%#v", got)
	}
	if got[0].Text != "第一句" || got[1].Text != "第二句" {
		t.Fatalf("unexpected transition cues=%#v", got)
	}
	if got[0].End < 3.2 || got[0].End > 3.41 {
		t.Fatalf("first cue ended at %v; should survive unconfirmed garbage until confirmed replacement", got[0].End)
	}
}

func TestSubtitleTrackerRestoreDropsLegacyForeignGarbage(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	active := &Cue{Start: 5, End: 6, Text: "AIYI", Conf: .99}
	tracker.Restore([]Cue{
		{Start: 1, End: 2, Text: "ILLC", Conf: .99},
		{Start: 2, End: 2.5, Text: "铺 U 碎", Conf: .98},
		{Start: 3, End: 4, Text: "保留字幕", Conf: .94},
	}, active)
	got := tracker.Cues()
	if len(got) != 1 || got[0].Text != "保留字幕" {
		t.Fatalf("legacy foreign noise survived restore: %#v", got)
	}
	if tracker.Active() != nil {
		t.Fatalf("legacy active foreign noise survived restore: %#v", tracker.Active())
	}
}

func TestSubtitleTrackerSurvivesSingleEmptyMiss(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.96})
	tracker.Observe(1.2, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.95})
	tracker.Observe(2.0, Result{OK: true, Detected: false})
	if !tracker.NeedsConfirmation() {
		t.Fatal("single empty observation should request confirmation")
	}
	tracker.Observe(2.2, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.94})
	tracker.Finish(3.0)
	got := tracker.Cues()
	if len(got) != 1 || got[0].Text != "字幕" {
		t.Fatalf("single miss split/lost cue: %#v", got)
	}
	if got[0].End < 2.2 {
		t.Fatalf("cue did not survive miss: %#v", got[0])
	}
}

func TestEdgeSignatureActivityRejectsBlankAndKeepsSubtitleLikeText(t *testing.T) {
	w, h := 320, 80
	blank := make([]byte, w*h*3)
	withText := append([]byte(nil), blank...)
	for y := 26; y < 57; y++ {
		for x := 40; x < 280; x++ {
			if (x/5)%2 == 0 {
				i := (y*w + x) * 3
				withText[i], withText[i+1], withText[i+2] = 245, 220, 30
			}
		}
	}
	if got := edgeSignatureActivity(makeEdgeSignature(blank, w, h)); got != 0 {
		t.Fatalf("blank activity=%v want 0", got)
	}
	if got := edgeSignatureActivity(makeEdgeSignature(withText, w, h)); got < 0.05 {
		t.Fatalf("subtitle-like activity=%v too small", got)
	}
}

func TestShouldRunOCRSkipsInactiveBlankGuardButConfirmsTransitions(t *testing.T) {
	if shouldRunOCR(true, false, false, false, 0) {
		t.Fatal("blank inactive ROI should not OCR merely because guard elapsed")
	}
	if !shouldRunOCR(false, true, false, false, 0.12) {
		t.Fatal("changed active-looking ROI should OCR")
	}
	if !shouldRunOCR(false, true, false, true, 0) {
		t.Fatal("active subtitle disappearing must OCR once to confirm transition")
	}
	if !shouldRunOCR(false, false, true, false, 0) {
		t.Fatal("forced temporal confirmation must always OCR")
	}
}

func TestSubtitleTrackerAllowsHighConfidenceCJKVisualConfirmation(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("accurate", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "你好世界", Confidence: 0.96})
	if !tracker.CanVisualConfirm() {
		t.Fatal("high-confidence CJK candidate should be eligible for stable visual confirmation")
	}
	if !tracker.ConfirmVisual(1.25) {
		t.Fatal("stable visual frame should promote eligible subtitle")
	}
	if tracker.NeedsConfirmation() {
		t.Fatal("visual-confirmed subtitle should no longer need OCR confirmation")
	}
	got := tracker.Active()
	if got == nil || got.Text != "你好世界" || got.Start != 1.0 || got.End < 1.5 {
		t.Fatalf("visual-confirmed active cue=%#v", got)
	}
}

func TestSubtitleTrackerNeverVisualConfirmsShortASCIIGarbage(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("accurate", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "OV", Confidence: 0.99})
	if tracker.CanVisualConfirm() || tracker.ConfirmVisual(1.25) {
		t.Fatal("short ASCII false-positive shape must still require OCR confirmation")
	}
}

func TestSubtitleTrackerExtendsConfirmedCueFromStableVisualFrames(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("accurate", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.96})
	tracker.Observe(1.25, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.95})
	before := tracker.Active()
	if before == nil {
		t.Fatal("expected active subtitle")
	}
	if !tracker.ExtendActiveVisual(3.0) {
		t.Fatal("stable visual frame should extend active subtitle")
	}
	after := tracker.Active()
	if after == nil || after.End <= before.End || after.End < 3.25 {
		t.Fatalf("active cue did not extend: before=%#v after=%#v", before, after)
	}
}

func TestSubtitleTrackerCanConfirmSingleOCRMissWithStableBlankVisual(t *testing.T) {
	tracker := newSubtitleTracker(scanModeFor("accurate", 1))
	tracker.Observe(1.0, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.96})
	tracker.Observe(1.25, Result{OK: true, Detected: true, Text: "字幕", Confidence: 0.95})
	tracker.Observe(3.0, Result{OK: true, Detected: false})
	if !tracker.CanVisualConfirmEmpty() {
		t.Fatal("one OCR miss on an active subtitle should allow a second stable blank visual confirmation")
	}
	if !tracker.ConfirmVisualEmpty(3.25) {
		t.Fatal("stable blank visual should close the active cue after one OCR empty observation")
	}
	got := tracker.Cues()
	if len(got) != 1 || got[0].Text != "字幕" || math.Abs(got[0].End-3.0) > 0.001 {
		t.Fatalf("visual-empty confirmation produced wrong cue: %#v", got)
	}
}

type benchmarkBatchOCR struct {
	mu    sync.Mutex
	sizes []int
}

func (b *benchmarkBatchOCR) Run(ctx context.Context, image string) (Result, error) {
	return Result{OK: true, Detected: true, Text: image, Confidence: .95}, nil
}
func (b *benchmarkBatchOCR) BatchCapable() bool { return true }
func (b *benchmarkBatchOCR) RunBatch(ctx context.Context, images []string) ([]Result, error) {
	b.mu.Lock()
	b.sizes = append(b.sizes, len(images))
	b.mu.Unlock()
	var delay time.Duration
	switch len(images) {
	case 1:
		delay = 80 * time.Millisecond
	case 2:
		delay = 100 * time.Millisecond
	case 4:
		delay = 120 * time.Millisecond
	default:
		delay = 50 * time.Millisecond
	}
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-time.After(delay):
	}
	out := make([]Result, len(images))
	for i, image := range images {
		out[i] = Result{OK: true, Detected: true, Text: image, Confidence: .95}
	}
	return out, nil
}

func TestNormalizeScanBatchAcceptsOnlyRC12Modes(t *testing.T) {
	for _, in := range []string{"", "auto", "1", "2", "4", " AUTO "} {
		if _, err := normalizeScanBatch(in); err != nil {
			t.Fatalf("normalizeScanBatch(%q): %v", in, err)
		}
	}
	for _, in := range []string{"0", "3", "8", "16", "gpu"} {
		if _, err := normalizeScanBatch(in); err == nil {
			t.Fatalf("normalizeScanBatch(%q) unexpectedly accepted", in)
		}
	}
}

func TestBenchmarkScanBatchChoosesBestPerImageThroughput(t *testing.T) {
	runner := &benchmarkBatchOCR{}
	got, elapsed, err := benchmarkScanBatch(context.Background(), runner, "same-frame")
	if err != nil {
		t.Fatal(err)
	}
	if got != 4 {
		t.Fatalf("selected batch=%d want 4", got)
	}
	if elapsed < 250*time.Millisecond {
		t.Fatalf("benchmark elapsed=%v unexpectedly short", elapsed)
	}
	runner.mu.Lock()
	defer runner.mu.Unlock()
	if len(runner.sizes) != 3 || runner.sizes[0] != 1 || runner.sizes[1] != 2 || runner.sizes[2] != 4 {
		t.Fatalf("benchmark sizes=%v", runner.sizes)
	}
}

func TestRunScanOCRCandidatesUsesOneBatchCallAndKeepsOrder(t *testing.T) {
	runner := &benchmarkBatchOCR{}
	candidates := []*scanOCRCandidate{
		{At: 1, Image: "one"}, {At: 2, Image: "two"}, {At: 3, Image: "three"}, {At: 4, Image: "four"},
	}
	runScanOCRCandidates(context.Background(), runner, scanModeFor("balanced", 1), candidates, 4)
	for i, want := range []string{"one", "two", "three", "four"} {
		if candidates[i].Err != nil || candidates[i].Result.Text != want {
			t.Fatalf("candidate[%d]=%+v", i, candidates[i])
		}
		if candidates[i].Calls != 1 {
			t.Fatalf("candidate[%d] images=%d want 1", i, candidates[i].Calls)
		}
	}
	if candidates[0].BatchCalls != 1 || candidates[1].BatchCalls != 0 || candidates[2].BatchCalls != 0 || candidates[3].BatchCalls != 0 {
		t.Fatalf("batch call accounting=%d/%d/%d/%d", candidates[0].BatchCalls, candidates[1].BatchCalls, candidates[2].BatchCalls, candidates[3].BatchCalls)
	}
}

type blockingBatchOCR struct{}

func (b *blockingBatchOCR) Run(ctx context.Context, image string) (Result, error) {
	<-ctx.Done()
	return Result{}, ctx.Err()
}
func (b *blockingBatchOCR) BatchCapable() bool { return true }
func (b *blockingBatchOCR) RunBatch(ctx context.Context, images []string) ([]Result, error) {
	<-ctx.Done()
	return nil, ctx.Err()
}

func TestBatchCancellationPropagatesToEveryCandidateWithoutReordering(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	candidates := []*scanOCRCandidate{{At: 1, Image: "one"}, {At: 2, Image: "two"}}
	runScanOCRCandidates(ctx, &blockingBatchOCR{}, scanModeFor("balanced", 1), candidates, 2)
	for i, candidate := range candidates {
		if candidate.Err == nil || !strings.Contains(candidate.Err.Error(), "deadline exceeded") {
			t.Fatalf("candidate[%d] err=%v", i, candidate.Err)
		}
	}
	if candidates[0].BatchCalls != 1 || candidates[1].BatchCalls != 0 {
		t.Fatalf("batch accounting after cancel=%d/%d", candidates[0].BatchCalls, candidates[1].BatchCalls)
	}
}

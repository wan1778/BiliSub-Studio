package ocr

import (
	"bilisubstudio/internal/jobs"
	"context"
	"errors"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	scanDefaultOverlapSeconds  = 8.0
	scanMinCoreDurationSeconds = 120.0
	scanMaxManualParallelism   = 16
	scanAutoMinThroughputGain  = 0.10
	// Auto calibration is deliberately bounded. A high-concurrency level is
	// useful only if it can make forward progress promptly; one stuck lane must
	// never keep the OCR job in "Đang đo ..." forever.
	scanAutoBenchmarkLevelTimeout = 15 * time.Second
	scanAutoWorkerScaleTimeout    = 30 * time.Second
	scanAutoPoolRestoreTimeout    = 8 * time.Second
	scanAutoPoolResetTimeout      = 30 * time.Second
)

type scanSegment struct {
	Index     int     `json:"index"`
	CoreStart float64 `json:"core_start"`
	CoreEnd   float64 `json:"core_end"`
	ScanStart float64 `json:"scan_start"`
	ScanEnd   float64 `json:"scan_end"`
}

type scanLaneState struct {
	MediaSeconds float64
	Cues         []Cue
	Active       *Cue
	Frames       int
	Stats        scanCheckpointStats
}

type scanLaneProgress struct {
	MediaSeconds         float64
	Cues                 []Cue
	Active               *Cue
	Frames               int
	OCRImages            int
	OCRCalls             int
	VisualSkips          int
	VisualConfirmations  int
	OCRRetries           int
	FramePipelineSeconds float64
	VisualSeconds        float64
	EncodeSeconds        float64
	OCRSeconds           float64
	Decoder              scanDecoderDecision
}

type scanRunOptions struct {
	StartAt           float64
	EndAt             float64
	DisableCheckpoint bool
	ForceBatchOne     bool
	PauseRequested    func() bool
	OnSafeState       func(scanLaneState)
	OnProgress        func(scanLaneProgress)
	Resume            *scanLaneState
}

func normalizeScanParallelism(value string) (string, error) {
	value = strings.ToLower(strings.TrimSpace(value))
	if value == "" {
		return "auto", nil
	}
	if value == "auto" {
		return value, nil
	}
	n, err := strconv.Atoi(value)
	if err != nil || n < 1 || n > scanMaxManualParallelism {
		return "", fmt.Errorf("số luồng quét OCR không hợp lệ: %s", value)
	}
	return strconv.Itoa(n), nil
}

func explicitScanParallelism(value string) int {
	if value == "auto" || strings.TrimSpace(value) == "" {
		return 1
	}
	n, err := strconv.Atoi(value)
	if err != nil || n < 1 {
		return 1
	}
	if n > scanMaxManualParallelism {
		return scanMaxManualParallelism
	}
	return n
}

func maxParallelismForDuration(duration float64) int {
	if duration <= 0 || math.IsNaN(duration) || math.IsInf(duration, 0) {
		return 1
	}
	n := int(math.Floor(duration / scanMinCoreDurationSeconds))
	if n < 1 {
		n = 1
	}
	if n > scanMaxManualParallelism {
		n = scanMaxManualParallelism
	}
	return n
}

func buildScanSegments(duration float64, parallelism int, overlap float64) ([]scanSegment, error) {
	if duration <= 0 || math.IsNaN(duration) || math.IsInf(duration, 0) {
		return nil, errors.New("thời lượng video không hợp lệ cho quét song song")
	}
	if parallelism < 1 || parallelism > scanMaxManualParallelism {
		return nil, fmt.Errorf("số lane OCR không hợp lệ: %d", parallelism)
	}
	if overlap < 0 || math.IsNaN(overlap) || math.IsInf(overlap, 0) {
		return nil, errors.New("overlap OCR không hợp lệ")
	}
	if parallelism > maxParallelismForDuration(duration) {
		parallelism = maxParallelismForDuration(duration)
	}
	segments := make([]scanSegment, parallelism)
	for i := 0; i < parallelism; i++ {
		coreStart := duration * float64(i) / float64(parallelism)
		coreEnd := duration * float64(i+1) / float64(parallelism)
		scanStart := math.Max(0, coreStart-overlap)
		scanEnd := math.Min(duration, coreEnd+overlap)
		segments[i] = scanSegment{
			Index: i, CoreStart: coreStart, CoreEnd: coreEnd,
			ScanStart: scanStart, ScanEnd: scanEnd,
		}
	}
	return segments, nil
}

func cueOwnedBySegment(c Cue, seg scanSegment, last bool) bool {
	if c.Start < seg.CoreStart {
		return false
	}
	if last {
		return c.Start <= seg.CoreEnd
	}
	return c.Start < seg.CoreEnd
}

type OCRScanPoolController interface {
	ConfigureScanWorkers(context.Context, int) (int, error)
	ActiveScanWorkers() int
}

type OCRScanPoolResetter interface {
	ResetScanWorkers(context.Context, int) (int, error)
}

type parallelBenchmarkOutcome struct {
	images  int
	scanned float64
	err     error
}

type parallelBenchmarkMetrics struct {
	Throughput float64
	OCRImages  int
	Peak       autoResourceSnapshot
}

func collectParallelBenchmarkOutcomes(ctx context.Context, lanes int, out <-chan parallelBenchmarkOutcome) (int, float64, error) {
	ocrImages := 0
	scannedSeconds := 0.0
	for i := 0; i < lanes; i++ {
		select {
		case <-ctx.Done():
			return ocrImages, scannedSeconds, ctx.Err()
		case result := <-out:
			if result.err != nil {
				return ocrImages, scannedSeconds, result.err
			}
			ocrImages += result.images
			scannedSeconds += result.scanned
		}
	}
	return ocrImages, scannedSeconds, nil
}

func configureAutoWorkerLevel(ctx context.Context, pool OCRScanPoolController, level int, timeout time.Duration) (int, error) {
	if timeout <= 0 {
		timeout = scanAutoWorkerScaleTimeout
	}
	levelCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	return pool.ConfigureScanWorkers(levelCtx, level)
}

func restoreAutoWorkerPool(ctx context.Context, pool OCRScanPoolController, target int, timeout time.Duration) error {
	if timeout <= 0 {
		timeout = scanAutoPoolRestoreTimeout
	}
	restoreCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	actual, err := pool.ConfigureScanWorkers(restoreCtx, target)
	if err == nil && actual >= target {
		return nil
	}
	firstErr := err
	if firstErr == nil {
		firstErr = fmt.Errorf("thiếu capacity: %d/%d", actual, target)
	}
	resetter, ok := pool.(OCRScanPoolResetter)
	if !ok {
		return fmt.Errorf("khôi phục OCR worker pool về %d worker: %w", target, firstErr)
	}
	resetCtx, resetCancel := context.WithTimeout(ctx, scanAutoPoolResetTimeout)
	defer resetCancel()
	actual, err = resetter.ResetScanWorkers(resetCtx, target)
	if err != nil {
		return fmt.Errorf("khôi phục OCR worker pool về %d worker thất bại (%v); hard reset cũng lỗi: %w", target, firstErr, err)
	}
	if actual < target {
		return fmt.Errorf("hard reset OCR worker pool thiếu capacity: %d/%d", actual, target)
	}
	return nil
}

type parallelLaneOutcome struct {
	Index  int
	Result ScanResult
	Err    error
}

func (s *Scanner) runParallel(ctx context.Context, job *jobs.Job, req ScanRequest, requested int) (ScanResult, error) {
	if req.Duration <= 0 || math.IsNaN(req.Duration) || math.IsInf(req.Duration, 0) {
		return ScanResult{}, errors.New("quét song song cần thời lượng video hợp lệ")
	}
	checkpointSession, saved, resumed, err := newParallelCheckpointSession(s.CheckpointDir, req)
	if err != nil {
		return ScanResult{}, fmt.Errorf("chuẩn bị checkpoint OCR song song: %w", err)
	}

	var segments []scanSegment
	selected := requested
	parallelCP := saved
	if resumed {
		selected = saved.SelectedParallelism
		segments = make([]scanSegment, len(saved.Lanes))
		for i := range saved.Lanes {
			segments[i] = saved.Lanes[i].Segment
		}
		if job != nil {
			job.Logf("Resume checkpoint schema 4: giữ %d luồng đã chọn trước đó", selected)
		}
	} else {
		segments, err = buildScanSegments(req.Duration, requested, scanDefaultOverlapSeconds)
		if err != nil {
			return ScanResult{}, err
		}
		selected = len(segments)
		parallelCP = scanParallelCheckpoint{
			Schema: parallelCheckpointSchema, RequestedParallelism: req.Parallelism,
			SelectedParallelism: selected, Duration: req.Duration, Overlap: scanDefaultOverlapSeconds,
			Lanes: make([]scanParallelLaneCheckpoint, selected),
		}
		for i, seg := range segments {
			parallelCP.Lanes[i] = scanParallelLaneCheckpoint{ID: fmt.Sprintf("lane-%03d", i), Segment: seg, Media: seg.ScanStart}
		}
	}
	if selected < 1 || len(segments) != selected || len(parallelCP.Lanes) != selected {
		return ScanResult{}, errors.New("topology checkpoint OCR song song không hợp lệ")
	}

	if pool, ok := s.Engine.(OCRScanPoolController); ok {
		actual, poolErr := pool.ConfigureScanWorkers(ctx, selected)
		if poolErr != nil {
			return ScanResult{}, fmt.Errorf("chuẩn bị pool OCR %d worker: %w", selected, poolErr)
		}
		if actual < 1 {
			return ScanResult{}, errors.New("OCR worker pool không có worker sẵn sàng")
		}
		defer func() {
			shrinkCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
			defer cancel()
			_, _ = pool.ConfigureScanWorkers(shrinkCtx, 1)
		}()
	}

	laneCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	started := time.Now()
	outcomes := make(chan parallelLaneOutcome, selected)
	progress := make([]scanLaneProgress, selected)
	completed := make([]bool, selected)
	laneResults := make([]ScanResult, selected)
	var progressMu sync.Mutex
	var checkpointMu sync.Mutex
	lastCheckpointWrite := time.Time{}

	writeParallelCheckpoint := func(force bool) error {
		if checkpointSession == nil {
			return nil
		}
		checkpointMu.Lock()
		defer checkpointMu.Unlock()
		if !force && !lastCheckpointWrite.IsZero() && time.Since(lastCheckpointWrite) < 5*time.Second {
			return nil
		}
		if err := checkpointSession.Save(parallelCP); err != nil {
			return err
		}
		lastCheckpointWrite = time.Now()
		return nil
	}

	for i, lane := range parallelCP.Lanes {
		completed[i] = lane.Completed
		progress[i] = scanLaneProgress{
			MediaSeconds: lane.Media, Cues: append([]Cue(nil), lane.Cues...), Active: lane.Active, Frames: lane.Frames,
			OCRImages: lane.Stats.OCRImages, OCRCalls: lane.Stats.OCRBatchCalls, VisualSkips: lane.Stats.VisualSkips,
			VisualConfirmations: lane.Stats.VisualConfirmations, OCRRetries: lane.Stats.OCRRetries,
			FramePipelineSeconds: lane.Stats.FramePipelineSeconds, VisualSeconds: lane.Stats.VisualSeconds,
			EncodeSeconds: lane.Stats.EncodeSeconds, OCRSeconds: lane.Stats.OCRSeconds,
		}
		if lane.Completed {
			laneResults[i] = scanResultFromParallelCheckpointLane(lane)
		}
	}

	publish := func() {
		if job == nil {
			return
		}
		progressMu.Lock()
		defer progressMu.Unlock()
		unique := 0.0
		frames := 0
		ocrImages := 0
		ocrCalls := 0
		visualSkips := 0
		visualConfirmations := 0
		ocrRetries := 0
		framePipelineSeconds := 0.0
		visualSeconds := 0.0
		encodeSeconds := 0.0
		ocrSeconds := 0.0
		active := 0
		decoderNVDEC := true
		var liveCues [][]Cue
		for i, seg := range segments {
			p := progress[i]
			coreMedia := math.Min(seg.CoreEnd, math.Max(seg.CoreStart, p.MediaSeconds))
			if completed[i] {
				coreMedia = seg.CoreEnd
			}
			unique += math.Max(0, coreMedia-seg.CoreStart)
			frames += p.Frames
			ocrImages += p.OCRImages
			ocrCalls += p.OCRCalls
			visualSkips += p.VisualSkips
			visualConfirmations += p.VisualConfirmations
			ocrRetries += p.OCRRetries
			framePipelineSeconds += p.FramePipelineSeconds
			visualSeconds += p.VisualSeconds
			encodeSeconds += p.EncodeSeconds
			ocrSeconds += p.OCRSeconds
			if !completed[i] {
				active++
			}
			if p.Decoder.Mode != "nvdec" && p.MediaSeconds > seg.ScanStart {
				decoderNVDEC = false
			}
			cues := append([]Cue(nil), p.Cues...)
			if p.Active != nil {
				cues = append(cues, *p.Active)
			}
			liveCues = append(liveCues, cues)
		}
		elapsed := time.Since(started).Seconds()
		speed := 0.0
		if elapsed > 0 {
			speed = unique / elapsed
		}
		pct := math.Min(99.5, math.Max(0, unique/req.Duration*100))
		decoder := "software"
		if decoderNVDEC {
			decoder = "nvdec"
		}
		cues, boundaryMerges := reconcileSegmentCues(liveCues, segments, req.Duration)
		frontier := contiguousCompletedFrontier(progress, completed, segments)
		displayCues := recentCuesAtOrBefore(cues, frontier, 120)
		lastText := ""
		lastConf := 0.0
		if len(displayCues) > 0 {
			last := displayCues[len(displayCues)-1]
			lastText = last.Text
			lastConf = last.Conf
		}
		ocrCallsPerCue := 0.0
		if len(cues) > 0 {
			ocrCallsPerCue = float64(ocrImages) / float64(len(cues))
		}
		averageBatch := 0.0
		if ocrCalls > 0 {
			averageBatch = float64(ocrImages) / float64(ocrCalls)
		}
		msg := fmt.Sprintf("Đang quét song song %d luồng · %.1f× realtime · %d/%d lane hoạt động · %d câu · %d ảnh OCR", selected, speed, active, selected, len(cues), ocrImages)
		job.Set("ocr-scan", pct, msg)
		job.SetResult(map[string]any{
			"recent_cues": displayCues, "cue_count": len(cues), "frames": frames, "ocr_images": ocrImages, "ocr_calls": ocrImages,
			"ocr_batch_calls": ocrCalls, "average_batch": averageBatch, "parallelism_selected": selected, "total_lanes": selected, "active_lanes": active,
			"completed_lanes": selected - active, "boundary_merges": boundaryMerges, "realtime_speed": speed,
			"elapsed_seconds": elapsed, "media_seconds": frontier, "progress_percent": pct, "visual_skips": visualSkips, "visual_confirmations": visualConfirmations,
			"ocr_retries": ocrRetries, "ocr_calls_per_cue": ocrCallsPerCue, "frame_pipeline_seconds": framePipelineSeconds,
			"visual_seconds": visualSeconds, "encode_seconds": encodeSeconds, "ocr_seconds": ocrSeconds, "decoder": decoder,
			"last_text": lastText, "last_confidence": lastConf,
		})
	}

	launched := 0
	for i, seg := range segments {
		if completed[i] {
			continue
		}
		i, seg := i, seg
		launched++
		go func() {
			laneReq := req
			laneReq.Batch = "1"
			laneReq.Parallelism = "1"
			var resumeState *scanLaneState
			checkpointMu.Lock()
			if parallelCP.Lanes[i].Media > seg.ScanStart || len(parallelCP.Lanes[i].Cues) > 0 || parallelCP.Lanes[i].Active != nil {
				state := laneCheckpointState(parallelCP.Lanes[i])
				resumeState = &state
			}
			checkpointMu.Unlock()
			result, laneErr := s.run(laneCtx, nil, laneReq, "", scanRunOptions{
				StartAt: seg.ScanStart, EndAt: seg.ScanEnd, DisableCheckpoint: true, ForceBatchOne: true, Resume: resumeState,
				PauseRequested: func() bool { return job != nil && job.PauseRequested() },
				OnSafeState: func(state scanLaneState) {
					checkpointMu.Lock()
					updateLaneCheckpointState(&parallelCP.Lanes[i], state)
					shouldWrite := lastCheckpointWrite.IsZero() || time.Since(lastCheckpointWrite) >= 5*time.Second
					if shouldWrite && checkpointSession != nil {
						if saveErr := checkpointSession.Save(parallelCP); saveErr == nil {
							lastCheckpointWrite = time.Now()
						} else if job != nil {
							job.Logf("Checkpoint lane %d chưa ghi được: %v", i, saveErr)
						}
					}
					checkpointMu.Unlock()
				},
				OnProgress: func(p scanLaneProgress) {
					progressMu.Lock()
					progress[i] = p
					progressMu.Unlock()
					publish()
				},
			})
			outcomes <- parallelLaneOutcome{Index: i, Result: result, Err: laneErr}
		}()
	}

	paused := false
	var firstErr error
	for n := 0; n < launched; n++ {
		out := <-outcomes
		laneResults[out.Index] = out.Result
		progressMu.Lock()
		completed[out.Index] = out.Err == nil
		if out.Result.MediaSeconds > 0 {
			progress[out.Index].MediaSeconds = out.Result.MediaSeconds
			progress[out.Index].Cues = out.Result.Cues
			progress[out.Index].Frames = out.Result.Frames
			progress[out.Index].OCRImages = out.Result.OCRImages
			progress[out.Index].OCRCalls = out.Result.OCRBatchCalls
			progress[out.Index].VisualSkips = out.Result.VisualSkips
			progress[out.Index].Decoder = scanDecoderDecision{Mode: out.Result.Decoder, FallbackReason: out.Result.DecoderFallback}
		}
		progressMu.Unlock()

		checkpointMu.Lock()
		lane := &parallelCP.Lanes[out.Index]
		if out.Err == nil {
			lane.Completed = true
			lane.Media = lane.Segment.ScanEnd
			lane.Cues = append([]Cue(nil), out.Result.Cues...)
			lane.Active = nil
			lane.Frames = out.Result.Frames
			lane.Stats = checkpointStatsFromScanResult(out.Result)
		}
		checkpointMu.Unlock()

		if errors.Is(out.Err, ErrScanPaused) {
			paused = true
		} else if out.Err != nil && firstErr == nil {
			firstErr = out.Err
			cancel()
		}
		publish()
	}
	if firstErr != nil {
		_ = writeParallelCheckpoint(true)
		return ScanResult{}, firstErr
	}

	result := aggregateParallelScanResult(laneResults, parallelCP, segments, req.Duration, time.Since(started).Seconds())
	if paused {
		// Barrier guarantee: every non-completed lane returned ErrScanPaused only
		// after its tracker reached CanCheckpoint and OnSafeState captured that
		// state. Only now is the global schema-4 checkpoint fsynced.
		if err := writeParallelCheckpoint(true); err != nil {
			return ScanResult{}, fmt.Errorf("lưu checkpoint OCR song song khi tạm dừng: %w", err)
		}
		progressMu.Lock()
		result.MediaSeconds = contiguousCompletedFrontier(progress, completed, segments)
		progressMu.Unlock()
		result.CompletedLanes = 0
		for _, done := range completed {
			if done {
				result.CompletedLanes++
			}
		}
		result.ActiveLanes = selected - result.CompletedLanes
		return result, ErrScanPaused
	}
	if err := checkpointSession.Remove(); err != nil {
		return ScanResult{}, fmt.Errorf("xóa checkpoint OCR song song sau khi hoàn tất: %w", err)
	}
	if job != nil {
		job.SetResult(result)
		job.Set("ocr-scan", 100, fmt.Sprintf("Đã quét xong · %d luồng · %d câu · %d ảnh OCR · %.1f× realtime", selected, len(result.Cues), result.OCRImages, result.RealtimeSpeed))
	}
	return result, nil
}

func checkpointStatsFromScanResult(r ScanResult) scanCheckpointStats {
	return scanCheckpointStats{
		OCRImages: r.OCRImages, OCRBatchCalls: r.OCRBatchCalls, VisualSkips: r.VisualSkips,
		VisualConfirmations: r.VisualConfirmations, OCRRetries: r.OCRRetries,
		FramePipelineSeconds: r.FramePipelineSeconds, VisualSeconds: r.VisualSeconds,
		EncodeSeconds: r.EncodeSeconds, OCRSeconds: r.OCRSeconds,
	}
}

func scanResultFromParallelCheckpointLane(l scanParallelLaneCheckpoint) ScanResult {
	return ScanResult{
		Cues: append([]Cue(nil), l.Cues...), Frames: l.Frames, OCRCalls: l.Stats.OCRImages, OCRImages: l.Stats.OCRImages,
		OCRBatchCalls: l.Stats.OCRBatchCalls, VisualSkips: l.Stats.VisualSkips, VisualConfirmations: l.Stats.VisualConfirmations,
		OCRRetries: l.Stats.OCRRetries, FramePipelineSeconds: l.Stats.FramePipelineSeconds, VisualSeconds: l.Stats.VisualSeconds,
		EncodeSeconds: l.Stats.EncodeSeconds, OCRSeconds: l.Stats.OCRSeconds, MediaSeconds: l.Media,
	}
}

func aggregateParallelScanResult(laneResults []ScanResult, cp scanParallelCheckpoint, segments []scanSegment, duration, elapsed float64) ScanResult {
	selected := len(segments)
	laneCues := make([][]Cue, selected)
	result := ScanResult{ParallelismSelected: selected, ActiveLanes: 0, CompletedLanes: selected, MediaSeconds: duration, ElapsedSeconds: elapsed}
	allNVDEC := true
	for i := 0; i < selected; i++ {
		lr := laneResults[i]
		if i < len(cp.Lanes) && len(lr.Cues) == 0 && (len(cp.Lanes[i].Cues) > 0 || cp.Lanes[i].Active != nil) {
			lr = scanResultFromParallelCheckpointLane(cp.Lanes[i])
		}
		cues := append([]Cue(nil), lr.Cues...)
		if i < len(cp.Lanes) && cp.Lanes[i].Active != nil && !cp.Lanes[i].Completed {
			cues = append(cues, *cp.Lanes[i].Active)
		}
		laneCues[i] = cues
		result.Frames += lr.Frames
		result.OCRCalls += lr.OCRCalls
		result.OCRImages += lr.OCRImages
		result.OCRBatchCalls += lr.OCRBatchCalls
		result.VisualSkips += lr.VisualSkips
		result.VisualConfirmations += lr.VisualConfirmations
		result.OCRRetries += lr.OCRRetries
		result.FramePipelineSeconds += lr.FramePipelineSeconds
		result.VisualSeconds += lr.VisualSeconds
		result.EncodeSeconds += lr.EncodeSeconds
		result.OCRSeconds += lr.OCRSeconds
		if lr.Decoder != "" && lr.Decoder != "nvdec" {
			allNVDEC = false
		}
	}
	result.Cues, result.BoundaryMerges = reconcileSegmentCues(laneCues, segments, duration)
	if elapsed > 0 {
		result.RealtimeSpeed = duration / elapsed
	}
	if len(result.Cues) > 0 {
		result.OCRCallsPerCue = float64(result.OCRImages) / float64(len(result.Cues))
	}
	if result.OCRBatchCalls > 0 {
		result.AverageBatch = float64(result.OCRImages) / float64(result.OCRBatchCalls)
	}
	if allNVDEC {
		result.Decoder = "nvdec"
	} else {
		result.Decoder = "software"
	}
	return result
}

func contiguousCompletedFrontier(progress []scanLaneProgress, completed []bool, segments []scanSegment) float64 {
	frontier := 0.0
	for i, seg := range segments {
		if completed[i] {
			frontier = seg.CoreEnd
			continue
		}
		media := math.Min(seg.CoreEnd, math.Max(seg.CoreStart, progress[i].MediaSeconds))
		if media > frontier {
			frontier = media
		}
		break
	}
	return frontier
}

func recentCues(cues []Cue, max int) []Cue {
	if max <= 0 || len(cues) <= max {
		return append([]Cue(nil), cues...)
	}
	return append([]Cue(nil), cues[len(cues)-max:]...)
}

func recentCuesAtOrBefore(cues []Cue, frontier float64, max int) []Cue {
	if frontier < 0 {
		frontier = 0
	}
	eligible := make([]Cue, 0, len(cues))
	for _, cue := range cues {
		if cue.Start <= frontier+1e-6 {
			eligible = append(eligible, cue)
		}
	}
	return recentCues(eligible, max)
}

func reconcileSegmentCues(laneCues [][]Cue, segments []scanSegment, duration float64) ([]Cue, int) {
	var cues []Cue
	for i, seg := range segments {
		if i >= len(laneCues) {
			break
		}
		last := i == len(segments)-1
		for _, c := range laneCues[i] {
			if cueOwnedBySegment(c, seg, last) {
				cues = append(cues, c)
			}
		}
	}
	sort.SliceStable(cues, func(i, j int) bool {
		if cues[i].Start != cues[j].Start {
			return cues[i].Start < cues[j].Start
		}
		if cues[i].End != cues[j].End {
			return cues[i].End < cues[j].End
		}
		return cues[i].Text < cues[j].Text
	})
	merged := make([]Cue, 0, len(cues))
	mergeCount := 0
	for _, c := range cues {
		if c.Start < 0 {
			c.Start = 0
		}
		if duration > 0 && c.End > duration {
			c.End = duration
		}
		if c.End < c.Start || strings.TrimSpace(c.Text) == "" {
			continue
		}
		// Final-output guard for cues restored from older schema-4 checkpoints
		// or produced by any legacy path before the strict Chinese validator
		// existed. The final SRT must not contain foreign-script OCR garbage.
		text, ok := NormalizeChineseSubtitleText(c.Text)
		if !ok {
			continue
		}
		c.Text = text
		if len(merged) > 0 {
			prev := &merged[len(merged)-1]
			near := c.Start <= prev.End+0.6 && c.End >= prev.Start-0.6
			if near && scanSimilarity(prev.Text, c.Text) >= 0.92 {
				if c.Start < prev.Start {
					prev.Start = c.Start
				}
				if c.End > prev.End {
					prev.End = c.End
				}
				if c.Conf > prev.Conf {
					prev.Conf = c.Conf
				}
				mergeCount++
				continue
			}
		}
		merged = append(merged, c)
	}
	return merged, mergeCount
}

func (s *Scanner) selectAutoParallelism(ctx context.Context, job *jobs.Job, req ScanRequest) (int, []int, string, time.Duration, error) {
	maxLanes := maxParallelismForDuration(req.Duration)
	levels := []int{1, 2, 4, 8, 16}
	best := 1
	bestThroughput := 0.0
	var tested []int
	var total time.Duration
	stopReason := "max_safe_level"
	pool, hasPool := s.Engine.(OCRScanPoolController)
	probe := s.autoResourceProbe()
	baseline := probe(ctx)
	lastPeak := baseline
	lastLevel := 0

	for _, level := range levels {
		if level > maxLanes {
			stopReason = "duration_cap"
			break
		}
		if lastLevel > 0 {
			if job != nil {
				job.Set("ocr-calibrate", 0.5, fmt.Sprintf("Đang kiểm tra khả năng chạy %d luồng OCR...", level))
			}
			decision := evaluateAutoResourceGate(baseline, lastPeak, lastLevel, level)
			if !decision.Allow {
				stopReason = decision.Reason
				if job != nil {
					job.Set("ocr-calibrate", 0.5, fmt.Sprintf("%d luồng vượt giới hạn an toàn · giữ %d luồng", level, best))
					job.Logf("Auto resource gate chặn %d→%d: %s (%s)", lastLevel, level, decision.Reason, decision.Detail)
				}
				break
			}
			if job != nil {
				job.Logf("Auto resource gate cho phép %d→%d: %s", lastLevel, level, decision.Detail)
			}
		}
		if hasPool {
			actual, err := configureAutoWorkerLevel(ctx, pool, level, scanAutoWorkerScaleTimeout)
			if err != nil {
				if len(tested) == 0 {
					return 0, tested, "worker_start_failed", total, err
				}
				stopReason = "worker_start_failed"
				if job != nil {
					job.Logf("Auto dừng ở %d: không mở được %d OCR worker: %v", best, level, err)
				}
				break
			}
			if actual < level {
				stopReason = "worker_capacity"
				break
			}
		}
		if job != nil {
			job.Set("ocr-calibrate", 0.5, fmt.Sprintf("Đang đo %d luồng quét OCR trên video thật...", level))
		}
		started := time.Now()
		metrics, err := s.benchmarkParallelLevel(ctx, req, level)
		elapsed := time.Since(started)
		total += elapsed
		if err != nil {
			if len(tested) == 0 {
				reason := "benchmark_failed"
				if errors.Is(err, context.DeadlineExceeded) {
					reason = "benchmark_timeout"
				}
				return 0, tested, reason, total, err
			}
			stopReason = "benchmark_failed"
			if errors.Is(err, context.DeadlineExceeded) {
				stopReason = "benchmark_timeout"
				if job != nil {
					job.Set("ocr-calibrate", 0.5, fmt.Sprintf("%d luồng không phản hồi kịp · quay về %d luồng ổn định...", level, best))
					job.Logf("Auto timeout %d luồng sau %.3fs; fallback %d", level, elapsed.Seconds(), best)
				}
			}
			break
		}
		tested = append(tested, level)
		lastLevel = level
		lastPeak = metrics.Peak
		if job != nil {
			job.Logf("Auto benchmark %d luồng: %.2f× end-to-end trong %.3fs · %s", level, metrics.Throughput, elapsed.Seconds(), formatAutoResourceSnapshot(metrics.Peak))
		}
		if level == 1 {
			best = 1
			bestThroughput = metrics.Throughput
			continue
		}
		gain := 0.0
		if bestThroughput > 0 {
			gain = metrics.Throughput/bestThroughput - 1
		}
		if metrics.Throughput > bestThroughput && gain >= scanAutoMinThroughputGain {
			best = level
			bestThroughput = metrics.Throughput
			continue
		}
		stopReason = "gain_below_threshold"
		if job != nil {
			job.Set("ocr-calibrate", 0.5, fmt.Sprintf("%d luồng chỉ tăng %.1f%% · giữ %d luồng", level, gain*100, best))
		}
		break
	}
	if hasPool {
		if err := restoreAutoWorkerPool(ctx, pool, best, scanAutoPoolRestoreTimeout); err != nil {
			return 0, tested, "worker_restore_failed", total, err
		}
	}
	if job != nil {
		job.Logf("Auto chọn %d luồng · tested=%v · stop=%s", best, tested, stopReason)
	}
	return best, tested, stopReason, total, nil
}

func (s *Scanner) benchmarkParallelLevel(ctx context.Context, req ScanRequest, lanes int) (parallelBenchmarkMetrics, error) {
	if lanes < 1 {
		return parallelBenchmarkMetrics{}, errors.New("benchmark parallelism không hợp lệ")
	}
	window := 4.0
	if req.Duration < float64(lanes)*window*2 {
		window = math.Max(1, req.Duration/float64(lanes*2))
	}
	started := time.Now()
	out := make(chan parallelBenchmarkOutcome, lanes)
	benchCtx, cancel := context.WithTimeout(ctx, scanAutoBenchmarkLevelTimeout)
	defer cancel()
	stopSampler := startAutoResourceSampler(benchCtx, s.autoResourceProbe())
	for i := 0; i < lanes; i++ {
		center := req.Duration * (float64(i) + 0.5) / float64(lanes)
		start := math.Max(0, math.Min(req.Duration-window, center-window/2))
		end := math.Min(req.Duration, start+window)
		go func(start, end float64) {
			laneReq := req
			laneReq.Batch = "1"
			laneReq.Parallelism = "1"
			result, err := s.run(benchCtx, nil, laneReq, "", scanRunOptions{StartAt: start, EndAt: end, DisableCheckpoint: true, ForceBatchOne: true})
			scanned := math.Max(0, math.Min(end, result.MediaSeconds)-start)
			outcome := parallelBenchmarkOutcome{images: result.OCRImages, scanned: scanned, err: err}
			select {
			case out <- outcome:
			case <-benchCtx.Done():
			}
		}(start, end)
	}
	ocrImages, scannedSeconds, err := collectParallelBenchmarkOutcomes(benchCtx, lanes, out)
	peak := stopSampler()
	if err != nil {
		cancel()
		return parallelBenchmarkMetrics{OCRImages: ocrImages, Peak: peak}, err
	}
	elapsed := time.Since(started).Seconds()
	if elapsed <= 0 || scannedSeconds <= 0 {
		return parallelBenchmarkMetrics{OCRImages: ocrImages, Peak: peak}, errors.New("benchmark parallelism không tạo được đủ tiến độ video để đo")
	}
	return parallelBenchmarkMetrics{Throughput: scannedSeconds / elapsed, OCRImages: ocrImages, Peak: peak}, nil
}

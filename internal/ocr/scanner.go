package ocr

import (
	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/proc"
	"bytes"
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"image"
	"image/png"
	"io"
	"math"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	scanWidth  = 1280
	scanHeight = 320
)

var ErrScanPaused = errors.New("OCR scan paused")

type OCRRunner interface {
	Run(context.Context, string) (Result, error)
}

type OCRParallelRunner interface {
	OCRRunner
	Parallelism() int
}

type OCRBatchRunner interface {
	OCRRunner
	BatchCapable() bool
	RunBatch(context.Context, []string) ([]Result, error)
}

type ScanRegion struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	W float64 `json:"w"`
	H float64 `json:"h"`
}

type ScanRequest struct {
	Path        string     `json:"path"`
	Region      ScanRegion `json:"region"`
	Mode        string     `json:"mode"`
	Device      string     `json:"device,omitempty"`
	Batch       string     `json:"batch,omitempty"` // Legacy RC12 request micro-batch; RC13 UI no longer sends this.
	Parallelism string     `json:"parallelism,omitempty"`
	Sensitivity float64    `json:"sensitivity"`
	Duration    float64    `json:"duration"`
}

type Cue struct {
	Start float64 `json:"start"`
	End   float64 `json:"end"`
	Text  string  `json:"text"`
	Conf  float64 `json:"conf"`
}

type ScanResult struct {
	Cues                  []Cue   `json:"cues"`
	Frames                int     `json:"frames"`
	OCRCalls              int     `json:"ocr_calls"`
	OCRImages             int     `json:"ocr_images"`
	OCRBatchCalls         int     `json:"ocr_batch_calls"`
	BatchSelected         int     `json:"batch_selected"`
	AverageBatch          float64 `json:"average_batch"`
	BatchBenchmarkSeconds float64 `json:"batch_benchmark_seconds"`
	VisualSkips           int     `json:"visual_skips"`
	VisualConfirmations   int     `json:"visual_confirmations"`
	OCRRetries            int     `json:"ocr_retries"`
	Decoder               string  `json:"decoder"`
	DecoderFallback       string  `json:"decoder_fallback,omitempty"`
	FramePipelineSeconds  float64 `json:"frame_pipeline_seconds"`
	VisualSeconds         float64 `json:"visual_seconds"`
	EncodeSeconds         float64 `json:"encode_seconds"`
	OCRSeconds            float64 `json:"ocr_seconds"`
	OCRCallsPerCue        float64 `json:"ocr_calls_per_cue"`
	MediaSeconds          float64 `json:"media_seconds"`
	ElapsedSeconds        float64 `json:"elapsed_seconds"`
	RealtimeSpeed         float64 `json:"realtime_speed"`
	ParallelismSelected   int     `json:"parallelism_selected,omitempty"`
	ActiveLanes           int     `json:"active_lanes,omitempty"`
	CompletedLanes        int     `json:"completed_lanes,omitempty"`
	BoundaryMerges        int     `json:"boundary_merges,omitempty"`
	AutoTested            []int   `json:"auto_tested,omitempty"`
	AutoStopReason        string  `json:"auto_stop_reason,omitempty"`
	AutoBenchmarkSeconds  float64 `json:"auto_benchmark_seconds,omitempty"`
}

type Scanner struct {
	FFmpeg                    string
	Engine                    OCRRunner
	CheckpointDir             string
	CheckpointIntervalSeconds float64
	resourceProbe             autoResourceProbe
}

type scanOCRCandidate struct {
	At             float64
	FrameIndex     int
	Changed        bool
	Activity       float64
	Image          string
	RetryRGB       []byte
	Result         Result
	Calls          int
	BatchCalls     int
	Retries        int
	EncodeDuration time.Duration
	OCRDuration    time.Duration
	Err            error
}

type scanVisualFrame struct {
	At          float64
	FrameIndex  int
	Stable      bool
	BlankStable bool
}

type scanBatchEvent struct {
	Candidate *scanOCRCandidate
	Visual    *scanVisualFrame
}

type scanEvaluatedFrame struct {
	At          float64
	FrameIndex  int
	RGB         []byte
	Changed     bool
	Activity    float64
	Stable      bool
	BlankStable bool
}

type scanMode struct {
	FPS                float64
	GuardSeconds       float64
	ActiveGuardSeconds float64
	DiffTrigger        float64
	LowConf            float64
}

type scanDecoderDecision struct {
	Mode           string
	FallbackReason string
}

const scanBatchMaxWait = 25 * time.Millisecond

func normalizeScanBatch(mode string) (string, error) {
	mode = strings.ToLower(strings.TrimSpace(mode))
	if mode == "" {
		return "auto", nil
	}
	switch mode {
	case "auto", "1", "2", "4":
		return mode, nil
	default:
		return "", fmt.Errorf("batch OCR không hợp lệ: %s", mode)
	}
}

func explicitScanBatchSize(mode string) int {
	switch mode {
	case "2":
		return 2
	case "4":
		return 4
	default:
		return 1
	}
}

func ocrBatchCapable(engine OCRRunner) bool {
	runner, ok := engine.(OCRBatchRunner)
	return ok && runner.BatchCapable()
}

func benchmarkScanBatch(ctx context.Context, engine OCRRunner, image string) (int, time.Duration, error) {
	runner, ok := engine.(OCRBatchRunner)
	if !ok || !runner.BatchCapable() {
		return 1, 0, nil
	}
	bestSize := 1
	bestPerImage := time.Duration(1<<63 - 1)
	var total time.Duration
	for _, size := range []int{1, 2, 4} {
		images := make([]string, size)
		for i := range images {
			images[i] = image
		}
		started := time.Now()
		results, err := runner.RunBatch(ctx, images)
		elapsed := time.Since(started)
		total += elapsed
		if err != nil || len(results) != size {
			if size == 1 {
				if err == nil {
					err = fmt.Errorf("benchmark batch 1 trả %d kết quả", len(results))
				}
				return 1, total, err
			}
			continue
		}
		perImage := elapsed / time.Duration(size)
		if perImage < bestPerImage {
			bestPerImage = perImage
			bestSize = size
		}
	}
	return bestSize, total, nil
}

func scanModeFor(name string, sensitivity float64) scanMode {
	var m scanMode
	switch strings.ToLower(strings.TrimSpace(name)) {
	case "accurate", "precise", "chinh-xac":
		m = scanMode{FPS: 4, GuardSeconds: 3.0, ActiveGuardSeconds: 12.0, DiffTrigger: 0.10, LowConf: 0.68}
	case "fast", "nhanh":
		m = scanMode{FPS: 1.5, GuardSeconds: 8.0, ActiveGuardSeconds: 24.0, DiffTrigger: 0.22, LowConf: 0.58}
	default:
		m = scanMode{FPS: 2.5, GuardSeconds: 5.0, ActiveGuardSeconds: 16.0, DiffTrigger: 0.16, LowConf: 0.62}
	}
	if sensitivity <= 0 {
		sensitivity = 1
	}
	// Lower sensitivity value means more sensitive. Clamp so a malformed UI
	// value cannot turn every frame into an OCR request or suppress all changes.
	if sensitivity < 0.60 {
		sensitivity = 0.60
	}
	if sensitivity > 1.50 {
		sensitivity = 1.50
	}
	m.DiffTrigger *= sensitivity
	return m
}

func scanFilter(reg ScanRegion, mode scanMode, nvdec bool) string {
	base := fmt.Sprintf(
		"crop=iw*%.8f:ih*%.8f:iw*%.8f:ih*%.8f,scale=%d:%d:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=%d:%d:(ow-iw)/2:(oh-ih)/2:black,format=rgb24",
		reg.W, reg.H, reg.X, reg.Y, scanWidth, scanHeight, scanWidth, scanHeight,
	)
	if nvdec {
		// fps is intentionally before hwdownload. It can discard hardware-frame
		// references without touching their pixels, so only sampled frames cross
		// PCIe back to system memory for the CPU ROI filters and visual scanner.
		return fmt.Sprintf("fps=%.6g,hwdownload,format=nv12|p010le|p016le,%s", mode.FPS, base)
	}
	return fmt.Sprintf("fps=%.6g,%s", mode.FPS, base)
}

func scanFFmpegArgs(path string, resumeAt float64, filter string, nvdec bool) []string {
	return scanFFmpegArgsRange(path, resumeAt, 0, filter, nvdec)
}

func scanFFmpegArgsRange(path string, startAt, endAt float64, filter string, nvdec bool) []string {
	args := []string{"-hide_banner", "-loglevel", "error", "-nostdin"}
	if nvdec {
		args = append(args, "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", "0")
	}
	if startAt > 0 {
		args = append(args, "-ss", fmt.Sprintf("%.3f", startAt))
	}
	args = append(args, "-i", path)
	if endAt > startAt {
		args = append(args, "-t", fmt.Sprintf("%.3f", endAt-startAt))
	}
	return append(args,
		"-map", "0:v:0", "-an", "-sn", "-dn",
		"-vf", filter,
		"-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
	)
}

func compactFFmpegError(s string) string {
	s = strings.Join(strings.Fields(strings.TrimSpace(s)), " ")
	if len(s) > 220 {
		s = s[:220] + "…"
	}
	return s
}

func probeNVDEC(ctx context.Context, ffmpeg, path string, resumeAt float64, reg ScanRegion, mode scanMode) scanDecoderDecision {
	probeCtx, cancel := context.WithTimeout(ctx, 12*time.Second)
	defer cancel()
	args := []string{"-hide_banner", "-loglevel", "error", "-nostdin", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", "0"}
	if resumeAt > 0 {
		args = append(args, "-ss", fmt.Sprintf("%.3f", resumeAt))
	}
	args = append(args,
		"-i", path,
		"-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
		"-vf", scanFilter(reg, mode, true),
		"-f", "null", "-",
	)
	cmd := proc.Hide(exec.CommandContext(probeCtx, ffmpeg, args...))
	var stderr bytes.Buffer
	cmd.Stdout = io.Discard
	cmd.Stderr = &stderr
	err := cmd.Run()
	if err == nil {
		return scanDecoderDecision{Mode: "nvdec"}
	}
	reason := compactFFmpegError(stderr.String())
	if reason == "" {
		if errors.Is(probeCtx.Err(), context.DeadlineExceeded) {
			reason = "NVDEC probe timeout"
		} else {
			reason = err.Error()
		}
	}
	return scanDecoderDecision{Mode: "software", FallbackReason: reason}
}

type scanFramePacket struct {
	RGB  []byte
	Wait time.Duration
	Err  error
}

type scanFrameReader struct {
	ch <-chan scanFramePacket
}

func newScanFrameReader(ctx context.Context, r io.Reader, frameSize, depth int) *scanFrameReader {
	if depth < 1 {
		depth = 1
	}
	ch := make(chan scanFramePacket, depth)
	go func() {
		defer close(ch)
		buf := make([]byte, frameSize)
		for {
			started := time.Now()
			_, err := io.ReadFull(r, buf)
			packet := scanFramePacket{Wait: time.Since(started), Err: err}
			if err == nil {
				packet.RGB = append([]byte(nil), buf...)
			}
			select {
			case <-ctx.Done():
				return
			case ch <- packet:
			}
			if err != nil {
				return
			}
		}
	}()
	return &scanFrameReader{ch: ch}
}

func (r *scanFrameReader) next(ctx context.Context, timeout time.Duration) (scanFramePacket, bool) {
	if timeout <= 0 {
		select {
		case <-ctx.Done():
			return scanFramePacket{Err: ctx.Err()}, false
		case packet, ok := <-r.ch:
			if !ok {
				return scanFramePacket{Err: io.EOF}, false
			}
			return packet, false
		}
	}
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return scanFramePacket{Err: ctx.Err()}, false
	case <-timer.C:
		return scanFramePacket{}, true
	case packet, ok := <-r.ch:
		if !ok {
			return scanFramePacket{Err: io.EOF}, false
		}
		return packet, false
	}
}

func (s *Scanner) Run(ctx context.Context, job *jobs.Job, req ScanRequest) (ScanResult, error) {
	// Empty Parallelism is the legacy RC12/API contract and intentionally keeps
	// the schema-3 single-timeline path for old checkpoints. RC13 UI always
	// sends Parallelism, including "1", so every new scan uses schema 4.
	if strings.TrimSpace(req.Parallelism) == "" {
		return s.run(ctx, job, req, "")
	}
	legacyResume, err := legacyCheckpointAvailable(s.CheckpointDir, req)
	if err != nil {
		return ScanResult{}, fmt.Errorf("kiểm tra checkpoint OCR cũ: %w", err)
	}
	if legacyResume {
		legacyReq := req
		legacyReq.Parallelism = ""
		// Schema 3 did not persist RC12's request-level batch selector. Resume
		// conservatively as batch 1 so the legacy checkpoint can finish without
		// introducing a new calibration path into an already-started scan.
		legacyReq.Batch = "1"
		if job != nil {
			job.Logf("Phát hiện checkpoint RC12 schema 3; tiếp tục theo đường quét 1 luồng legacy để giữ tiến độ hiện có.")
		}
		return s.run(ctx, job, legacyReq, "")
	}
	parallelMode, err := normalizeScanParallelism(req.Parallelism)
	if err != nil {
		return ScanResult{}, err
	}
	selected := explicitScanParallelism(parallelMode)
	if parallelMode != "auto" {
		maxForVideo := maxParallelismForDuration(req.Duration)
		if selected > maxForVideo {
			return ScanResult{}, fmt.Errorf("video %.0f giây quá ngắn cho %d luồng; tối đa %d luồng với cấu hình hiện tại", req.Duration, selected, maxForVideo)
		}
	}
	var tested []int
	var stopReason string
	var benchDuration time.Duration
	if parallelMode == "auto" {
		// Resume never recalibrates. The schema-4 topology is authoritative for
		// determinism and avoids repartitioning partially completed ranges.
		_, saved, resumed, cpErr := newParallelCheckpointSession(s.CheckpointDir, req)
		if cpErr != nil {
			return ScanResult{}, cpErr
		}
		if resumed {
			selected = saved.SelectedParallelism
			stopReason = "checkpoint_topology"
		} else {
			selected, tested, stopReason, benchDuration, err = s.selectAutoParallelism(ctx, job, req)
			if err != nil {
				return ScanResult{}, err
			}
		}
	}
	result, runErr := s.runParallel(ctx, job, req, selected)
	result.AutoTested = tested
	result.AutoStopReason = stopReason
	result.AutoBenchmarkSeconds = benchDuration.Seconds()
	if job != nil {
		job.SetResult(result)
	}
	return result, runErr
}

func (s *Scanner) run(ctx context.Context, job *jobs.Job, req ScanRequest, forceSoftwareReason string, option ...scanRunOptions) (ScanResult, error) {
	cfg := scanRunOptions{}
	if len(option) > 0 {
		cfg = option[0]
	}

	if s == nil || s.Engine == nil {
		return ScanResult{}, errors.New("OCR scanner chưa có engine")
	}
	ff := strings.TrimSpace(s.FFmpeg)
	if ff == "" {
		return ScanResult{}, errors.New("OCR scanner chưa có ffmpeg")
	}
	path := strings.TrimSpace(req.Path)
	if path == "" {
		return ScanResult{}, errors.New("thiếu video nguồn")
	}
	st, err := os.Stat(path)
	if err != nil || st.IsDir() || st.Size() == 0 {
		return ScanResult{}, errors.New("video nguồn không tồn tại hoặc rỗng")
	}
	reg, err := normalizeScanRegion(req.Region)
	if err != nil {
		return ScanResult{}, err
	}
	mode := scanModeFor(req.Mode, req.Sensitivity)
	tracker := newSubtitleTracker(mode)
	var checkpoint *scanCheckpointSession
	var saved scanCheckpoint
	resumed := false
	var checkpointErr error
	if !cfg.DisableCheckpoint {
		checkpoint, saved, resumed, checkpointErr = newScanCheckpointSession(s.CheckpointDir, s.CheckpointIntervalSeconds, req)
		if checkpointErr != nil {
			return ScanResult{}, fmt.Errorf("chuẩn bị checkpoint OCR: %w", checkpointErr)
		}
	}
	resumeAt := math.Max(0, cfg.StartAt)
	baseFrames := 0
	ocrCalls := 0
	if cfg.Resume != nil {
		resumeAt = cfg.Resume.MediaSeconds
		baseFrames = cfg.Resume.Frames
		ocrCalls = cfg.Resume.Stats.OCRImages
		tracker.Restore(cfg.Resume.Cues, cfg.Resume.Active)
		resumed = true
	} else if resumed {
		resumeAt = saved.MediaSeconds
		baseFrames = saved.Frames
		ocrCalls = saved.OCRCalls
		tracker.Restore(saved.Cues, saved.Active)
	}
	decoder := scanDecoderDecision{Mode: "software", FallbackReason: forceSoftwareReason}
	if forceSoftwareReason == "" {
		decoder = probeNVDEC(ctx, ff, path, resumeAt, reg, mode)
	}
	filter := scanFilter(reg, mode, decoder.Mode == "nvdec")
	endAt := cfg.EndAt
	if endAt <= resumeAt && req.Duration > resumeAt {
		endAt = req.Duration
	}
	args := scanFFmpegArgsRange(path, resumeAt, endAt, filter, decoder.Mode == "nvdec")
	cmd := proc.Hide(exec.CommandContext(ctx, ff, args...))
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return ScanResult{}, err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return ScanResult{}, err
	}
	if err := cmd.Start(); err != nil {
		return ScanResult{}, fmt.Errorf("khởi động FFmpeg OCR scan: %w", err)
	}
	stderrCh := make(chan []byte, 1)
	go func() {
		b, _ := io.ReadAll(io.LimitReader(stderr, 8<<20))
		stderrCh <- b
	}()

	startWall := time.Now()
	frameSize := scanWidth * scanHeight * 3
	frameCtx, frameCancel := context.WithCancel(ctx)
	defer frameCancel()
	frameReader := newScanFrameReader(frameCtx, stdout, frameSize, 8)
	var prevSig edgeSignature
	lastOCRAt := resumeAt - 999.0
	frameIndex := 0
	forceNextOCR := false
	lastPublish := time.Time{}
	lastObservedText := ""
	lastObservedAt := 0.0
	lastObservedConf := 0.0
	visualSkips := 0
	visualConfirmations := 0
	ocrRetries := 0
	ocrImages := ocrCalls // checkpoint field keeps its historical "images processed" meaning.
	ocrBatchCalls := 0
	if cfg.Resume != nil {
		ocrImages = cfg.Resume.Stats.OCRImages
		ocrBatchCalls = cfg.Resume.Stats.OCRBatchCalls
		visualSkips = cfg.Resume.Stats.VisualSkips
		visualConfirmations = cfg.Resume.Stats.VisualConfirmations
		ocrRetries = cfg.Resume.Stats.OCRRetries
	} else if resumed {
		if saved.Stats.OCRImages > 0 || saved.OCRCalls == 0 {
			ocrImages = saved.Stats.OCRImages
		}
		ocrBatchCalls = saved.Stats.OCRBatchCalls
		visualSkips = saved.Stats.VisualSkips
		visualConfirmations = saved.Stats.VisualConfirmations
		ocrRetries = saved.Stats.OCRRetries
	}
	batchValue := req.Batch
	if cfg.ForceBatchOne {
		batchValue = "1"
	}
	batchMode, err := normalizeScanBatch(batchValue)
	if err != nil {
		return ScanResult{}, err
	}
	batchSelected := explicitScanBatchSize(batchMode)
	batchCalibrated := batchMode != "auto" || !ocrBatchCapable(s.Engine)
	if !ocrBatchCapable(s.Engine) {
		batchSelected = 1
	}
	var batchBenchmarkDuration time.Duration
	var framePipelineDuration time.Duration
	var visualDuration time.Duration
	var encodeDuration time.Duration
	var ocrDuration time.Duration
	if cfg.Resume != nil {
		framePipelineDuration = time.Duration(cfg.Resume.Stats.FramePipelineSeconds * float64(time.Second))
		visualDuration = time.Duration(cfg.Resume.Stats.VisualSeconds * float64(time.Second))
		encodeDuration = time.Duration(cfg.Resume.Stats.EncodeSeconds * float64(time.Second))
		ocrDuration = time.Duration(cfg.Resume.Stats.OCRSeconds * float64(time.Second))
	} else if resumed {
		framePipelineDuration = time.Duration(saved.Stats.FramePipelineSeconds * float64(time.Second))
		visualDuration = time.Duration(saved.Stats.VisualSeconds * float64(time.Second))
		encodeDuration = time.Duration(saved.Stats.EncodeSeconds * float64(time.Second))
		ocrDuration = time.Duration(saved.Stats.OCRSeconds * float64(time.Second))
	}
	var pendingFrame *scanEvaluatedFrame
	checkpointStats := func() scanCheckpointStats {
		return scanCheckpointStats{
			OCRImages: ocrImages, OCRBatchCalls: ocrBatchCalls, VisualSkips: visualSkips,
			VisualConfirmations: visualConfirmations, OCRRetries: ocrRetries,
			FramePipelineSeconds: framePipelineDuration.Seconds(), VisualSeconds: visualDuration.Seconds(),
			EncodeSeconds: encodeDuration.Seconds(), OCRSeconds: ocrDuration.Seconds(),
		}
	}
	captureSafeState := func(media float64, frames int) {
		if cfg.OnSafeState == nil || !tracker.CanCheckpoint() {
			return
		}
		cfg.OnSafeState(scanLaneState{MediaSeconds: media, Cues: tracker.Cues(), Active: tracker.Active(), Frames: frames, Stats: checkpointStats()})
	}
	maybeSave := func(media float64, frames int) error {
		if checkpoint != nil {
			if err := checkpoint.MaybeSaveWithStats(media, tracker, frames, checkpointStats()); err != nil {
				return err
			}
		}
		captureSafeState(media, frames)
		return nil
	}
	saveNow := func(media float64, frames int) error {
		if checkpoint != nil {
			if err := checkpoint.SaveNowWithStats(media, tracker, frames, checkpointStats()); err != nil {
				return err
			}
		}
		captureSafeState(media, frames)
		return nil
	}

	publish := func(media float64, force bool) {
		if job == nil && cfg.OnProgress == nil {
			return
		}
		now := time.Now()
		if !force && !lastPublish.IsZero() && now.Sub(lastPublish) < 700*time.Millisecond {
			return
		}
		lastPublish = now
		elapsed := now.Sub(startWall).Seconds()
		speed := 0.0
		if elapsed > 0 {
			speed = math.Max(0, media-resumeAt) / elapsed
		}
		progress := -1.0
		if req.Duration > 0 {
			progress = math.Min(99.5, math.Max(0, media/req.Duration*100))
		}
		cues := tracker.Cues()
		pending := tracker.Active()
		decoderLabel := "CPU"
		if decoder.Mode == "nvdec" {
			decoderLabel = "NVDEC"
		}
		avgBatch := 0.0
		if ocrBatchCalls > 0 {
			avgBatch = float64(ocrImages) / float64(ocrBatchCalls)
		}
		msg := fmt.Sprintf("Đang quét %s · %.1f× realtime · %s · %d câu · %d ảnh OCR · batch %.2f", scanClock(media), speed, decoderLabel, len(cues)+boolInt(pending != nil), ocrImages, avgBatch)
		if job != nil {
			job.Set("ocr-scan", progress, msg)
			job.SetResult(buildLiveScanResult(cues, pending, baseFrames+frameIndex, ocrImages, ocrBatchCalls, batchSelected, batchBenchmarkDuration, visualSkips, visualConfirmations, ocrRetries, decoder, framePipelineDuration, visualDuration, encodeDuration, ocrDuration, media, elapsed, speed, progress, lastObservedText, lastObservedAt, lastObservedConf))
		}
		if cfg.OnProgress != nil {
			cfg.OnProgress(scanLaneProgress{
				MediaSeconds: media, Cues: cues, Active: pending, Frames: baseFrames + frameIndex,
				OCRImages: ocrImages, OCRCalls: ocrBatchCalls, VisualSkips: visualSkips, VisualConfirmations: visualConfirmations,
				OCRRetries: ocrRetries, FramePipelineSeconds: framePipelineDuration.Seconds(), VisualSeconds: visualDuration.Seconds(),
				EncodeSeconds: encodeDuration.Seconds(), OCRSeconds: ocrDuration.Seconds(),
				Decoder: decoder,
			})
		}
	}

	tryPause := func(media float64) (bool, error) {
		pauseRequested := false
		if job != nil && job.PauseRequested() {
			pauseRequested = true
		}
		if cfg.PauseRequested != nil && cfg.PauseRequested() {
			pauseRequested = true
		}
		if !pauseRequested || !tracker.CanCheckpoint() {
			return false, nil
		}
		if media <= 0 {
			media = resumeAt
		}
		if media > 0 {
			if err := saveNow(media, baseFrames+frameIndex); err != nil {
				return false, fmt.Errorf("lưu checkpoint khi tạm dừng OCR: %w", err)
			}
		}
		publish(media, true)
		return true, nil
	}

	commitVisualFrame := func(v scanVisualFrame) error {
		if forceNextOCR && v.Stable && tracker.CanVisualConfirm() {
			if tracker.ConfirmVisual(v.At) {
				visualConfirmations++
				forceNextOCR = tracker.NeedsConfirmation()
			}
		} else if forceNextOCR && v.BlankStable && tracker.CanVisualConfirmEmpty() {
			if tracker.ConfirmVisualEmpty(v.At) {
				visualConfirmations++
				forceNextOCR = tracker.NeedsConfirmation()
			}
		} else if v.Stable && !tracker.NeedsConfirmation() {
			tracker.ExtendActiveVisual(v.At)
		}
		visualSkips++
		if err := maybeSave(v.At, baseFrames+v.FrameIndex); err != nil {
			return fmt.Errorf("ghi checkpoint OCR: %w", err)
		}
		publish(v.At, false)
		return nil
	}

	nextEvaluatedFrame := func(timeout time.Duration) (scanEvaluatedFrame, bool, error) {
		if pendingFrame != nil {
			frame := *pendingFrame
			pendingFrame = nil
			return frame, false, nil
		}
		packet, timedOut := frameReader.next(ctx, timeout)
		if timedOut {
			return scanEvaluatedFrame{}, true, nil
		}
		framePipelineDuration += packet.Wait
		if packet.Err != nil {
			return scanEvaluatedFrame{}, false, packet.Err
		}
		t := resumeAt + float64(frameIndex)/mode.FPS
		frameIndex++
		visualStarted := time.Now()
		priorSig := prevSig
		sig := makeEdgeSignature(packet.RGB, scanWidth, scanHeight)
		diff := edgeSignatureDiff(priorSig, sig)
		activity := edgeSignatureActivity(sig)
		frame := scanEvaluatedFrame{
			At: t, FrameIndex: frameIndex, RGB: packet.RGB,
			Changed:     priorSig == nil || diff >= mode.DiffTrigger,
			Activity:    activity,
			Stable:      visualFrameStable(mode, priorSig, diff, activity),
			BlankStable: visualBlankStable(mode, priorSig, diff, activity),
		}
		prevSig = sig
		visualDuration += time.Since(visualStarted)
		return frame, false, nil
	}

	commitCandidate := func(candidate *scanOCRCandidate) error {
		ocrImages += candidate.Calls
		ocrBatchCalls += candidate.BatchCalls
		ocrRetries += candidate.Retries
		encodeDuration += candidate.EncodeDuration
		ocrDuration += candidate.OCRDuration
		lastOCRAt = candidate.At
		if candidate.Err != nil {
			return candidate.Err
		}
		out := candidate.Result
		if out.OK {
			if text, ok := NormalizeChineseSubtitleText(out.Text); ok {
				lastObservedText = text
				lastObservedAt = candidate.At
				lastObservedConf = out.Confidence
			}
			tracker.Observe(candidate.At, out)
			forceNextOCR = tracker.NeedsConfirmation()
		}
		if err := maybeSave(candidate.At, baseFrames+candidate.FrameIndex); err != nil {
			return fmt.Errorf("ghi checkpoint OCR: %w", err)
		}
		publish(candidate.At, false)
		return nil
	}

	var readErr error
	for {
		if err := ctx.Err(); err != nil {
			readErr = err
			break
		}
		mediaNow := resumeAt + float64(frameIndex)/mode.FPS
		if paused, err := tryPause(mediaNow); err != nil {
			readErr = err
			break
		} else if paused {
			readErr = ErrScanPaused
			break
		}

		frame, _, err := nextEvaluatedFrame(0)
		if err != nil {
			if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
				readErr = nil
				break
			}
			readErr = err
			break
		}
		t := frame.At

		if forceNextOCR && frame.Stable && tracker.CanVisualConfirm() {
			if tracker.ConfirmVisual(t) {
				visualConfirmations++
				forceNextOCR = tracker.NeedsConfirmation()
				visualSkips++
				if err := maybeSave(t, baseFrames+frame.FrameIndex); err != nil {
					readErr = fmt.Errorf("ghi checkpoint OCR: %w", err)
					break
				}
				publish(t, false)
				continue
			}
		}
		if forceNextOCR && frame.BlankStable && tracker.CanVisualConfirmEmpty() {
			if tracker.ConfirmVisualEmpty(t) {
				visualConfirmations++
				forceNextOCR = tracker.NeedsConfirmation()
				visualSkips++
				if err := maybeSave(t, baseFrames+frame.FrameIndex); err != nil {
					readErr = fmt.Errorf("ghi checkpoint OCR: %w", err)
					break
				}
				publish(t, false)
				continue
			}
		}
		if frame.Stable && !tracker.NeedsConfirmation() {
			tracker.ExtendActiveVisual(t)
		}

		guardSeconds := mode.GuardSeconds
		if tracker.HasActive() && !tracker.NeedsConfirmation() {
			guardSeconds = mode.ActiveGuardSeconds
		}
		guardDue := t-lastOCRAt >= guardSeconds
		mustConfirm := forceNextOCR
		if !shouldRunOCR(guardDue, frame.Changed, mustConfirm, tracker.HasActive(), frame.Activity) {
			visualSkips++
			if err := maybeSave(t, baseFrames+frame.FrameIndex); err != nil {
				readErr = fmt.Errorf("ghi checkpoint OCR: %w", err)
				break
			}
			publish(t, false)
			continue
		}
		forceNextOCR = false

		first, encErr := newScanOCRCandidate(frame.RGB, t, frame.FrameIndex, frame.Changed, frame.Activity)
		if encErr != nil {
			readErr = encErr
			break
		}
		if !batchCalibrated {
			selected, benchDuration, benchErr := benchmarkScanBatch(ctx, s.Engine, first.Image)
			batchBenchmarkDuration += benchDuration
			if benchErr != nil {
				batchSelected = 1
				if job != nil {
					job.Logf("Auto batch fallback 1: %v", benchErr)
				}
			} else {
				batchSelected = selected
				if job != nil {
					job.Logf("Auto batch chọn %d sau benchmark %.3fs", selected, benchDuration.Seconds())
				}
			}
			batchCalibrated = true
		}

		targetBatch := batchSelected
		if targetBatch < ocrRunnerParallelism(s.Engine) {
			targetBatch = ocrRunnerParallelism(s.Engine)
		}
		if targetBatch > 4 {
			targetBatch = 4
		}
		events := []scanBatchEvent{{Candidate: first}}
		candidates := []*scanOCRCandidate{first}
		var postBatchErr error
		if targetBatch > 1 {
			deadline := time.Now().Add(scanBatchMaxWait)
			for len(candidates) < targetBatch && len(events) < 8 {
				remaining := time.Until(deadline)
				if remaining <= 0 {
					break
				}
				next, timedOut, nextErr := nextEvaluatedFrame(remaining)
				if timedOut {
					break
				}
				if nextErr != nil {
					if errors.Is(nextErr, io.EOF) || errors.Is(nextErr, io.ErrUnexpectedEOF) {
						postBatchErr = io.EOF
					} else {
						postBatchErr = nextErr
					}
					break
				}
				// Do not manufacture OCR work to fill a batch. A text-like visual
				// transition is independent enough to OCR ahead. A changed low-activity
				// frame (often subtitle disappearance) depends on the result of the
				// current candidate, so defer it until tracker state is committed.
				if next.Changed && next.Activity >= minSubtitleActivity {
					candidate, encErr := newScanOCRCandidate(next.RGB, next.At, next.FrameIndex, next.Changed, next.Activity)
					if encErr != nil {
						readErr = encErr
						break
					}
					candidates = append(candidates, candidate)
					events = append(events, scanBatchEvent{Candidate: candidate})
				} else if next.Changed {
					pendingFrame = &next
					break
				} else {
					events = append(events, scanBatchEvent{Visual: &scanVisualFrame{At: next.At, FrameIndex: next.FrameIndex, Stable: next.Stable, BlankStable: next.BlankStable}})
				}
			}
		}
		if readErr != nil {
			break
		}

		runScanOCRCandidates(ctx, s.Engine, mode, candidates, batchSelected)
		for _, event := range events {
			if event.Candidate != nil {
				if err := commitCandidate(event.Candidate); err != nil {
					readErr = err
					break
				}
				continue
			}
			if event.Visual != nil {
				if err := commitVisualFrame(*event.Visual); err != nil {
					readErr = err
					break
				}
			}
		}
		if readErr != nil {
			break
		}
		if postBatchErr != nil {
			if errors.Is(postBatchErr, io.EOF) {
				readErr = nil
			} else {
				readErr = postBatchErr
			}
			break
		}
	}

	frameCancel()
	if errors.Is(readErr, ErrScanPaused) && cmd.Process != nil {
		_ = cmd.Process.Kill()
	}
	waitErr := cmd.Wait()
	stderrBytes := <-stderrCh
	media := resumeAt + float64(frameIndex)/mode.FPS
	if endAt > 0 && media > endAt {
		media = endAt
	}
	if req.Duration > 0 && media > req.Duration {
		media = req.Duration
	}
	if errors.Is(readErr, ErrScanPaused) {
		elapsed := time.Since(startWall).Seconds()
		speed := 0.0
		if elapsed > 0 {
			speed = math.Max(0, media-resumeAt) / elapsed
		}
		averageBatch := 0.0
		if ocrBatchCalls > 0 {
			averageBatch = float64(ocrImages) / float64(ocrBatchCalls)
		}
		return ScanResult{
			Cues: tracker.Cues(), Frames: baseFrames + frameIndex, OCRCalls: ocrImages, OCRImages: ocrImages,
			OCRBatchCalls: ocrBatchCalls, BatchSelected: batchSelected, AverageBatch: averageBatch,
			BatchBenchmarkSeconds: batchBenchmarkDuration.Seconds(), VisualSkips: visualSkips,
			VisualConfirmations: visualConfirmations, OCRRetries: ocrRetries, Decoder: decoder.Mode,
			DecoderFallback: decoder.FallbackReason, FramePipelineSeconds: framePipelineDuration.Seconds(),
			VisualSeconds: visualDuration.Seconds(), EncodeSeconds: encodeDuration.Seconds(), OCRSeconds: ocrDuration.Seconds(),
			MediaSeconds: media, ElapsedSeconds: elapsed, RealtimeSpeed: speed,
		}, ErrScanPaused
	}
	if readErr != nil {
		_ = saveNow(media, baseFrames+frameIndex)
		if errors.Is(readErr, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) {
			return ScanResult{}, context.Canceled
		}
		return ScanResult{}, readErr
	}
	if waitErr != nil && ctx.Err() == nil {
		_ = saveNow(media, baseFrames+frameIndex)
		msg := strings.TrimSpace(string(stderrBytes))
		if decoder.Mode == "nvdec" {
			reason := compactFFmpegError(msg)
			if reason == "" {
				reason = waitErr.Error()
			}
			return s.run(ctx, job, req, "NVDEC lỗi, đã chuyển sang CPU: "+reason, cfg)
		}
		if msg != "" {
			return ScanResult{}, fmt.Errorf("FFmpeg OCR scan: %w: %s", waitErr, msg)
		}
		return ScanResult{}, fmt.Errorf("FFmpeg OCR scan: %w", waitErr)
	}
	tracker.Finish(media)
	cues := tracker.Cues()
	elapsed := time.Since(startWall).Seconds()
	speed := 0.0
	if elapsed > 0 {
		speed = math.Max(0, media-resumeAt) / elapsed
	}
	callsPerCue := 0.0
	if len(cues) > 0 {
		callsPerCue = float64(ocrImages) / float64(len(cues))
	}
	averageBatch := 0.0
	if ocrBatchCalls > 0 {
		averageBatch = float64(ocrImages) / float64(ocrBatchCalls)
	}
	result := ScanResult{
		Cues: cues, Frames: baseFrames + frameIndex, OCRCalls: ocrImages, OCRImages: ocrImages,
		OCRBatchCalls: ocrBatchCalls, BatchSelected: batchSelected, AverageBatch: averageBatch,
		BatchBenchmarkSeconds: batchBenchmarkDuration.Seconds(), VisualSkips: visualSkips,
		VisualConfirmations: visualConfirmations, OCRRetries: ocrRetries, Decoder: decoder.Mode,
		DecoderFallback: decoder.FallbackReason, FramePipelineSeconds: framePipelineDuration.Seconds(),
		VisualSeconds: visualDuration.Seconds(), EncodeSeconds: encodeDuration.Seconds(), OCRSeconds: ocrDuration.Seconds(),
		OCRCallsPerCue: callsPerCue, MediaSeconds: media, ElapsedSeconds: elapsed, RealtimeSpeed: speed,
	}
	if err := checkpoint.Remove(); err != nil {
		return ScanResult{}, fmt.Errorf("xóa checkpoint OCR sau khi hoàn tất: %w", err)
	}
	if job != nil {
		job.SetResult(result)
		job.Set("ocr-scan", 100, fmt.Sprintf("Đã quét xong · %d câu · %d ảnh OCR / %d lượt inference · %.1f× realtime", len(cues), ocrImages, ocrBatchCalls, speed))
		publish(media, true)
		// publish() intentionally emits only lightweight live stats. Restore the
		// complete final result after it so the client receives all cues on done.
		job.SetResult(result)
	}
	return result, nil
}

func ocrRunnerParallelism(engine OCRRunner) int {
	p, ok := engine.(OCRParallelRunner)
	if !ok {
		return 1
	}
	n := p.Parallelism()
	if n < 1 {
		return 1
	}
	if n > 2 {
		return 2
	}
	return n
}

func newScanOCRCandidate(rgb []byte, at float64, frameIndex int, changed bool, activity float64) (*scanOCRCandidate, error) {
	started := time.Now()
	image, err := framePNGBase64(rgb, scanWidth, scanHeight, false)
	if err != nil {
		return nil, err
	}
	candidate := &scanOCRCandidate{
		At: at, FrameIndex: frameIndex, Changed: changed, Activity: activity, Image: image,
		EncodeDuration: time.Since(started),
	}
	if changed {
		// RC9 eagerly encoded a second enhanced PNG for every changed frame even
		// when the first OCR result was already high-confidence. Keep only one
		// bounded RGB copy and create the enhanced retry lazily if it is needed.
		candidate.RetryRGB = append([]byte(nil), rgb...)
	}
	return candidate, nil
}

func runScanOCRCandidates(ctx context.Context, engine OCRRunner, mode scanMode, candidates []*scanOCRCandidate, batchSize int) {
	runSingle := func(candidate *scanOCRCandidate, image string) Result {
		candidate.Calls++ // historical OCR-call counter is kept as images processed.
		candidate.BatchCalls++
		started := time.Now()
		out, err := engine.Run(ctx, image)
		candidate.OCRDuration += time.Since(started)
		if err != nil {
			candidate.Err = err
			return Result{}
		}
		return out
	}

	runBatch := func(group []*scanOCRCandidate, images []string) bool {
		runner, ok := engine.(OCRBatchRunner)
		if !ok || !runner.BatchCapable() || batchSize <= 1 || len(group) <= 1 {
			return false
		}
		for _, candidate := range group {
			candidate.Calls++
		}
		group[0].BatchCalls++
		started := time.Now()
		results, err := runner.RunBatch(ctx, images)
		group[0].OCRDuration += time.Since(started)
		if err != nil {
			for _, candidate := range group {
				candidate.Err = err
			}
			return true
		}
		if len(results) != len(group) {
			err = fmt.Errorf("batch OCR trả %d/%d kết quả", len(results), len(group))
			for _, candidate := range group {
				candidate.Err = err
			}
			return true
		}
		for i, result := range results {
			group[i].Result = result
		}
		return true
	}

	images := make([]string, len(candidates))
	for i, candidate := range candidates {
		images[i] = candidate.Image
	}
	if !runBatch(candidates, images) {
		if len(candidates) > 1 && ocrRunnerParallelism(engine) >= 2 {
			var wg sync.WaitGroup
			wg.Add(len(candidates))
			for _, candidate := range candidates {
				candidate := candidate
				go func() {
					defer wg.Done()
					candidate.Result = runSingle(candidate, candidate.Image)
				}()
			}
			wg.Wait()
		} else {
			for _, candidate := range candidates {
				candidate.Result = runSingle(candidate, candidate.Image)
			}
		}
	}

	var retries []*scanOCRCandidate
	for _, candidate := range candidates {
		if candidate.Err != nil || len(candidate.RetryRGB) == 0 || !candidate.Result.OK {
			candidate.RetryRGB = nil
			continue
		}
		// A visual-empty disappearance that PaddleOCR also reports as no text is
		// already strong evidence; enhancing and OCR'ing the same blank frame again
		// only burns inference time. Active-looking frames still get the retry so a
		// faint/fading subtitle is not lost merely because the first pass missed it.
		if !candidate.Result.Detected && candidate.Activity < minSubtitleActivity {
			candidate.RetryRGB = nil
			continue
		}
		if strings.TrimSpace(candidate.Result.Text) == "" || candidate.Result.Confidence < mode.LowConf {
			retries = append(retries, candidate)
		} else {
			candidate.RetryRGB = nil
		}
	}
	if len(retries) == 0 {
		return
	}

	retryImages := make([]string, 0, len(retries))
	validRetries := make([]*scanOCRCandidate, 0, len(retries))
	for _, candidate := range retries {
		started := time.Now()
		enhanced, err := framePNGBase64(candidate.RetryRGB, scanWidth, scanHeight, true)
		candidate.EncodeDuration += time.Since(started)
		candidate.RetryRGB = nil
		if err != nil {
			candidate.Err = err
			continue
		}
		candidate.Retries++
		validRetries = append(validRetries, candidate)
		retryImages = append(retryImages, enhanced)
	}
	if len(validRetries) == 0 {
		return
	}

	original := make([]Result, len(validRetries))
	for i, candidate := range validRetries {
		original[i] = candidate.Result
	}
	if runBatch(validRetries, retryImages) {
		for i, candidate := range validRetries {
			if candidate.Err == nil && candidate.Result.OK && candidate.Result.Confidence <= original[i].Confidence {
				candidate.Result = original[i]
			}
		}
		return
	}
	if len(validRetries) > 1 && ocrRunnerParallelism(engine) >= 2 {
		var wg sync.WaitGroup
		wg.Add(len(validRetries))
		for i, candidate := range validRetries {
			i, candidate := i, candidate
			go func() {
				defer wg.Done()
				alt := runSingle(candidate, retryImages[i])
				if candidate.Err == nil && alt.OK && alt.Confidence > original[i].Confidence {
					candidate.Result = alt
				} else if candidate.Err == nil {
					candidate.Result = original[i]
				}
			}()
		}
		wg.Wait()
		return
	}
	for i, candidate := range validRetries {
		alt := runSingle(candidate, retryImages[i])
		if candidate.Err == nil && alt.OK && alt.Confidence > original[i].Confidence {
			candidate.Result = alt
		} else if candidate.Err == nil {
			candidate.Result = original[i]
		}
	}
}

func buildLiveScanResult(cues []Cue, pending *Cue, frames, ocrImages, ocrBatchCalls, batchSelected int, batchBenchmarkDuration time.Duration, visualSkips, visualConfirmations, ocrRetries int, decoder scanDecoderDecision, framePipelineDuration, visualDuration, encodeDuration, ocrDuration time.Duration, media, elapsed, speed, progress float64, lastText string, lastAt, lastConf float64) map[string]any {
	const maxRecent = 120
	start := 0
	if len(cues) > maxRecent {
		start = len(cues) - maxRecent
	}
	recent := append([]Cue(nil), cues[start:]...)
	if pending != nil {
		recent = append(recent, *pending)
		if len(recent) > maxRecent {
			recent = recent[len(recent)-maxRecent:]
		}
	}
	cueCount := len(cues) + boolInt(pending != nil)
	callsPerCue := 0.0
	if cueCount > 0 {
		callsPerCue = float64(ocrImages) / float64(cueCount)
	}
	averageBatch := 0.0
	if ocrBatchCalls > 0 {
		averageBatch = float64(ocrImages) / float64(ocrBatchCalls)
	}
	return map[string]any{
		"cue_count":               cueCount,
		"frames":                  frames,
		"ocr_calls":               ocrImages, // backwards-compatible alias from RC11.
		"ocr_images":              ocrImages,
		"ocr_batch_calls":         ocrBatchCalls,
		"batch_selected":          batchSelected,
		"average_batch":           averageBatch,
		"batch_benchmark_seconds": batchBenchmarkDuration.Seconds(),
		"visual_skips":            visualSkips,
		"visual_confirmations":    visualConfirmations,
		"ocr_retries":             ocrRetries,
		"decoder":                 decoder.Mode,
		"decoder_fallback":        decoder.FallbackReason,
		"frame_pipeline_seconds":  framePipelineDuration.Seconds(),
		"visual_seconds":          visualDuration.Seconds(),
		"encode_seconds":          encodeDuration.Seconds(),
		"ocr_seconds":             ocrDuration.Seconds(),
		"ocr_calls_per_cue":       callsPerCue,
		"media_seconds":           media,
		"elapsed_seconds":         elapsed,
		"realtime_speed":          speed,
		"progress_percent":        progress,
		"parallelism_selected":    1,
		"active_lanes":            1,
		"completed_lanes":         0,
		"total_lanes":             1,
		"last_text":               lastText,
		"last_time":               lastAt,
		"last_confidence":         lastConf,
		"recent_cues":             recent,
	}
}

// CaptureFramePNGBase64 extracts exactly one video frame at the requested
// timestamp and crops it using the same normalized ROI semantics as the full
// OCR scanner. It intentionally runs in the backend so manual frame OCR does
// not depend on browser codec support, canvas state, or fallback <img> timing.
func CaptureFramePNGBase64(ctx context.Context, ffmpeg, path string, at float64, region ScanRegion, enhanced bool) (string, error) {
	ff := strings.TrimSpace(ffmpeg)
	if ff == "" {
		return "", errors.New("OCR frame chưa có ffmpeg")
	}
	path = strings.TrimSpace(path)
	if path == "" {
		return "", errors.New("thiếu video nguồn")
	}
	st, err := os.Stat(path)
	if err != nil || st.IsDir() || st.Size() == 0 {
		return "", errors.New("video nguồn không tồn tại hoặc rỗng")
	}
	reg, err := normalizeScanRegion(region)
	if err != nil {
		return "", err
	}
	if at < 0 || math.IsNaN(at) || math.IsInf(at, 0) {
		at = 0
	}
	filter := fmt.Sprintf(
		"crop=iw*%.8f:ih*%.8f:iw*%.8f:ih*%.8f,scale=%d:%d:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=%d:%d:(ow-iw)/2:(oh-ih)/2:black,format=rgb24",
		reg.W, reg.H, reg.X, reg.Y, scanWidth, scanHeight, scanWidth, scanHeight,
	)
	args := []string{
		"-hide_banner", "-loglevel", "error", "-nostdin",
		"-ss", fmt.Sprintf("%.3f", at), "-i", path,
		"-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
		"-vf", filter,
		"-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
	}
	cmd := proc.Hide(exec.CommandContext(ctx, ff, args...))
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("đọc khung OCR tại %.3fs: %w", at, err)
	}
	want := scanWidth * scanHeight * 3
	if len(out) != want {
		return "", fmt.Errorf("khung OCR thiếu dữ liệu: %d/%d bytes", len(out), want)
	}
	return framePNGBase64(out, scanWidth, scanHeight, enhanced)
}

func normalizeScanRegion(r ScanRegion) (ScanRegion, error) {
	if r.W <= 0 || r.H <= 0 {
		return ScanRegion{}, errors.New("vùng OCR không hợp lệ")
	}
	if r.X < 0 {
		r.X = 0
	}
	if r.Y < 0 {
		r.Y = 0
	}
	if r.X > 1 {
		r.X = 1
	}
	if r.Y > 1 {
		r.Y = 1
	}
	if r.W > 1-r.X {
		r.W = 1 - r.X
	}
	if r.H > 1-r.Y {
		r.H = 1 - r.Y
	}
	if r.W < 0.01 || r.H < 0.01 {
		return ScanRegion{}, errors.New("vùng OCR quá nhỏ")
	}
	return r, nil
}

type edgeSignature []byte

func makeEdgeSignature(rgb []byte, w, h int) edgeSignature {
	const cols, rows = 160, 40
	out := make(edgeSignature, cols*rows)
	if len(rgb) < w*h*3 || w <= 8 || h <= 8 {
		return out
	}
	// Subtitle-like local-contrast mask. Generic scene edges fire constantly on
	// moving backgrounds and would send nearly every frame to the OCR engine.
	for gy := 0; gy < rows; gy++ {
		cy := 4 + gy*(h-8)/rows
		for gx := 0; gx < cols; gx++ {
			cx := 4 + gx*(w-8)/cols
			hit := false
			for dy := -3; dy <= 3 && !hit; dy += 2 {
				for dx := -3; dx <= 3; dx += 2 {
					if subtitleLikePixel(rgb, w, h, cx+dx, cy+dy) {
						hit = true
						break
					}
				}
			}
			if hit {
				out[gy*cols+gx] = 1
			}
		}
	}
	return out
}

func subtitleLikePixel(rgb []byte, w, h, x, y int) bool {
	if x < 2 || y < 2 || x >= w-2 || y >= h-2 {
		return false
	}
	i := (y*w + x) * 3
	r, g, b := int(rgb[i]), int(rgb[i+1]), int(rgb[i+2])
	hi := maxInt(r, g, b)
	lo := minInt(r, g, b)
	lum := (77*r + 150*g + 29*b) >> 8
	if lum < 132 && !(hi >= 165 && hi-lo >= 45) {
		return false
	}
	minNeighbor := 255
	for _, q := range [][2]int{{-2, 0}, {2, 0}, {0, -2}, {0, 2}, {-1, -1}, {1, 1}} {
		n := lumAt(rgb, w, x+q[0], y+q[1])
		if n < minNeighbor {
			minNeighbor = n
		}
	}
	return lum-minNeighbor >= 34 || (hi-lo >= 55 && hi-minNeighbor >= 55)
}

func edgeSignatureActivity(sig edgeSignature) float64 {
	if len(sig) == 0 {
		return 0
	}
	hits := 0
	for _, v := range sig {
		if v != 0 {
			hits++
		}
	}
	return float64(hits) / float64(len(sig))
}

const minSubtitleActivity = 0.012

func visualFrameStable(mode scanMode, previous edgeSignature, diff, activity float64) bool {
	if len(previous) == 0 || activity < minSubtitleActivity {
		return false
	}
	// A substantially tighter threshold than the transition trigger prevents
	// animated/fading text from being treated as a stable visual confirmation.
	return diff <= mode.DiffTrigger*0.45
}

func visualBlankStable(mode scanMode, previous edgeSignature, diff, activity float64) bool {
	if len(previous) == 0 || activity >= minSubtitleActivity {
		return false
	}
	return diff <= mode.DiffTrigger*0.45
}

func shouldRunOCR(guardDue, changed, mustConfirm, hasActive bool, activity float64) bool {
	if mustConfirm {
		return true
	}
	if hasActive && changed && activity < minSubtitleActivity {
		// A confirmed subtitle disappearing is itself a transition. Ask the OCR
		// engine once, then the tracker forces one follow-up observation.
		return true
	}
	if activity < minSubtitleActivity {
		return false
	}
	return changed || guardDue
}

func edgeSignatureDiff(a, b edgeSignature) float64 {
	if len(a) == 0 || len(b) == 0 || len(a) != len(b) {
		return 1
	}
	union, diff := 0, 0
	for i := range a {
		if a[i] != 0 || b[i] != 0 {
			union++
		}
		if a[i] != b[i] {
			diff++
		}
	}
	if union < 24 {
		if diff > 12 {
			return 1
		}
		return 0
	}
	return float64(diff) / float64(union)
}

func lumAt(rgb []byte, w, x, y int) int {
	i := (y*w + x) * 3
	return (77*int(rgb[i]) + 150*int(rgb[i+1]) + 29*int(rgb[i+2])) >> 8
}

func framePNGBase64(rgb []byte, w, h int, enhanced bool) (string, error) {
	if len(rgb) < w*h*3 {
		return "", errors.New("frame RGB thiếu dữ liệu")
	}
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for si, di := 0, 0; si < w*h*3; si, di = si+3, di+4 {
		r, g, b := rgb[si], rgb[si+1], rgb[si+2]
		if enhanced {
			l := (77*int(r) + 150*int(g) + 29*int(b)) >> 8
			l = clampInt((l-128)*2+128, 0, 255)
			r, g, b = byte(l), byte(l), byte(l)
		}
		img.Pix[di], img.Pix[di+1], img.Pix[di+2], img.Pix[di+3] = r, g, b, 255
	}
	var out bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.BestSpeed}
	if err := enc.Encode(&out, img); err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(out.Bytes()), nil
}

func cleanScanText(s string) string {
	return strings.TrimSpace(strings.Join(strings.Fields(strings.ReplaceAll(strings.ReplaceAll(s, "\r", " "), "\n", " ")), " "))
}

func comparableScanText(s string) string {
	s = cleanScanText(s)
	repl := strings.NewReplacer(" ", "", "，", "", "。", "", "！", "", "？", "", "、", "", ",", "", ".", "", "!", "", "?", "", ";", "", "；", "", ":", "", "：", "", "'", "", "\"", "", "“", "", "”", "", "‘", "", "’", "", "（", "", "）", "", "(", "", ")", "", "[", "", "]", "", "【", "", "】", "")
	return repl.Replace(s)
}

func scanSimilarity(a, b string) float64 {
	a = comparableScanText(a)
	b = comparableScanText(b)
	if a == b {
		return 1
	}
	if a == "" || b == "" {
		return 0
	}
	ra, rb := []rune(a), []rune(b)
	prev := make([]int, len(rb)+1)
	cur := make([]int, len(rb)+1)
	for j := range prev {
		prev[j] = j
	}
	for i := 1; i <= len(ra); i++ {
		cur[0] = i
		for j := 1; j <= len(rb); j++ {
			cost := 1
			if ra[i-1] == rb[j-1] {
				cost = 0
			}
			cur[j] = minInt(cur[j-1]+1, prev[j]+1, prev[j-1]+cost)
		}
		prev, cur = cur, prev
	}
	den := len(ra)
	if len(rb) > den {
		den = len(rb)
	}
	return 1 - float64(prev[len(rb)])/float64(den)
}

func scanClock(sec float64) string {
	if sec < 0 || math.IsNaN(sec) || math.IsInf(sec, 0) {
		sec = 0
	}
	whole := int64(sec + 0.5)
	h := whole / 3600
	m := (whole % 3600) / 60
	s := whole % 60
	if h > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
	}
	return fmt.Sprintf("%02d:%02d", m, s)
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

func absInt(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

func minInt(v ...int) int {
	m := v[0]
	for _, x := range v[1:] {
		if x < m {
			m = x
		}
	}
	return m
}

func maxInt(v ...int) int {
	m := v[0]
	for _, x := range v[1:] {
		if x > m {
			m = x
		}
	}
	return m
}

func boolInt(v bool) int {
	if v {
		return 1
	}
	return 0
}

// String is intentionally useful in logs and tests without exposing the
// internal mode struct through the HTTP contract.
func (m scanMode) String() string {
	return strconv.FormatFloat(m.FPS, 'f', -1, 64) + "fps"
}

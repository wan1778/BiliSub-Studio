package nativeui

import (
	"fmt"
	"strings"

	"bilisubstudio/internal/ocr"
)

type ocrTelemetryView struct {
	Source                string
	Schema                int
	CueCount              int
	ListCount             int
	Frames                int
	OCRImages             int
	InferenceCalls        int
	BatchSelected         int
	AverageBatch          float64
	BatchBenchmarkSeconds float64
	VisualSkips           int
	VisualConfirmations   int
	OCRRetries            int
	Decoder               string
	DecoderFallback       string
	FramePipelineSeconds  float64
	VisualSeconds         float64
	EncodeSeconds         float64
	OCRSeconds            float64
	OCRCallsPerCue        float64
	MediaSeconds          float64
	ElapsedSeconds        float64
	RealtimeSpeed         float64
	ProgressPercent       float64
	ParallelismSelected   int
	ActiveLanes           int
	CompletedLanes        int
	TotalLanes            int
	BoundaryMerges        int
	AutoTested            []int
	AutoStopReason        string
	AutoBenchmarkSeconds  float64
	LastText              string
	LastConfidence        float64
}

func telemetryFromCheckpoint(cp ocr.CheckpointInfo, listCount int) ocrTelemetryView {
	callsPerCue := 0.0
	if cp.CueCount > 0 {
		callsPerCue = float64(cp.OCRCalls) / float64(cp.CueCount)
	}
	averageBatch := 0.0
	if cp.OCRBatchCalls > 0 {
		averageBatch = float64(cp.OCRCalls) / float64(cp.OCRBatchCalls)
	}
	return ocrTelemetryView{
		Source: "Checkpoint", Schema: cp.Schema, CueCount: cp.CueCount, ListCount: listCount,
		Frames: cp.Frames, OCRImages: cp.OCRCalls, InferenceCalls: cp.OCRBatchCalls,
		AverageBatch: averageBatch, VisualSkips: cp.VisualSkips, VisualConfirmations: cp.VisualConfirmations,
		OCRRetries: cp.OCRRetries, FramePipelineSeconds: cp.FramePipelineSeconds, VisualSeconds: cp.VisualSeconds,
		EncodeSeconds: cp.EncodeSeconds, OCRSeconds: cp.OCRSeconds, OCRCallsPerCue: callsPerCue,
		MediaSeconds: cp.MediaSeconds, ProgressPercent: cp.ProgressPercent, ParallelismSelected: cp.ParallelismSelected,
		ActiveLanes: cp.ActiveLanes, CompletedLanes: cp.CompletedLanes, TotalLanes: cp.TotalLanes,
		BoundaryMerges: cp.BoundaryMerges,
	}
}

func telemetryFromScanResult(r ocr.ScanResult) ocrTelemetryView {
	return ocrTelemetryView{
		Source: "Kết quả", CueCount: len(r.Cues), ListCount: len(r.Cues), Frames: r.Frames,
		OCRImages: r.OCRImages, InferenceCalls: r.OCRBatchCalls, BatchSelected: r.BatchSelected,
		AverageBatch: r.AverageBatch, BatchBenchmarkSeconds: r.BatchBenchmarkSeconds,
		VisualSkips: r.VisualSkips, VisualConfirmations: r.VisualConfirmations, OCRRetries: r.OCRRetries,
		Decoder: r.Decoder, DecoderFallback: r.DecoderFallback, FramePipelineSeconds: r.FramePipelineSeconds,
		VisualSeconds: r.VisualSeconds, EncodeSeconds: r.EncodeSeconds, OCRSeconds: r.OCRSeconds,
		OCRCallsPerCue: r.OCRCallsPerCue, MediaSeconds: r.MediaSeconds, ElapsedSeconds: r.ElapsedSeconds,
		RealtimeSpeed: r.RealtimeSpeed, ProgressPercent: 100, ParallelismSelected: r.ParallelismSelected,
		ActiveLanes: r.ActiveLanes, CompletedLanes: r.CompletedLanes, TotalLanes: telemetryMaxInt(r.ParallelismSelected, r.CompletedLanes),
		BoundaryMerges: r.BoundaryMerges, AutoTested: append([]int(nil), r.AutoTested...),
		AutoStopReason: r.AutoStopReason, AutoBenchmarkSeconds: r.AutoBenchmarkSeconds,
	}
}

func telemetryFromLiveMap(m map[string]any, listCount int) ocrTelemetryView {
	t := ocrTelemetryView{Source: "Đang quét", ListCount: listCount}
	t.CueCount = intAny(m["cue_count"])
	t.Frames = intAny(m["frames"])
	t.OCRImages = intAnyFirst(m["ocr_images"], m["ocr_calls"])
	t.InferenceCalls = intAny(m["ocr_batch_calls"])
	t.BatchSelected = intAny(m["batch_selected"])
	t.AverageBatch, _ = telemetryNumAny(m["average_batch"])
	t.BatchBenchmarkSeconds, _ = telemetryNumAny(m["batch_benchmark_seconds"])
	t.VisualSkips = intAny(m["visual_skips"])
	t.VisualConfirmations = intAny(m["visual_confirmations"])
	t.OCRRetries = intAny(m["ocr_retries"])
	t.Decoder, _ = m["decoder"].(string)
	t.DecoderFallback, _ = m["decoder_fallback"].(string)
	t.FramePipelineSeconds, _ = telemetryNumAny(m["frame_pipeline_seconds"])
	t.VisualSeconds, _ = telemetryNumAny(m["visual_seconds"])
	t.EncodeSeconds, _ = telemetryNumAny(m["encode_seconds"])
	t.OCRSeconds, _ = telemetryNumAny(m["ocr_seconds"])
	t.OCRCallsPerCue, _ = telemetryNumAny(m["ocr_calls_per_cue"])
	t.MediaSeconds, _ = telemetryNumAny(m["media_seconds"])
	t.ElapsedSeconds, _ = telemetryNumAny(m["elapsed_seconds"])
	t.RealtimeSpeed, _ = telemetryNumAny(m["realtime_speed"])
	t.ProgressPercent, _ = telemetryNumAny(m["progress_percent"])
	t.ParallelismSelected = intAny(m["parallelism_selected"])
	t.ActiveLanes = intAny(m["active_lanes"])
	t.CompletedLanes = intAny(m["completed_lanes"])
	t.TotalLanes = intAny(m["total_lanes"])
	if t.TotalLanes == 0 {
		t.TotalLanes = t.ParallelismSelected
	}
	t.BoundaryMerges = intAny(m["boundary_merges"])
	if s, ok := m["last_text"].(string); ok {
		t.LastText = s
	}
	t.LastConfidence, _ = telemetryNumAny(m["last_confidence"])
	return t
}

func formatOCRTelemetry(t ocrTelemetryView) string {
	total := telemetryMaxInt(t.CueCount, t.ListCount)
	source := t.Source
	if t.Schema > 0 {
		source += fmt.Sprintf(" schema %d", t.Schema)
	}
	decoder := strings.TrimSpace(t.Decoder)
	if decoder == "" {
		decoder = "—"
	} else if decoder == "nvdec" {
		decoder = "NVIDIA NVDEC"
	} else if decoder == "software" {
		decoder = "CPU"
	}
	if strings.TrimSpace(t.DecoderFallback) != "" {
		decoder += " · fallback: " + strings.TrimSpace(t.DecoderFallback)
	}
	speed := "—"
	if t.RealtimeSpeed > 0 {
		speed = fmt.Sprintf("%.1f×", t.RealtimeSpeed)
	}
	progress := "—"
	if t.ProgressPercent > 0 || t.MediaSeconds > 0 {
		progress = fmt.Sprintf("%.1f%% · mốc %s", t.ProgressPercent, telemetryClock(t.MediaSeconds))
	}
	lanes := "—"
	if t.ParallelismSelected > 0 || t.TotalLanes > 0 {
		totalLanes := telemetryMaxInt(t.TotalLanes, t.ParallelismSelected)
		lanes = fmt.Sprintf("%d chọn · %d hoạt động · %d/%d hoàn tất", t.ParallelismSelected, t.ActiveLanes, t.CompletedLanes, totalLanes)
	}
	avgBatch := "—"
	if t.AverageBatch > 0 {
		avgBatch = fmt.Sprintf("%.2f", t.AverageBatch)
	}
	callsPerCue := "—"
	if t.OCRCallsPerCue > 0 {
		callsPerCue = fmt.Sprintf("%.2f", t.OCRCallsPerCue)
	}
	lines := []string{
		fmt.Sprintf("%s · Danh sách %d / %d câu · %s · tốc độ %s", source, t.ListCount, total, progress, speed),
		fmt.Sprintf("OCR %d ảnh · %d inference · %.2f ảnh/câu · batch TB %s · frames %d", t.OCRImages, t.InferenceCalls, t.OCRCallsPerCue, avgBatch, t.Frames),
		fmt.Sprintf("Luồng: %s · boundary merge %d · decoder %s", lanes, t.BoundaryMerges, decoder),
		fmt.Sprintf("Visual skip %d · confirm %d · retry %d", t.VisualSkips, t.VisualConfirmations, t.OCRRetries),
		fmt.Sprintf("Thời gian: pipeline %.1fs · visual %.1fs · encode %.1fs · OCR %.1fs · elapsed %.1fs", t.FramePipelineSeconds, t.VisualSeconds, t.EncodeSeconds, t.OCRSeconds, t.ElapsedSeconds),
	}
	// Replace a misleading zero rendered by the numeric formatter when there
	// is no cue yet; keep the visible telemetry stable during startup.
	lines[1] = strings.Replace(lines[1], "0.00 ảnh/câu", callsPerCue+" ảnh/câu", 1)
	if t.BatchSelected > 0 || t.BatchBenchmarkSeconds > 0 {
		lines = append(lines, fmt.Sprintf("Micro-batch: %d · benchmark %.1fs", t.BatchSelected, t.BatchBenchmarkSeconds))
	}
	if len(t.AutoTested) > 0 || t.AutoStopReason != "" || t.AutoBenchmarkSeconds > 0 {
		lines = append(lines, fmt.Sprintf("Auto: tested %v · dừng %s · benchmark %.1fs", t.AutoTested, emptyDash(t.AutoStopReason), t.AutoBenchmarkSeconds))
	}
	if strings.TrimSpace(t.LastText) != "" {
		lines = append(lines, fmt.Sprintf("Gần nhất: %.0f%% · %s", t.LastConfidence*100, t.LastText))
	}
	return strings.Join(lines, "\r\n")
}

func intAny(v any) int {
	if f, ok := telemetryNumAny(v); ok {
		return int(f)
	}
	return 0
}

func intAnyFirst(values ...any) int {
	for _, v := range values {
		if f, ok := telemetryNumAny(v); ok {
			return int(f)
		}
	}
	return 0
}

func emptyDash(s string) string {
	if strings.TrimSpace(s) == "" {
		return "—"
	}
	return strings.TrimSpace(s)
}

func telemetryMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func telemetryNumAny(v any) (float64, bool) {
	switch x := v.(type) {
	case float64:
		return x, true
	case float32:
		return float64(x), true
	case int:
		return float64(x), true
	case int8:
		return float64(x), true
	case int16:
		return float64(x), true
	case int32:
		return float64(x), true
	case int64:
		return float64(x), true
	case uint:
		return float64(x), true
	case uint8:
		return float64(x), true
	case uint16:
		return float64(x), true
	case uint32:
		return float64(x), true
	case uint64:
		return float64(x), true
	}
	return 0, false
}

func telemetryClock(sec float64) string {
	if sec < 0 {
		sec = 0
	}
	t := int(sec + .5)
	h := t / 3600
	m := (t % 3600) / 60
	s := t % 60
	if h > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
	}
	return fmt.Sprintf("%02d:%02d", m, s)
}

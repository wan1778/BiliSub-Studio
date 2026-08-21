package nativeui

import (
	"strings"
	"testing"

	"bilisubstudio/internal/ocr"
)

func TestCheckpointTelemetryIncludesResumeTopologyAndCounters(t *testing.T) {
	cp := ocr.CheckpointInfo{
		Exists: true, Schema: 4, MediaSeconds: 161, ProgressPercent: 40, CueCount: 562,
		Frames: 9000, OCRCalls: 5397, OCRBatchCalls: 1888, VisualSkips: 321,
		VisualConfirmations: 64, OCRRetries: 3, ParallelismSelected: 8, ActiveLanes: 0,
		CompletedLanes: 2, TotalLanes: 8, BoundaryMerges: 4, FramePipelineSeconds: 25.9,
		VisualSeconds: 10.7, EncodeSeconds: 2.3, OCRSeconds: 80.1,
	}
	got := formatOCRTelemetry(telemetryFromCheckpoint(cp, 120))
	for _, want := range []string{"Checkpoint schema 4", "Danh sách 120 / 562 câu", "40.0% · mốc 02:41", "OCR 5397 ảnh", "1888 inference", "8 chọn · 0 hoạt động · 2/8 hoàn tất", "boundary merge 4", "confirm 64", "retry 3"} {
		if !strings.Contains(got, want) {
			t.Fatalf("telemetry missing %q:\n%s", want, got)
		}
	}
}

func TestLiveTelemetryIncludesFullParallelMetrics(t *testing.T) {
	live := map[string]any{
		"cue_count": 835, "frames": 12000, "ocr_images": 8584, "ocr_batch_calls": 3000,
		"average_batch": 2.86, "visual_skips": 7000, "visual_confirmations": 100,
		"ocr_retries": 7, "decoder": "nvdec", "frame_pipeline_seconds": 35.0,
		"visual_seconds": 11.0, "encode_seconds": 4.0, "ocr_seconds": 90.0,
		"ocr_calls_per_cue": 10.28, "media_seconds": 239.0, "elapsed_seconds": 19.3,
		"realtime_speed": 12.4, "progress_percent": 25.6, "parallelism_selected": 8,
		"active_lanes": 8, "completed_lanes": 0, "total_lanes": 8, "boundary_merges": 2,
		"last_text": "测试字幕", "last_confidence": .95,
	}
	got := formatOCRTelemetry(telemetryFromLiveMap(live, 120))
	for _, want := range []string{"Danh sách 120 / 835 câu", "25.6% · mốc 03:59", "12.4×", "8584 ảnh", "3000 inference", "10.28 ảnh/câu", "8 chọn · 8 hoạt động · 0/8 hoàn tất", "NVIDIA NVDEC", "Gần nhất: 95% · 测试字幕"} {
		if !strings.Contains(got, want) {
			t.Fatalf("telemetry missing %q:\n%s", want, got)
		}
	}
}

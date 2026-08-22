from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCANNER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrScanner.cs"
CHECKPOINT = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrCheckpointStore.cs"
OCR_PAGE = ROOT / "csharp" / "src" / "BiliSubStudio.App" / "Pages" / "OcrPage.xaml.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL OCR scanner contract: " + message)


def main() -> int:
    scanner = SCANNER.read_text(encoding="utf-8")
    checkpoint = CHECKPOINT.read_text(encoding="utf-8")
    page = OCR_PAGE.read_text(encoding="utf-8")

    canonical_scan = "request = request with { Region = OcrCheckpointStore.CanonicalRegion(request.Region) };"
    require(canonical_scan in scanner, "scan request does not use restart-stable canonical ROI")
    require("var region = CanonicalRegion(request.Region);" in checkpoint,
            "checkpoint key does not use canonical ROI")

    set_result = scanner.find("job.SetResult(result);")
    pause_complete = scanner.find("job.PauseComplete(pauseMessage);")
    require(set_result >= 0 and pause_complete >= 0 and set_result < pause_complete,
            "paused job can become terminal before OcrScanResult is published")

    require('$"hwdownload,format=nv12|p010le|p016le,fps={fps},{crop}"' in scanner,
            "NVDEC filter does not download hardware frames before software fps/crop filters")

    similarity_start = scanner.find("private static double Similarity")
    require(similarity_start >= 0, "missing OCR lane similarity function")
    similarity_body = scanner[similarity_start:scanner.find("private static string FormatClock", similarity_start)]
    require("Intersect(" not in similarity_body and "previous[^1]" in similarity_body,
            "OCR boundary similarity is not order-sensitive Levenshtein")

    require("HardwareService.RecommendedOcrSegmentLanes(hardware)" in scanner,
            "segment topology does not have an independent CPU/RAM policy")
    require("HardwareService.RecommendedOcrWorkers(hardware, effectiveDevice)" in scanner and
            "HardwareService.RecommendedOcrWorkerProbeCeiling(hardware, effectiveDevice)" in scanner,
            "OCR worker pool does not have an independent device/VRAM live-probe policy")
    require("previousThroughput" not in scanner and "tăng dưới 10% throughput" not in scanner,
            "Auto topology still collapses real lanes using the invalid identical-frame throughput gate")
    select_start = scanner.find("private async Task<int> SelectParallelismAsync")
    worker_start = scanner.find("private async Task<int> SelectWorkerPoolAsync")
    require(select_start >= 0 and worker_start > select_start, "missing separate segment and worker selectors")
    segment_selector = scanner[select_start:worker_start]
    require("ConfigureWorkerPoolAsync" not in segment_selector and "configuredWorkers < level" not in segment_selector,
            "segment-lane selection still requires one Python worker per lane")
    require("FFmpeg lane → pool {configuredWorkers} OCR worker" in segment_selector,
            "Auto segment probe does not exercise N decoders through the shared M-worker pool")
    require("ConfigureWorkerPoolAsync(level" in scanner[worker_start:] and "Worker Probe" in scanner[worker_start:],
            "worker capacity is not live-probed independently")
    require("checkpoint.Lanes.Count != selected" in scanner,
            "scanner does not verify the committed FFmpeg segment topology")
    require("FFmpeg segment lane; dùng pool chung {configuredWorkers} Python worker" in scanner,
            "scanner commit log does not expose segment and worker topology separately")
    require("new SubtitleTracker(mode.Fps, mode.LowConfidence)" in scanner,
            "scan mode low-confidence threshold is not applied to subtitle tracking")
    require("var overlap = Math.Max(scanMode.Guard, scanMode.ActiveGuard);" in checkpoint,
            "scan mode guard is not applied to lane overlap")

    require("private OcrScanRequest? _checkpointRequest;" in page and
            "private OcrScanRequest? _activeRequest;" in page,
            "OCR page does not retain the exact request owning a paused checkpoint")
    require("CancelButton.IsEnabled = true;" in page and 'CancelButton.Content = "Đang hủy...";' in page,
            "OCR page disables or hides Cancel while cancellation cleanup runs")
    require("_checkpointRequest = paused ? request : null;" in page,
            "OCR page loses the paused checkpoint request at terminal polling")
    require("await _application.CancelOcrScanAsync(runningId, request, CancellationToken.None);" in page,
            "running Cancel does not wait for Core cleanup and verified checkpoint removal")
    require("checkpoint.Exists" in page and "Checkpoint OCR vẫn còn" in page,
            "paused Cancel/Restart does not verify checkpoint absence")
    require('RestartButton' in page and 'OcrScanStartMode.Fresh' in page and 'OcrScanStartMode.Resume' in page,
            "OCR UI does not expose explicit fresh/resume intent")
    require("ExportButton.IsEnabled = false;" in page,
            "partial paused OCR cues can incorrectly be exported as completed output")
    require("OcrScanTelemetry telemetry" in page and "{telemetry.SegmentLanes} FFmpeg lane" in page and
            "{telemetry.WorkerCount} worker" in page,
            "live OCR telemetry does not expose segment lanes and worker processes separately")

    require("File.Delete(path);" in checkpoint and "if (File.Exists(path)" in checkpoint,
            "checkpoint removal still swallows delete errors or skips absence verification")
    require("Where(x => x.Start <= media + 0.001)" in checkpoint,
            "paused checkpoint cues are not restricted to the contiguous safe frontier")

    print("PASS OCR independent segment/worker topology, verified cancel, explicit fresh/resume, safe-frontier, NVDEC, similarity and mode contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

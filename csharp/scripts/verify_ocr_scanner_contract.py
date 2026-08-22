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
    pause_complete = scanner.find("if (paused) job.PauseComplete(pauseMessage);")
    require(set_result >= 0 and pause_complete >= 0 and set_result < pause_complete,
            "paused job can become terminal before OcrScanResult is published")

    require('$"hwdownload,format=nv12|p010le|p016le,fps={fps},{crop}"' in scanner,
            "NVDEC filter does not download hardware frames before software fps/crop filters")

    similarity_start = scanner.find("private static double Similarity")
    require(similarity_start >= 0, "missing OCR lane similarity function")
    similarity_body = scanner[similarity_start:scanner.find("private static string FormatClock", similarity_start)]
    require("Intersect(" not in similarity_body and "previous[^1]" in similarity_body,
            "OCR boundary similarity is not order-sensitive Levenshtein")

    require("HardwareService.RecommendedOcrLanes(hardware, effectiveDevice)" in scanner,
            "scanner topology does not follow the effective OCR device")
    require("HardwareService.RecommendedOcrProbeCeiling(hardware, effectiveDevice)" in scanner and
            "x <= probeCeiling" in scanner,
            "Auto topology prediction is still a hard cap instead of a live probe ceiling")
    require("previousThroughput" not in scanner and "tăng dưới 10% throughput" not in scanner,
            "Auto topology still collapses real lanes using the invalid identical-frame throughput gate")
    require("configuredWorkers < level" in scanner and "configuredWorkers < selected" in scanner,
            "scanner does not require a real Python worker for every committed lane")
    require("request.Duration * (index + 0.5) / level" in scanner and
            "{level} pipeline FFmpeg+OCR đồng thời PASS" in scanner,
            "Auto Probe is not exercising one real decode+OCR pipeline per candidate segment")
    require("checkpoint.Lanes.Count != selected" in scanner,
            "scanner does not verify the committed FFmpeg segment topology")
    require("FFmpeg lane + {configuredWorkers} Python worker" in scanner,
            "scanner commit log does not expose real process topology")
    require("new SubtitleTracker(mode.Fps, mode.LowConfidence)" in scanner,
            "scan mode low-confidence threshold is not applied to subtitle tracking")
    require("var overlap = Math.Max(scanMode.Guard, scanMode.ActiveGuard);" in checkpoint,
            "scan mode guard is not applied to lane overlap")

    require("private OcrScanRequest? _pausedRequest;" in page,
            "OCR page does not retain the exact request owning a paused checkpoint")
    require("CancelButton.IsEnabled = paused;" in page,
            "OCR page disables Cancel when a scan reaches PAUSED")
    require("_pausedRequest = paused ? request : null;" in page,
            "OCR page loses the paused checkpoint request at terminal polling")
    require("await _application.RemoveOcrCheckpointAsync(pausedRequest, CancellationToken.None);" in page,
            "Cancel cannot delete a paused OCR checkpoint")
    require("ExportButton.IsEnabled = !paused && _cues.Count > 0;" in page,
            "partial paused OCR cues can incorrectly be exported as completed output")
    require("{ocrStatus.Workers} Python worker" in page,
            "live OCR telemetry does not expose the actual worker-process count")

    print("PASS OCR scanner real-process topology, checkpoint, Auto probe, paused-cancel, NVDEC, similarity, device and mode-policy contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

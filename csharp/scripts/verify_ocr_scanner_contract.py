from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCANNER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrScanner.cs"
CHECKPOINT = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrCheckpointStore.cs"
OCR_PAGE = ROOT / "csharp" / "src" / "BiliSubStudio.App" / "Pages" / "OcrPage.xaml.cs"
MANAGER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrManager.cs"
TOPOLOGY = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrTopologyBenchmark.cs"
RESOURCE_POLICY = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrAutoResourcePolicy.cs"
HARDWARE = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Hardware" / "HardwareService.cs"
APPLICATION = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Application" / "BiliSubApplication.cs"
PROCESS_GROUP = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Processes" / "OwnedProcessGroup.cs"
PROCESS_RUNNER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Processes" / "ProcessRunner.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL OCR scanner contract: " + message)


def main() -> int:
    scanner = SCANNER.read_text(encoding="utf-8")
    checkpoint = CHECKPOINT.read_text(encoding="utf-8")
    page = OCR_PAGE.read_text(encoding="utf-8")
    manager = MANAGER.read_text(encoding="utf-8")
    topology = TOPOLOGY.read_text(encoding="utf-8")
    resource_policy = RESOURCE_POLICY.read_text(encoding="utf-8")
    hardware = HARDWARE.read_text(encoding="utf-8")
    application = APPLICATION.read_text(encoding="utf-8")
    process_group = PROCESS_GROUP.read_text(encoding="utf-8")
    process_runner = PROCESS_RUNNER.read_text(encoding="utf-8")

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

    require("Levels { get; } = [1, 2, 4, 8, 16]" in topology,
            "OCR Auto benchmark ladder is not exactly 1/2/4/8/16")
    require("await restore(best, cancellationToken);" in topology and
            "rejected(level, best, error);" in topology and
            "return best;" in topology,
            "failed OCR Auto level does not restore and retain the last PASS topology")
    select_start = scanner.find("private async Task<int> SelectParallelismAsync")
    probe_start = scanner.find("private async Task<OcrTopologyProbe> ProbeTopologyLevelAsync")
    require(select_start >= 0 and probe_start > select_start, "missing full OCR topology benchmark selector/probe")
    selector = scanner[select_start:probe_start]
    probe = scanner[probe_start:scanner.find("private static void PublishTelemetry", probe_start)]
    require("OcrTopologyBenchmark.SelectAsync" in selector and "EnsureResourceHeadroom" in selector and
            "HasUsefulThroughputGain" in selector,
            "OCR Auto does not execute Predict -> Probe -> throughput Commit across the fixed ladder")
    require("GlobalMemoryStatusEx" in hardware and "nvmlDeviceGetMemoryInfo" in hardware,
            "OCR Auto cannot read live Windows RAM and NVIDIA VRAM headroom")
    require("GpuWorkerRamBytes" in resource_policy and "GpuWorkerVramBytes" in resource_policy and
            "ramReserve" in resource_policy and "vramReserve" in resource_policy and
            "MinimumThroughputGain = 0.10" in resource_policy,
            "OCR Auto resource policy is missing reviewed RAM/VRAM reserves or throughput threshold")
    require("Math.Min(explicitValue" not in selector and
            "ProbeTopologyLevelAsync(ffmpeg, request, explicitValue" in selector,
            "manual OCR topology is still silently downgraded instead of probing the exact request")
    require("ConfigureWorkerPoolAsync(level" in probe and "actual != level" in probe and
            "Enumerable.Range(0, level)" in probe and "CaptureFrameWithFfmpegAsync" in probe and
            "_ocr.RunAsync" in probe and "RunRoundAsync(0)" in probe and "RunRoundAsync(2)" in probe and
            "_ocr.Status.Workers != level" in probe,
            "each OCR Auto level is not a real N-worker plus N-FFmpeg/OCR concurrent probe")
    require("ConfigureWorkerPoolAsync(best" in selector and "restored != best" in selector,
            "scanner does not verify rollback to the last PASS worker topology")
    require("configuredWorkers != selected" in scanner,
            "scan can start before worker count equals the benchmark-selected lane count")
    require("ResizePoolLockedAsync" in manager and "var retained = _workers.Count;" in manager and
            "index >= retained" in manager and "RebuildAvailabilityLocked();" in manager,
            "worker scale-up failure does not transactionally preserve the prior PASS pool")
    require('"hybrid" => target == 1' in manager and '? ["gpu"]' in manager,
            "Hybrid mode cannot participate in the mandatory level-1 benchmark")
    require("checkpoint.Lanes.Count != selected" in scanner,
            "scanner does not verify the committed FFmpeg segment topology")
    require("Math.Clamp(parallelism, 1, 16)" in checkpoint and "duration / 120" not in checkpoint,
            "checkpoint construction silently reduces the benchmark-selected topology by video duration")
    require("benchmark xong, khóa {selected} pipeline" in scanner and
            "{selected} FFmpeg lane + {configuredWorkers} Python worker" in scanner,
            "scanner commit log does not expose the benchmark-selected full topology")
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
    require("await _ocr.StopAsync(cancellationToken);" in application and
            "_ocr.Status.Workers != 0" in application and "_ocrScanner.ActiveProcessCount != 0" in application,
            "OCR Cancel can report completion before Python/FFmpeg ownership reaches zero")
    require("await processes.StopAsync();" in scanner and "await _ocr.StopAsync();" in scanner and
            scanner.find("await processes.StopAsync();") < scanner.find("await _checkpoints.RemoveAsync(request, CancellationToken.None);"),
            "OCR cancellation does not reap process trees before deleting checkpoint")
    require("class OwnedProcessGroup" in process_group and "Kill(entireProcessTree: true)" in process_group and
            "ActiveCount" in process_group and "owner?.Track(process)" in process_runner,
            "OCR FFmpeg process trees are not tracked and reaped as one owned group")
    require("checkpoint.Exists" in page and "Checkpoint OCR vẫn còn" in page,
            "paused Cancel/Restart does not verify checkpoint absence")
    require('RestartButton' in page and 'OcrScanStartMode.Fresh' in page and 'OcrScanStartMode.Resume' in page,
            "OCR UI does not expose explicit fresh/resume intent")
    require("ExportButton.IsEnabled = false;" in page,
            "partial paused OCR cues can incorrectly be exported as completed output")
    require("OcrBenchmarkTelemetry benchmark" in page and "Benchmark {benchmark.Candidate}/{benchmark.Maximum}" in page,
            "OCR page does not expose the current Auto benchmark level")
    require("OcrScanTelemetry telemetry" in page and "{telemetry.SegmentLanes} FFmpeg lane" in page and
            "{telemetry.WorkerCount} worker" in page,
            "live OCR telemetry does not expose the committed FFmpeg/Python topology")

    require("File.Delete(path);" in checkpoint and "if (File.Exists(path)" in checkpoint,
            "checkpoint removal still swallows delete errors or skips absence verification")
    require("Where(x => x.Start <= media + 0.001)" in checkpoint,
            "paused checkpoint cues are not restricted to the contiguous safe frontier")

    print("PASS OCR Predict/Probe/Commit 1/2/4/8/16 benchmark, RAM/VRAM/throughput gate, exact topology, owned-process cleanup, transactional cancel, safe-frontier and NVDEC contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

from __future__ import annotations

import pathlib
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCANNER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrScanner.cs"
FRAME_EXTRACTOR = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrFrameExtractor.cs"
CHECKPOINT = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrCheckpointStore.cs"
MODELS = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrModels.cs"
OCR_PAGE = ROOT / "csharp" / "src" / "BiliSubStudio.App" / "Pages" / "OcrPage.xaml.cs"
OCR_PAGE_XAML = ROOT / "csharp" / "src" / "BiliSubStudio.App" / "Pages" / "OcrPage.xaml"
MANAGER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrManager.cs"
WORKER_CLIENT = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrWorkerClient.cs"
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
    frame_extractor = FRAME_EXTRACTOR.read_text(encoding="utf-8")
    checkpoint = CHECKPOINT.read_text(encoding="utf-8")
    models = MODELS.read_text(encoding="utf-8")
    page = OCR_PAGE.read_text(encoding="utf-8")
    page_xaml = OCR_PAGE_XAML.read_text(encoding="utf-8")
    manager = MANAGER.read_text(encoding="utf-8")
    worker_client = WORKER_CLIENT.read_text(encoding="utf-8")
    topology = TOPOLOGY.read_text(encoding="utf-8")
    resource_policy = RESOURCE_POLICY.read_text(encoding="utf-8")
    hardware = HARDWARE.read_text(encoding="utf-8")
    application = APPLICATION.read_text(encoding="utf-8")
    process_group = PROCESS_GROUP.read_text(encoding="utf-8")
    process_runner = PROCESS_RUNNER.read_text(encoding="utf-8")
    worker = (ROOT / "internal" / "ocr" / "worker.py").read_text(encoding="utf-8")

    canonical_scan = "request = request with { Region = OcrCheckpointStore.CanonicalRegion(request.Region) };"
    require(canonical_scan in scanner, "scan request does not use restart-stable canonical ROI")
    require("var region = CanonicalRegion(request.Region);" in checkpoint,
            "checkpoint key does not use canonical ROI")

    set_result = scanner.find("job.SetResult(result);")
    pause_complete = scanner.find("job.PauseComplete(pauseMessage);")
    require(set_result >= 0 and pause_complete >= 0 and set_result < pause_complete,
            "paused job can become terminal before OcrScanResult is published")

    # 320e07b: the fps filter was removed because it rewrites PTS (drop/duplicate) and
    # corrupts provenance. Adaptive sampling happens in C# on absolute PTS via
    # settb + -fps_mode passthrough; the exact timebase contract is tested by
    # OcrTimeBaseContract in the Core contract suite.
    require('$"hwdownload,format=nv12|p010le|p016le,settb=expr={tb},{crop},showinfo=checksum=0"' in scanner,
            "NVDEC lane must pin the source timebase before showinfo so sampled PTS stay absolute")
    require(scanner.count("showinfo=checksum=0") >= 4 and "showinfo=checksum=0" in frame_extractor,
            "all OCR showinfo paths must disable unused frame checksums")
    require('"-fps_mode", "passthrough"' in scanner,
            "OCR lane output must pass frames through without PTS rewriting")
    require("var probedFrameDuration = videoProbe is null ? 0 : videoProbe.FrameDurationSeconds;" in scanner
            and "if (!(dur > 0)) dur = nativeFrameDuration;" in scanner
            and "1 / mode.Fps;" in scanner,
            "missing-duration fallback must use the native ffprobe frame duration, never the sampling FPS")
    lane_filter = scanner.split("private static IReadOnlyList<string> BuildLaneArguments", 1)[1].split(
            "private static double Similarity", 1)[0]
    require("fps=" not in lane_filter,
            "OCR lane filter graph must not reintroduce a PTS-rewriting fps filter")

    similarity_start = scanner.find("private static double Similarity")
    require(similarity_start >= 0, "missing OCR lane similarity function")
    similarity_body = scanner[similarity_start:scanner.find("private static string FormatClock", similarity_start)]
    require("Intersect(" not in similarity_body and "previous[^1]" in similarity_body,
            "OCR boundary similarity is not order-sensitive Levenshtein")

    require("Levels { get; } = [1, 2, 4, 8, 16]" in topology,
            "OCR Auto base benchmark ladder is not exactly 1/2/4/8/16")
    require("HybridLevels { get; } = [2, 4, 8, 16]" in topology and
            "SelectForModeAsync" in topology and "HybridLevels : Levels" in topology,
            "OCR Hybrid benchmark can still commit a fake one-worker GPU-only topology")
    require("await restore(best, cancellationToken);" in topology and
            "rejected(level, best, error);" in topology and
            "Enumerable.Range(best + 1, level - best - 1).Reverse()" in topology and
            "return fallback;" in topology,
            "failed OCR Auto level does not restore then probe descending intermediate topologies")
    select_start = scanner.find("private async Task<int> SelectParallelismAsync")
    probe_start = scanner.find("private async Task<OcrTopologyProbe> ProbeTopologyLevelAsync")
    require(select_start >= 0 and probe_start > select_start, "missing full OCR topology benchmark selector/probe")
    selector = scanner[select_start:probe_start]
    probe = scanner[probe_start:scanner.find("private static void PublishTelemetry", probe_start)]
    require("OcrTopologyBenchmark.SelectForModeAsync" in selector and "EnsureResourceHeadroom" in selector and
            "HasUsefulThroughputGain" in selector,
            "OCR Auto does not execute Predict -> Probe -> throughput Commit across the base ladder and fallbacks")
    require("GlobalMemoryStatusEx" in hardware and "nvmlDeviceGetMemoryInfo" in hardware,
            "OCR Auto cannot read live Windows RAM and NVIDIA VRAM headroom")
    require("GpuWorkerRamBytes" in resource_policy and "GpuWorkerVramBytes" not in resource_policy and
            "observedVramPerGpuWorkerBytes" in resource_policy and "VramGrowthSafetyFactor = 1.25" in resource_policy and
            "ramReserve" in resource_policy and "vramReserve" in resource_policy and
            "MinimumThroughputGain = 0.10" in resource_policy,
            "OCR Auto resource policy is not using machine-measured VRAM growth with reviewed reserve/throughput safety")
    require("beforeResources" in selector and "afterResources" in selector and
            "observedVramPerGpuWorkerBytes" in selector and "postProbe: true" in selector and
            "beforeResources.AvailableVramBytes - afterResources.AvailableVramBytes" in selector,
            "OCR Auto does not learn machine-specific VRAM growth and re-check actual reserve after a real topology probe")
    require("Math.Min(explicitValue" not in selector and
            "ProbeTopologyLevelAsync(ffmpeg, request, explicitValue" in selector,
            "manual OCR topology is still silently downgraded instead of probing the exact request")
    require('<ComboBox Grid.Column="3" x:Name="LanesBox" Header="Luồng OCR" SelectedIndex="0"><x:String>auto</x:String><x:String>1</x:String><x:String>2</x:String><x:String>3</x:String><x:String>4</x:String><x:String>5</x:String><x:String>6</x:String><x:String>7</x:String><x:String>8</x:String><x:String>16</x:String></ComboBox>' in page_xaml,
            "manual OCR topology selector does not expose auto, every level 1..8, and 16")
    require("ConfigureWorkerPoolAsync(workerTarget" in probe and "actual != workerTarget" in probe and
            "Enumerable.Range(0, level)" in probe and "CaptureFrameWithFfmpegAsync" in probe and
            "_ocr.RunAsync" in probe and "RunRoundAsync(0)" in probe and "RunRoundAsync(2)" in probe and
            "_ocr.Status.Workers != workerTarget" in probe,
            "each OCR Auto level must probe N FFmpeg lanes through the independent worker target")
    require("ConfigureWorkerPoolAsync(workerTarget" in selector and "restored != workerTarget" in selector,
            "scanner does not verify rollback to the last PASS worker topology")
    require("configuredWorkers != checkpoint.Workers" in scanner and
            "WorkerTarget(_ocr.Status.ActiveMode, selected)" in scanner and
            "Math.Min(2, lanes)" in topology,
            "scan must validate independent checkpoint worker and decoder topology")
    queue = (MANAGER.parent / "OcrInferenceQueue.cs").read_text(encoding="utf-8")
    require("BoundedChannelFullMode.Wait" in queue and "Capacity = 12" in queue and
            "TaskCompletionSource<T>" in queue and "WriteAsync" in queue and
            "inference.RunAsync" in manager and "RetireInferenceLocked" in manager,
            "all OCR requests must use bounded zero-drop backpressure and per-request completion")
    require("ResizePoolLockedAsync" in manager and "var retained = _workers.Count;" in manager and
            "index >= retained" in manager and "RebuildAvailabilityLocked();" in manager,
            "worker scale-up failure does not transactionally preserve the prior PASS pool")
    require("private int _committedWorkerTarget = 1;" in manager and
            "Math.Max(_committedWorkerTarget, MinimumWorkerTarget(mode))" in manager and
            'BuildPoolLockedAsync("cpu", _committedWorkerTarget' in manager and
            "_committedWorkerTarget = target;" in manager,
            "a failed OCR pool can silently rebuild one worker instead of the last committed topology")
    require("private const int MaxLaneRecoveryAttempts = 2;" in scanner and
            "recoveryAttempts < MaxLaneRecoveryAttempts" in scanner and
            "laneRecoveryGate.WaitAsync(token)" in scanner and
            "ConfigureWorkerPoolAsync(effectiveWorkers, token)" in scanner and
            "restored != effectiveWorkers" in scanner and "SaveRecoverableCheckpointAsync();" in scanner and
            "khôi phục PASS" in scanner,
            "long OCR lanes do not retry from a safe checkpoint after restoring the exact worker topology")
    require("checkpointSaveGate.WaitAsync(CancellationToken.None)" in scanner and
            "checkpointSaveGate.Release();" in scanner,
            "periodic and recovery checkpoint publications can race on the same temporary file")
    require('"hybrid" when target >= 2' in manager and
            'new[] { "gpu", "cpu" }' in manager and
            '"hybrid" => throw new ArgumentOutOfRangeException' in manager and
            'MinimumWorkerTarget(string mode) => mode == "hybrid" ? 2 : 1' in manager,
            "Hybrid manager can still accept a GPU-only one-worker pool")
    require("resumedGpuFallback" in scanner and
            'ConfigureDeviceAsync(saved?.WorkerMode ?? request.Device' in scanner and
            "SelectForModeAsync" in scanner and "không giữ nhãn Hybrid giả" in scanner,
            "legacy checkpoint or failed Hybrid probe can still be mislabeled as Hybrid")
    require("checkpoint.Lanes.Count != selected" in scanner,
            "scanner does not verify the committed FFmpeg segment topology")
    require("Math.Clamp(parallelism, 1, 16)" in checkpoint and "duration / 120" not in checkpoint,
            "checkpoint construction silently reduces the benchmark-selected topology by video duration")
    require("benchmark xong, khóa {selected} pipeline" in scanner and
            "{selected} FFmpeg lane + {effectiveWorkers} Python worker" in scanner,
            "scanner commit log does not expose the benchmark-selected full topology")
    require("new SubtitleTracker(mode.Fps, mode.LowConfidence, exactFrameTiming: mode.AdaptiveTiming" in scanner,
            "scan mode low-confidence threshold or exact-frame timing policy is not applied to subtitle tracking")
    require('"fast" or "nhanh" => new OcrScanMode(1.5, 8, 24, 0.22, 0.58, AdaptiveTiming: true)' in checkpoint and
            '_ => new OcrScanMode(2.5, 5, 16, 0.16, 0.62, AdaptiveTiming: true)' in checkpoint,
            "Balanced/Fast OCR can still quantize timestamps or skip short captions between sampled frames")
    require("var overlap = Math.Max(scanMode.Guard, scanMode.ActiveGuard);" in checkpoint,
            "scan mode guard is not applied to lane overlap")

    live_publish_start = scanner.find("private static void PublishTelemetry")
    live_publish_end = scanner.find("private async Task<OcrLaneCheckpoint> RunLaneWithFallbackAsync", live_publish_start)
    live_publish = scanner[live_publish_start:live_publish_end]
    live_lane_start = scanner.find("private async Task<OcrLaneCheckpoint> RunLaneAsync")
    live_lane_end = scanner.find("private static IReadOnlyList<OcrCue> Reconcile", live_lane_start)
    live_lane = scanner[live_lane_start:live_lane_end]
    require('args.AddRange(["-to", end.ToString(' in scanner and
            'args.AddRange(["-t", (end - start)' not in scanner,
            "copyts OCR lane output still stops on a relative duration instead of absolute PTS")
    require("ValidateLaneCoverage(segment, frames, mediaSeconds, lastFrameDuration);" in live_lane and
            live_lane.index("ValidateLaneCoverage(segment, frames, mediaSeconds, lastFrameDuration);") <
            live_lane.index("mediaSeconds = segment.CoreEnd;"),
            "OCR lane marks completion without verifying decoded frame coverage")
    require("liveCommitted" in scanner and "liveActive" in scanner and
            "recentCues" in live_publish and "new OcrScanResult(" in live_publish,
            "running OCR does not publish a bounded live cue snapshot through AppJob.Result")
    live_seed = scanner[scanner.find("var liveCommitted = checkpoint.Lanes"):scanner.find("var liveActive =", scanner.find("var liveCommitted = checkpoint.Lanes"))]
    require("TakeLast(120)" not in live_seed and "liveCommitted.RemoveRange" not in scanner and
            "TakeLast(previewCueLimit)" in live_publish and "const int previewCueLimit = 120" in live_publish,
            "future-lane callbacks can evict safe-frontier cues before the bounded live snapshot is built")
    require(".Concat(liveActive.Where(cue => cue is not null).Select(cue => cue!))" in live_publish,
            "live OCR preview hides the tracker-confirmed active cue until a later subtitle commits it")
    require(".Where(cue => cue.Start <= frontier + .001)" in live_publish,
            "live OCR preview exposes speculative cues beyond the contiguous safe frontier")
    require("publishedCueCount" in live_lane and "tracker.Cues.Skip(publishedCueCount)" in live_lane and
            "Active = tracker.Active" in live_lane and "}, committedCues, tracker.CanCheckpoint);" in live_lane,
            "tracker-confirmed committed/active OCR text is not streamed while scanning")
    require("snapshot.Result is OcrScanResult result" in page and
            "MergeLiveCueSnapshot(result.Cues)" in page and "OcrLiveCueAccumulator _liveCues" in page and
            "RefreshLiveCueView(force: snapshot.Done)" in page and "now.AddSeconds(2)" in page,
            "OCR page does not incrementally retain and throttle live cue rendering")
    # 2941804: live preview consumes the pixel-gated CommittedView; touching-identical
    # reconciliation of the FINAL SRT happens in ReconcileAsync (line: MergeTouchingIdentical
    # over the owned output) plus the pixel-gated MergeTouchingIdenticalAsync path.
    require("OcrLiveCueAccumulator" in page and
            "OcrCueReconciler.MergeTouchingIdentical(output.Select(item => item.Cue))" in scanner and
            "OcrCueReconciler.MergeTouchingIdenticalAsync(" in scanner and
            "liveTail.AppendCommitted(owned)" in scanner and
            "liveTail.GetTailBelow(frontier, previewCueLimit)" in live_publish and
            "const int previewCueLimit = 120" in live_publish,
            "identical touching OCR fragments are not reconciled consistently in live history and final SRT")
    require("recoverShortBlank: tracker.Active is not null" in live_lane,
            "active short-caption recovery no longer requests a bounded tighter-box retry")
    require("adaptiveWindow" in live_lane and "NeedsAdaptiveRefinement" in live_lane and
            "ProcessAdaptiveWindowAsync" in live_lane and "RunBatchAsync" in live_lane and
            "offset += OcrRequestBatcher.WorkerBatchLimit" in live_lane and "images += batch.Length" in live_lane and
            "TimeSpan.FromMilliseconds(500)" in live_lane,
            "OCR adaptive/exhaustive paths must preserve batching and throttled telemetry")
    require("previousVisualAnchor" in live_lane and "ProbeVisualChangesAsync" in live_lane and
            "SelectAdaptiveRefinementFrames(scores, ref pendingVisualFollowups)" in live_lane and
            'request.get("probe_images_base64")' in worker and
            "visual_change_scores" in worker and "cv2.Canny" in worker,
            "stable adaptive intervals do not cheaply inspect every frame and nominate visual transitions")
    require("Math.Max(0, index - 1)" in scanner and "nearby <= index + 2" in scanner and
            "pendingFollowups - scores.Count" in scanner and
            "if (!tracker.CanCheckpoint)" in live_lane and
            "pendingVisualFollowups = Math.Max(pendingVisualFollowups, 2);" in live_lane,
            "adaptive confirmation must include pre-boundary evidence and carry unresolved samples without duplicating the baseline")
    require("mode.Fps >= 4" in live_lane and
            "IsDenseAdaptiveVisualNoise(scores)" in live_lane and
            "CapRefinementIndexes(candidateIndexes, windowLength, maximum: 4)" in live_lane and
            "CapRefinementIndexes(candidateIndexes, adaptiveWindow.Count - 1, maximum: mode.Fps >= 4 ? 4 : 6)" in live_lane and
            "maximum: mode.Fps >= 4 ? 2 : 5" in live_lane,
            "Accurate must skip dense stable-text animation and cap sparse refinement without changing lower modes")
    require("!string.Equals(previousText, currentText, StringComparison.Ordinal)" in scanner,
            "similar but changed captions must trigger recognition refinement, not be declared continuous")
    require('x:Name="ScanModeBox"' in page_xaml and 'Tag="accurate"' in page_xaml and
            'Tag="balanced"' in page_xaml and 'Tag="fast"' in page_xaml and
            page_xaml.index('Tag="accurate"') < page_xaml.index('Tag="balanced"'),
            "OCR UI must expose a mode selector with accurate as the first (default) option")
    require("ExhaustiveRecognition: true" not in checkpoint and
            '"accurate" or "precise" or "chinh-xac" => new OcrScanMode(' in checkpoint and
            "4, 3, 12, 0.10, 0.68, AdaptiveTiming: true)" in checkpoint and
            "kiểm tra mọi khung hình" in page_xaml,
            "Accurate OCR must probe every native frame without exhaustive Medium-model inference")
    ns = {"ui": "http://schemas.microsoft.com/winfx/2006/xaml/presentation"}
    name = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
    root = ET.fromstring(page_xaml)
    settings = next(grid for grid in root.iterfind(".//ui:Grid", ns)
                    if any(child.get(name) == "DeviceBox" for child in grid))
    columns = settings.find("ui:Grid.ColumnDefinitions", ns)
    boxes = settings.findall("ui:ComboBox", ns)
    mode_box = next(box for box in boxes if box.get(name) == "ScanModeBox")
    require(columns is not None and len(columns) == 4 and
            all(column.get("Width", "*") == "*" for column in columns) and
            [(box.get(name), box.get("Grid.Column", "0")) for box in boxes] ==
            [("DeviceBox", "0"), ("ScanModeBox", "1"), ("SourceBox", "2"), ("LanesBox", "3")] and
            mode_box.get("SelectedIndex") == "0",
            "OCR settings must expose device/mode/lanes columns with the mode selector defaulting to index 0")
    build_request = page.split("private OcrScanRequest BuildRequest() => new(", 1)[1].split(");", 1)[0]
    require("Selected(ScanModeBox)," in build_request and '"balanced"' not in build_request and
            "_checkpointRequest ?? BuildRequest()" in page,
            "new UI requests must read the selected scan mode instead of hard-coding balanced")
    require("region, request.Mode.Trim().ToLowerInvariant(), mode.Fps" in checkpoint and
            '"accurate" or "precise" or "chinh-xac"' in checkpoint and
            '"fast" or "nhanh"' in checkpoint,
            "legacy checkpoint mode identities must remain distinct from balanced")
    require('["images_base64"] = imageBase64' in worker_client and
            "resultsNode.EnumerateArray().Select(ParseResult)" in worker_client and
            "parsed.Length != imageBase64.Count" in worker_client and
            "RunBatchAsync(imageBase64" in manager,
            "C# OCR worker path does not validate and expose the eight-image Paddle batch protocol")
    require('["probe_images_base64"] = imageBase64' in worker_client and
            "change_scores" in worker_client and "ProbeVisualChangesExAsync(imageBase64" in manager,
            "C# OCR worker path does not validate and expose the every-frame visual probe protocol")
    require("_probeGate" in manager and "IsMemoryPressure" in manager and
            "CombineProbeResults" in manager and "imageBase64.Count > 2" in manager and
            "first.ChangeScores.Concat(second.ChangeScores)" in manager,
            "visual probe cannot split an out-of-memory batch without losing transition scores")
    require("var authoritative = snapshot.Done" in page and
            "ApplyAuthoritativeCues(result.Cues)" in page and
            "ExportCurrentSrtAsync" in page and "_cues, outputDirectory" in page and "ExportButton.IsEnabled = false;" in page,
            "provisional live active cues can leak into export instead of being replaced by the final authoritative scan result")
    require("RecoverableCues" in page and "ApplyRecoverableCheckpointCues(checkpoint)" in page and
            '"_Chinese.partial.srt"' in page and "ExportButton.IsEnabled = _cues.Count > 0;" in page,
            "OCR checkpoint cannot be exported as an explicitly partial recovery SRT")
    require("AutoSaveDirectoryBox" in page_xaml and "ChooseAutoSaveFolder_Click" in page_xaml and
            "OCR hoàn tất sẽ tự lưu SRT" in page_xaml and "IFolderPickerService" in page and
            "SetOcrOutputDirectoryAsync" in page and "ExportCurrentSrtAsync(autoSaveDirectory, recovery: false, exportRequest: request)" in page and
            "Đã quét xong và tự lưu SRT" in page,
            "OCR page does not expose and persist a dedicated auto-save folder or export final SRT automatically")
    require('path + ".tmp-" + Guid.NewGuid().ToString("N")' in application and
            "File.Move(temporary, path);" in application,
            "OCR SRT publication is not atomic")
    require("OcrScanner.ReconcileAsync(checkpoint.Lanes" in checkpoint and "RecoverableCues" in models,
            "OCR checkpoint inspection does not expose all committed safe-frontier cues")
    require("catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)" in application and
            "!laneCancellation.IsCancellationRequested" in scanner and "error is OperationCanceledException" in scanner,
            "an internal OCR lane cancellation can still be mislabeled as a user cancel")
    # a5829d9: virtualized incremental cue list. The full ordered history is retained in
    # _cues/_visibleCues; rows render incrementally with the same compact start/end format.
    require("TakeLast(120)" not in page and "Take(10)" not in page and
            "OcrExportPipeline.Build(_cues, _displayExclusions, _displayFixes)" in page and
            "private static string FormatClock(double seconds)" in page and
            'value.ToString(@"hh\\:mm\\:ss")' in page,
            "OCR page does not retain the complete compact start/end subtitle history")
    # a5829d9: the fixed "10 cues per viewport" window became a continuously scrollable
    # full-history list inside the same 372px compact frame.
    require('Height="372"' in page_xaml and "Phụ đề OCR đã quét" in page_xaml and
            'ScrollViewer.VerticalScrollBarVisibility="Auto"' in page_xaml and
            '<Setter Property="Height" Value="32" />' in page_xaml and
            'HorizontalContentAlignment" Value="Stretch"' in page_xaml and
            'TextTrimming="CharacterEllipsis"' in page_xaml and 'ToolTipService.ToolTip="{Binding}"' in page_xaml,
            "OCR cue history is not isolated in a fixed compact scrollable viewport")

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
            scanner.find("await processes.StopAsync();") < scanner.find("await _checkpoints.RemoveAsync(keyRequest, CancellationToken.None);"),
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
    require("private const int Schema = 19;" in checkpoint and "schema >= 9" in checkpoint and
            "LegacyCheckpointIdentity" in checkpoint and
            "new[] { 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3 }" in checkpoint and
            "OcrModelTier.DetectionModel(request.Mode), OcrModelTier.RecognitionModel(request.Mode)" in checkpoint,
            "adaptive OCR checkpoint identity cannot safely reject and explicitly remove legacy checkpoint files")
    require("Where(x => x.Start <= media + 0.001)" in checkpoint,
            "paused checkpoint cues are not restricted to the contiguous safe frontier")

    print("PASS OCR Predict/Probe/Commit base 1/2/4/8/16 plus descending fallbacks, machine-measured VRAM growth/reserve/throughput gate, full scrollable SRT-style live cue history including the current confirmed active cue, final-authoritative export isolation, exact topology, owned-process cleanup, transactional cancel, safe-frontier and NVDEC contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

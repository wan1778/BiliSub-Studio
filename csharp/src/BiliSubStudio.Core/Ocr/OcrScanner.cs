using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Ocr;

public sealed class OcrScanner
{
    private const double AdaptiveVisualChangeThreshold = .02;
    // A sampled text change is independent evidence that a real boundary sits
    // inside this window, so the visual confirmation bar drops: trace-proven
    // one-rune H.264 boundaries score ~0.019 while grain background stays at
    // ~0.001-0.007. Steady windows keep the strict 0.02 bar. Caps are unchanged.
    private const double AdaptiveTransitionChangeThreshold = .015;
    private const int MaxLaneRecoveryAttempts = 2;
    // NVDEC-retry ladder (17/10, 0.08x crawl failure): an ffmpeg lane dying mid-stream
    // (ExitCode != 0, decode corruption/driver hiccup) previously dropped PERMANENTLY to
    // software until the end of the segment — 4/8 lanes CPU-ized on a 7h39m video.
    // Now: up to MaxNvdecLaneAttempts consecutive NVDEC retries (each resuming from a
    // safe checkpoint, backoff 1.5/3/6s for the driver to recover), and only when the ladder is exhausted
    // software falls back for the first time + telemetry counts clearly to make it visible.
    private const int MaxNvdecLaneAttempts = 3;
    private const double NvdecRetryBackoffSeconds = 1.5;
    // Spawn 8 lanes staggered to avoid the race for NVDEC sessions at start/resume
    // (8 decoders + 2 CUDA worker contexts being created at the same beat used to break sessions).
    private const int LaneSpawnStaggerMs = 150;
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly OcrManager _ocr;
    private readonly HardwareService _hardware;
    private readonly OcrCheckpointStore _checkpoints;
    private readonly OcrSidecarStore _sidecars;
    private readonly string _appDataDir;
    private OwnedProcessGroup? _activeProcesses;
    // App-lifetime probe/sidecar cache: video metadata is static, key = file hash
    // (changing the file is a miss). Null is memory-cache only (a transient error does not poison disk).
    private readonly ConcurrentDictionary<string, VideoProbe?> _probeCache = new();
    private readonly ConcurrentDictionary<string, OcrSidecar.SidecarDocument?> _sidecarDocs = new();

    internal OcrScanner(ToolManager tools, ProcessRunner processes, OcrManager ocr, HardwareService hardware, OcrCheckpointStore checkpoints, AppPaths paths)
    {
        _tools = tools;
        _processes = processes;
        _ocr = ocr;
        _hardware = hardware;
        _checkpoints = checkpoints;
        _sidecars = new OcrSidecarStore(paths.Data);
        _appDataDir = paths.Data;
    }

    public async Task<OcrResult> RecognizeFrameAsync(string path, double at, OcrRegion region, string device, CancellationToken cancellationToken)
    {
        await using var processes = new OwnedProcessGroup();
        await _ocr.ConfigureDeviceAsync(device, cancellationToken);
        await _ocr.EnsureAsync(cancellationToken);
        var jpeg = await CaptureFrameAsync(path, at, region, enhanced: false, cancellationToken, processes);
        var result = FilterOffBaselineOverlayLines(await _ocr.RunAsync(Convert.ToBase64String(jpeg), cancellationToken), region);
        if (NeedsEnhancedRecognition(result, .68))
        {
            var enhanced = await CaptureFrameAsync(path, at, region, enhanced: true, cancellationToken, processes);
            var alternate = FilterOffBaselineOverlayLines(await _ocr.RunAsync(Convert.ToBase64String(enhanced), cancellationToken), region);
            if (alternate.Ok && alternate.Confidence > result.Confidence) result = alternate;
        }
        return result;
    }

    public async Task<OcrScanResult> RunAsync(AppJob job, OcrScanRequest request, OcrScanStartMode startMode, OcrRegion? resumeRegionOverride = null)
    {
        request = request with { Region = OcrCheckpointStore.CanonicalRegion(request.Region) };
        // Tiep tuc sau khi sua ROI luc tam dung: checkpoint key TINH THEO request goc
        // (doi ROI la doi key -> mat checkpoint), con quet dung ROI moi.
        // keyRequest cho moi op checkpoint; request cho moi thu con lai.
        // Override chi co nghia o Resume; Fresh bo qua (khong bao gio doi key ngam).
        var keyRequest = request;
        if (startMode == OcrScanStartMode.Resume && resumeRegionOverride is not null)
        {
            var effective = OcrCheckpointStore.CanonicalRegion(resumeRegionOverride);
            if (!effective.Equals(request.Region))
                request = request with { Region = effective };
        }
        var processes = new OwnedProcessGroup();
        if (Interlocked.CompareExchange(ref _activeProcesses, processes, null) is not null)
        {
            await processes.DisposeAsync();
            throw new InvalidOperationException("Một lần quét OCR khác vẫn đang sở hữu tiến trình FFmpeg.");
        }
        try
        {
            return await RunCoreAsync(job, request, keyRequest, startMode, processes);
        }
        catch (Exception) when (job.CancellationToken.IsCancellationRequested)
        {
            // Cancellation is not terminal until every app-owned FFmpeg tree is reaped and
            // Python worker is stopped, and the exact checkpoint identity is verified absent.
            await processes.StopAsync();
            await _ocr.StopAsync();
            await _checkpoints.RemoveAsync(keyRequest, CancellationToken.None);
            throw new OperationCanceledException(job.CancellationToken);
        }
        finally
        {
            await processes.DisposeAsync();
            Interlocked.CompareExchange(ref _activeProcesses, null, processes);
        }
    }

    internal int ActiveProcessCount => Volatile.Read(ref _activeProcesses)?.ActiveCount ?? 0;

    // Batch scavenging scope of the running scan (ambient along the async-flow: lane/canary/audit
    // all flow from RunCoreAsync so they see it; other scans do not). Null = old path.
    private static readonly AsyncLocal<OcrManager.OcrBatchScope?> _ambientBatchScope = new();

    private async Task<OcrScanResult> RunCoreAsync(
        AppJob job,
        OcrScanRequest request,
        OcrScanRequest keyRequest,
        OcrScanStartMode startMode,
        OwnedProcessGroup processes)
    {
        // Per-scan batch scavenging scope (cap=1 right now = exactly the old path; opens to 8 after parity).
        // Dispose at the end of the scan (after the lanes). The finally cleans the ambient so the next scan does not see a dead scope.
        await using var batchScope = _ocr.CreateBatchScope(job.CancellationToken);
        _ambientBatchScope.Value = batchScope;
        try
        {
            return await RunCoreInnerAsync(job, request, keyRequest, startMode, processes);
        }
        finally
        {
            _ambientBatchScope.Value = null;
        }
    }

    private async Task<OcrScanResult> RunCoreInnerAsync(
        AppJob job,
        OcrScanRequest request,
        OcrScanRequest keyRequest,
        OcrScanStartMode startMode,
        OwnedProcessGroup processes)
    {
        var token = job.CancellationToken;
        var source = Path.GetFullPath(request.Path.Trim());
        if (!File.Exists(source) || new FileInfo(source).Length <= 0) throw new FileNotFoundException("Thiếu video nguồn.", source);
        if (request.Duration <= 0) throw new ArgumentException("Thiếu thời lượng video.");
        // Sidecar identity: file hash for the probe/VAD/pxv caches (changing the file is a miss).
        // Read the sidecar progressively during the scan (probe → VAD → pxv), saved best-effort.
        var fileIdentity = OcrSidecar.FileIdentity.FromFile(source);
        OcrSidecar.SidecarDocument? sidecarDoc = null;
        long sidecarProbeHits = 0;
        long sidecarVadHits = 0;
        // E.0 pre-flight TRUOC Fresh-remove: input hong thi dung, giu checkpoint cu.
        var inputHealth = await OcrInputHealth.CheckAsync(_tools, _processes, source, token);
        foreach (var warning in inputHealth.Warnings) job.Warn("Input health: " + warning);
        if (!inputHealth.Passed)
            throw new InvalidDataException("Input health FAIL, không quét: " + string.Join(" · ", inputHealth.Failures));
        job.Log($"Input health PASS: {inputHealth.Width}x{inputHealth.Height} {inputHealth.AvgFps:0.00}fps "
            + $"tb {inputHealth.TimeBaseNum}/{inputHealth.TimeBaseDen} "
            + (inputHealth.HasAudio ? "có tiếng (VAD chạy)" : "không tiếng (VAD nghỉ)") + " "
            + $"PTS {inputHealth.PtsChecked} frame đầu đơn điệu.");
        if (!keyRequest.Region.Equals(request.Region))
            job.Warn("Tiếp tục với ROI mới sửa lúc tạm dừng: checkpoint giữ theo ROI cũ; cue trước/sau điểm nối khác Geometry nên tách an toàn, không gộp sai.");
        if (startMode == OcrScanStartMode.Fresh)
        {
            await _checkpoints.RemoveAsync(keyRequest, token);
            // Time filter: Fresh = judge from scratch — delete the old journal (verdicts
            // from the previous run/old logic must not be blocked from re-judging by dedup). Delete both
            // the new appdata and the legacy alongside file beside the video.
            try { File.Delete(OcrVerdictJournal.JournalPath(source, string.Empty)); } catch { }
            try { File.Delete(OcrVerdictJournal.AppDataPath(source, _appDataDir)); } catch { }
        }
        var saved = startMode == OcrScanStartMode.Resume
            ? await LoadResumeCheckpointAsync(keyRequest, token)
            : null;
        if (saved is not null && startMode == OcrScanStartMode.Resume)
        {
            try
            {
                var spanEnd = saved.Lanes.OrderBy(lane => lane.Segment.Index).Last().Segment.CoreEnd;
                var tailGap = keyRequest.ScanStartOffset + keyRequest.Duration - spanEnd;
                if (tailGap > 0.001)
                    job.Warn($"Checkpoint phủ tới {FormatClock(spanEnd)}, ngắn hơn thời lượng container {tailGap:0.000}s: đuôi file là padding không có frame (bản quét mới cũng kẹp như vậy) — tiếp tục từ checkpoint, không mất câu nào.");
            }
            catch { }
        }
        if (startMode == OcrScanStartMode.Resume && saved is null)
        {
            // Case 06/10: run detailed diagnostics before reporting (do not leave the user guessing).
            var diagnosis = await _checkpoints.DiagnoseResumeAsync(keyRequest, CancellationToken.None);
            job.Error($"Resume OCR mất checkpoint: key {diagnosis.Key[..12]}... · "
                + $"file chính {(diagnosis.MainExists ? "CÓ" : "KHÔNG")} · backup {(diagnosis.BackupExists ? "CÓ" : "KHÔNG")} · "
                + $"đọc được {(diagnosis.Parsed ? "CÓ" : "KHÔNG")} · schema {diagnosis.Schema} {(diagnosis.SchemaMatch ? "khớp" : "LỆCH")} · "
                + $"key {(diagnosis.KeyMatch ? "khớp" : "LỆCH")} · validity {(diagnosis.Valid ? "đạt" : "RỚT")} · "
                + $"lanes {diagnosis.Lanes} · workers {diagnosis.Workers} · mode {diagnosis.WorkerMode} · parallelism {diagnosis.SelectedParallelism}.");
            throw new InvalidOperationException("Không còn checkpoint OCR phù hợp để tiếp tục. Hãy Quét từ đầu.");
        }

        // Whisper ear listening was removed from the scan path (22/09: cost ~1 hour per film while
        // only dropping ~2% of reads). Purely eye-based scanning; the dependent steps
        // (duplicate merging, banner, judge, missing-sub warning) follow eye rules themselves.
        IReadOnlyList<EditorSpeechSegment> speechSegments = [];

        // A Hybrid request may have explicitly fallen back to GPU. Resume the
        // persisted device mode, not a newly selected interpretation of the request.
        var resumedGpuFallback = saved?.WorkerMode == "gpu"
            && string.Equals(request.Device, "hybrid", StringComparison.OrdinalIgnoreCase);
        await _ocr.ConfigureDeviceAsync(saved?.WorkerMode ?? request.Device, token);
        await _ocr.EnsureAsync(token);
        if (saved is not null && !string.Equals(saved.WorkerMode, _ocr.Status.ActiveMode, StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot resume OCR with a different device topology; start a fresh scan instead.");
        if (resumedGpuFallback)
            job.Warn("Resume retains the checkpoint's explicit GPU fallback instead of switching to Hybrid.");
        else if (string.Equals(request.Device, "hybrid", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(_ocr.Status.ActiveMode, "hybrid", StringComparison.OrdinalIgnoreCase))
            job.Warn(_ocr.Status.Error ?? "Hybrid không sẵn sàng; lượt này đã chuyển sang GPU.");

        // Model tier by scan mode: Accurate = Medium (untouched),
        // Balanced/Fast = the smallest Mobile (28/09, per user order). Build the pool
        // with the right tier before locking topology; the checkpoint key includes the model name so
        // resume cannot mix tiers (an old small checkpoint is void, rescan).
        var tierDet = OcrModelTier.DetectionModel(request.Mode);
        var tierRec = OcrModelTier.RecognitionModel(request.Mode);
        await _ocr.EnsureModelTierAsync(tierDet, tierRec, token);
        if (!string.Equals(OcrModelTier.TierName(request.Mode), "medium", StringComparison.Ordinal))
            job.Log($"OCR dùng model nhẹ {tierDet} + {tierRec} cho chế độ {request.Mode}.");

        var ffmpeg = await _tools.EnsureFfmpegAsync(token);
        var mode = OcrCheckpointStore.ModeFor(request.Mode, request.Sensitivity);

        int selected;
        if (saved is null)
        {
            job.Set("benchmark", 0.5, "OCR · đo nền CPU/RAM trước benchmark pipeline thật...");
            var baseline = await _hardware.BenchmarkAsync(token);
            job.Log($"Benchmark tốc độ nền: CPU {baseline.CpuMegabytesPerSecond:0} MiB/s · RAM {baseline.MemoryMegabytesPerSecond:0} MiB/s. Auto sẽ quyết định bằng live RAM/VRAM, topology thật và throughput.");
            try
            {
                selected = await SelectParallelismAsync(job, ffmpeg, request, processes, token);
            }
            catch (Exception hybridError) when (
                !token.IsCancellationRequested
                && string.Equals(request.Device, "hybrid", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Parallelism.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
                && string.Equals(_ocr.Status.ActiveMode, "hybrid", StringComparison.OrdinalIgnoreCase))
            {
                job.Warn("Hybrid không hoàn tất probe GPU + CPU: " + Compact(hybridError.Message)
                    + " · chuyển rõ ràng sang GPU và benchmark lại; không giữ nhãn Hybrid giả.");
                await _ocr.ConfigureDeviceAsync("gpu", token);
                await _ocr.EnsureAsync(token);
                selected = await SelectParallelismAsync(job, ffmpeg, request, processes, token);
            }
        }
        else
        {
            selected = saved.SelectedParallelism;
            job.Set("resume", -1, $"OCR Resume: dựng lại đúng topology {selected} từ checkpoint...");
            EnsureResourceHeadroom(job, selected, isManual: true, lastStable: 0);
            var restored = await _ocr.ConfigureWorkerPoolAsync(saved.Workers, token);
            if (restored != saved.Workers || !_ocr.Status.Ready)
                throw new InvalidOperationException($"Cannot restore checkpoint: {selected} lanes, {restored}/{saved.Workers} workers.");
            job.Log($"Resume Commit: {selected} FFmpeg lanes + {restored} Python workers; no benchmark.");
        }
        var configuredWorkers = _ocr.Status.Workers;
        var configuredWorkerKinds = _ocr.Status.WorkerKinds;
        // Small tier may already have grown 2→4 from a previous resumed session: accept
        // the interval [base, cap] rather than an absolute value (base = old rule).
        var expectedBase = OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, selected);
        var expectedCap = await _ocr.EffectiveWorkerTargetAsync(_ocr.Status.ActiveMode, selected, token);
        if (configuredWorkers < expectedBase || configuredWorkers > expectedCap)
            throw new InvalidDataException($"OCR topology chưa khóa đúng: {selected} FFmpeg lane nhưng có {configuredWorkers} Python worker.");
        var checkpoint = saved ?? (await _checkpoints.NewAsync(keyRequest, selected, token)) with
        {
            Workers = configuredWorkers,
            WorkerMode = _ocr.Status.ActiveMode,
        };
        selected = checkpoint.SelectedParallelism;
        if (checkpoint.Lanes.Count != selected)
            throw new InvalidDataException($"OCR checkpoint topology không hợp lệ: {checkpoint.Lanes.Count}/{selected} lane.");
        // Small-tier scale-up (26/09): VRAM-gated grow 2→4 workers sau khi
        // topology already locked, before the lanes run (no traffic yet so the rebuild
        // safety window is available). With insufficient VRAM/OOM-pinned, keep as-is.
        var grownWorkers = await _ocr.TryGrowSmallTierAsync(selected, token);
        if (grownWorkers != configuredWorkers)
            job.Log($"OCR Small tier mở rộng worker {configuredWorkers} → {grownWorkers} (VRAM cho phép).");
        else if (_ocr.SmallTierGrowReason.Length > 0)
            job.Log($"OCR Small tier giữ {configuredWorkers} worker: {_ocr.SmallTierGrowReason}");
        // Real runtime strength (27/09, the balanced 4h case: grow 2→4 succeeded but
        // every runtime/recovery/telemetry path still used configuredWorkers=2 —
        // a lane retry rebuilt the pool back to 2, killing the grown workers, leaving the rest of the
        // scan at half capacity). configuredWorkers is preserved for the checkpoint
        // + resume validation (resume only works pre-grow when growth is blocked);
        // effectiveWorkers is used for everything runtime.
        var effectiveWorkers = grownWorkers;
        // Auto-detect the subtitle style (16/09, no manual switch): sample frames evenly, crop
        // to the exact ROI, the worker measures the text ring -> vote on a filter level for the WHOLE scan. Every error
        // -> "off" (exactly as before, no lost text). Each scan RE-ASSIGNS from scratch so it does not
        // leak into another scan.
        var styleMode = await ProbeSubtitleStyleAsync(job, ffmpeg, source, request, token);
        OcrWorkerClient.AmbientStyleFilter.Value = styleMode == "off" ? null : styleMode;
        job.Log(styleMode == "off"
            ? "Tách chữ khỏi nền: không rõ kiểu sub (chữ màu/không đo được) -> không lọc, quét như cũ."
            : $"Tách chữ khỏi nền: sub {OcrSubtitleStyle.Describe(styleMode)} (tự đo lúc bắt đầu) -> lọc trước khi đọc.");
        // P0 container-padding: file co the het stream truoc container (p02: stream
        // 6726.667s, container 6726.914s). Lane cuoi doi toi CoreEnd-container thi
        // ValidateLaneCoverage fail vinh vien, retry mai khong qua, job chet.
        // Kep CoreEnd/ScanEnd lane cuoi theo stream thuc do + log truy duoc.
        // Neu lane da quet toi noi thi danh dau xong (padding khong co frame nen
        // khong the co sub); nguoc lai resume binh thuong tu checkpoint.
        {
            double streamEnd = 0;
            try
            {
                var (padProbe, padDoc, padHit) = await ProbeVideoAsync(source, fileIdentity, token);
                sidecarDoc = padDoc ?? sidecarDoc;
                if (padHit) sidecarProbeHits++;
                streamEnd = padProbe?.StreamDurationSeconds ?? 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            var last = checkpoint.Lanes[^1];
            var clampedEnd = OcrInputHealth.ClampStreamEnd(last.Segment.CoreEnd, streamEnd);
            if (clampedEnd < last.Segment.CoreEnd)
            {
                var done = last.MediaSeconds + 0.1 >= clampedEnd;
                var fixedLanes = checkpoint.Lanes.ToList();
                fixedLanes[^1] = last with
                {
                    Segment = last.Segment with
                    {
                        CoreEnd = clampedEnd,
                        ScanEnd = Math.Min(last.Segment.ScanEnd, clampedEnd),
                    },
                    MediaSeconds = done ? clampedEnd : last.MediaSeconds,
                    Completed = last.Completed || done,
                };
                checkpoint = checkpoint with { Lanes = fixedLanes };
                job.Warn($"Input padding cuoi file: lane cuoi {last.Segment.Index + 1} kep {FormatClock(last.Segment.CoreEnd)} ve {FormatClock(clampedEnd)} theo stream thuc "
                    + (done ? "(da quet toi noi: danh dau xong, khong quet lai padding khong frame)." : "(resume tiep tu checkpoint)."));
            }
        }
        if (configuredWorkers != checkpoint.Workers)
            throw new InvalidDataException($"OCR checkpoint yêu cầu {selected} lane nhưng worker pool đang có {configuredWorkers}.");
        // E.5 BACKGROUND VAD (the sidecar case blocking the 20:34 session on 15/9): audio decoding no longer blocks lanes.
        // Sidecar miss -> decode runs in the background in parallel, lanes run ahead with vad=null
        // (vote=Suspicious = the correct missing-signal rule, council does not skip when no ear is available yet);
        // when done, it attaches to the council automatically + saves the sidecar. Sidecar hit -> use immediately as before.
        // Cancel scan -> the background dies via its token, no lingering. No audio -> rest (E.0 already reported).
        VadTimeline? vadSnapshot = null;
        Func<VadTimeline?> vadSource = () => Volatile.Read(ref vadSnapshot);
        // No ear listening: sidecar/energy as before (the timeline has no HasSub flag
        // so council skip is off by default — purely eye-based scanning).
        if (inputHealth.HasAudio)
        {
            vadSnapshot = OcrSidecar.ToTimeline(sidecarDoc?.Vad);
            if (vadSnapshot is not null)
            {
                sidecarVadHits++;
                job.Log($"VAD từ sidecar cạnh video: {vadSnapshot.Flags.Length} khung 0.2s, khỏi decode audio.");
            }
            else
            {
                job.Log("VAD miss sidecar: decode audio chạy NỀN song song lane (lane không đợi); xong tự gắn vào council + lưu sidecar.");
                var vadWatch = Stopwatch.StartNew();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var background = await OcrVadProbe.DecodeAsync(_tools, _processes, source, token);
                        if (background is null)
                        {
                            job.Warn("VAD vang mat (khong giai ma duoc audio): tai nghi, mat (B/C) quyet theo luat dong thuan.");
                            return;
                        }
                        Volatile.Write(ref vadSnapshot, background);
                        job.Log($"VAD nền xong trong {vadWatch.Elapsed.TotalSeconds:0.0}s: {background.Flags.Length} khung 0.2s — council đã gắn, lưu sidecar.");
                        if (fileIdentity is not null)
                        {
                            // Best-effort: copy the current Pixel state so it does not overwrite this session's pxv verdicts.
                            var updated = new OcrSidecar.SidecarDocument(
                                sidecarDoc?.Identity ?? fileIdentity, sidecarDoc?.Probe,
                                OcrSidecar.FromTimeline(background), sidecarDoc?.Pixel);
                            sidecarDoc = updated;
                            try { await _sidecars.SaveAsync(fileIdentity.Path, updated, CancellationToken.None); } catch { }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { job.Warn("VAD nền lỗi (bỏ qua, council chạy mù): " + Compact(error.Message)); }
                }, CancellationToken.None);
            }
        }
        job.Log(saved is null
            ? $"OCR Commit: benchmark xong, khóa {selected} pipeline = {selected} FFmpeg lane + {effectiveWorkers} Python worker ({configuredWorkerKinds}). Chế độ quét: {request.Mode} ({mode.Fps:0.#}Hz)."
            : $"Resume checkpoint schema {checkpoint.Schema}: giữ topology {selected} = {selected} FFmpeg lane + {effectiveWorkers} Python worker ({configuredWorkerKinds}). Chế độ quét: {request.Mode} ({mode.Fps:0.#}Hz).");
        var decoder = await ProbeNvdecAsync(ffmpeg, source, request.Region, mode, processes, token) ? "nvdec" : "software";
        job.Log(decoder == "nvdec" ? "Decoder: NVIDIA NVDEC." : "Decoder: software fallback.");
        // Diagnostic baselines captured after the topology lock: the benchmark
        // phase above also flows through the same manager/queue counters.
        var telemetry = new OcrScanTelemetryCounters();
        telemetry.Touch();
        // Sidecar hits happen BEFORE telemetry exists (padding/VAD): funnel them here.
        Interlocked.Add(ref telemetry.SidecarProbeHits, sidecarProbeHits);
        Interlocked.Add(ref telemetry.SidecarVadHits, sidecarVadHits);
        var inferenceBefore = _ocr.GetInferenceTelemetry();

        var started = Stopwatch.StartNew();
        var progress = checkpoint.Lanes.Select(x => x.MediaSeconds).ToArray();
        var frameProgress = checkpoint.Lanes.Select(x => x.Frames).ToArray();
        var imageProgress = checkpoint.Lanes.Select(x => x.OcrImages).ToArray();
        var completed = checkpoint.Lanes.Select(x => x.Completed).ToArray();
        var liveCommitted = checkpoint.Lanes
            .SelectMany(lane => lane.Cues.Where(cue =>
                cue.Start >= lane.Segment.CoreStart
                && (lane.Segment.Index == checkpoint.Lanes.Count - 1
                    ? cue.Start <= lane.Segment.CoreEnd
                    : cue.Start < lane.Segment.CoreEnd)))
            .OrderBy(cue => cue.Start)
            .ToList();
        var liveActive = checkpoint.Lanes.Select(lane =>
        {
            var active = lane.Active;
            if (active is null || active.Start < lane.Segment.CoreStart) return null;
            var isLast = lane.Segment.Index == checkpoint.Lanes.Count - 1;
            return isLast ? active.Start <= lane.Segment.CoreEnd ? active : null
                : active.Start < lane.Segment.CoreEnd ? active : null;
        }).ToArray();
        var progressGate = new object();
        var recoverableLanes = checkpoint.Lanes
            .Select(CloneLaneCheckpoint)
            .ToArray();
        Exception? laneFailure = null;
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Live incremental reconcile: the committed cue tail gets pixel-gated during the scan itself,
        // and the preview updates its clean tail without waiting for Pause. The final ReconcileAsync below still runs its safety pass.
        // Declared before RunGuardedLaneAsync so the local function captures it correctly.
        var liveWindow = await ComputeContinuationWindowAsync(_tools, _processes, source, token);
        // Pixel cache shared live -> final WITHIN the same scan session (Task 1 perf gate):
        // one single ProductionPixelComparer instance for both liveTail.Start and the final
        // ReconcileAsync. _comparisonCache is an instance field so final reuses the live measurements
        // (same key: video path + SourcePts A/B + shared crop) instead of decoding again via FFmpeg.
        // Lifetime = method scope: created when the session starts, ends when the session ends/is cancelled,
        // a new scan/different video always gets a new (cache) instance — no leaking across videos.
        // Not static/global. Key identity preserved, not loosened.
        var sharedPixelComparer = new ProductionPixelComparer(_tools, _processes, _activeProcesses);
        // Sidecar pxv: load pixel verdicts from a previous scan (key normalized by file hash, see OcrSidecar).
        // A miss is normal (first scan/changed ROI) — the recomputation path below is preserved.
        // Seeding ONLY adds hits, never deletes/modifies anything from this session.
        if (sidecarDoc?.Pixel is { } pxvSection && pxvSection.V == OcrSidecar.PixelVersion)
        {
            var seedEntries = new List<(long First, long Second, System.Drawing.Rectangle Crop, PixelCheckResult Result)>();
            foreach (var entry in pxvSection.Entries)
            {
                if (OcrSidecar.FromEntry(entry) is { } decoded)
                    seedEntries.Add((entry.First, entry.Second, decoded.Crop, decoded.Verdict));
            }
            if (seedEntries.Count > 0)
            {
                var seeded = 0;
                try { seeded = sharedPixelComparer.Seed(source, seedEntries); } catch { seeded = 0; }
                if (seeded > 0)
                {
                    Interlocked.Add(ref telemetry.SidecarPxvHits, seeded);
                    job.Log($"Pixel từ sidecar cạnh video: {seeded} verdict nạp sẵn, khỏi decode lại frame.");
                }
            }
        }
        using var liveTail = new OcrLiveTailReconciler();
        liveTail.ResetCommitted(liveCommitted);
        liveTail.Start(source, sharedPixelComparer, liveWindow,
            onError: error => job.Log("Live tail reconcile lỗi (preview giữ raw, final pass vẫn chạy): " + Compact(error.Message)),
            linkedToken: laneCancellation.Token);
        // Time filter (17/09) lives INSIDE the OCR pipeline: always runs, NO toggle,
        // NO key, NO AI. A real hardcoded sub STAYS STILL across many frames (fixed
        // position), text inside the video (book pages/background) MOVES with the camera
        // -> capture 3 frames, re-OCR, compare positions -> keep/drop. A SEPARATE journal file
        // in app Data (28/09: files beside the video are forbidden — alongside is legacy-read only;
        // old VLM verdicts must not be loaded — dedup would block re-judging). Dispose at the end of the
        // scan + the finally of the await using for every exit path.
        var temporalJournal = OcrVerdictJournal.AppDataPath(source, _appDataDir);
        await using var trailingJudge = OcrTrailingJudge.CreateTemporalLive(
            _tools, _processes, _ocr, source, ffmpeg,
            temporalJournal,
            async (fix, token) => { await SaveFixesAsync(keyRequest, new[] { fix }, token); },
            async (exclusion, token) => { await SaveExclusionsAsync(keyRequest, new[] { exclusion }, token); },
            checkpoint.Lanes.Count,
            inputHealth.Width,
            inputHealth.Height,
            speech: speechSegments);
        job.Log($"Lọc thời gian: bật ({trailingJudge.Workers} luồng chấm theo {checkpoint.Lanes.Count} lane OCR) — sub đứng yên qua nhiều khung thì qua, chữ di chuyển (trang sách/bối cảnh) thì xóa khỏi SRT.");
        // Real throughput for the fake .5x number case: track session average + 5 minutes + lane stalls.
        // Pure diagnostic, does not touch scheduling/tracker/checkpoint so it is resume-safe.
        // Resume-based (the 281× panel case): progress is seeded from the old checkpoint but the clock restarts
        // from 0, so the tracker subtracts the session start offset — all throughput is EXTRA WORK / extra run time.
        var initialUnique = checkpoint.Lanes.Sum(x =>
            Math.Max(0, Math.Min(x.Segment.CoreEnd, progress[x.Segment.Index]) - x.Segment.CoreStart));
        var pace = new ScanPaceTracker(checkpoint.Lanes.Count, initialUnique);

        OcrParallelCheckpoint SnapshotRecoverableCheckpoint()
        {
            lock (progressGate)
            {
                return checkpoint with
                {
                    Lanes = recoverableLanes.Select(CloneLaneCheckpoint).ToList(),
                };
            }
        }

        using var checkpointSaveGate = new SemaphoreSlim(1, 1);
        using var laneRecoveryGate = new SemaphoreSlim(1, 1);
        // Adaptive fallback (28/09, the 16:18 storm: rebuilding keeping grown capacity for
        // speed, but the pool kept failing (OOM/fragmentation) -> continuous rebuild -> retire queue -> cancelling
        // in-flight work from other lanes -> storm -> job death;
        // while the old behaviour of returning to base 2 was slow but survived). First failure:
        // rebuild at the grown capacity. A further failure within 15 minutes: fall back to the base
        // pre-grow level for the rest of this scan, no more speed attempts.
        var poolCollapseCount = 0;
        var poolRetreatedToBase = false;
        long lastPoolCollapseTicks = 0;

        async Task SaveRecoverableCheckpointAsync()
        {
            await checkpointSaveGate.WaitAsync(CancellationToken.None);
            var saveWatch = Stopwatch.StartNew();
            try
            {
                var snapshot = SnapshotRecoverableCheckpoint();
                await _checkpoints.SaveAsync(keyRequest, snapshot, CancellationToken.None);
            }
            finally
            {
                checkpointSaveGate.Release();
                saveWatch.Stop();
                Interlocked.Increment(ref telemetry.CheckpointSaves);
                Interlocked.Add(ref telemetry.CheckpointSaveMs, saveWatch.ElapsedMilliseconds);
            }
        }

        // Sidecar pxv dump: this session's pixel verdicts -> the sidecar file beside the video so the next scan
        // (finish/export/open-checkpoint) avoids decoding those frames again. ABSOLUTELY best-effort:
        // every error is swallowed internally, never kills the lane or the checkpoint.
        async Task FlushPixelSidecarAsync()
        {
            try
            {
                if (fileIdentity is null) return;
                var snapshot = sharedPixelComparer.Snapshot();
                if (snapshot.Count == 0) return;
                var entries = new List<OcrSidecar.PixelEntry>(snapshot.Count);
                foreach (var (first, second, crop, result) in snapshot)
                {
                    if (OcrSidecar.ToEntry(first, second, crop, result) is { } entry)
                        entries.Add(entry);
                }
                if (entries.Count == 0) return;
                sidecarDoc = new OcrSidecar.SidecarDocument(
                    sidecarDoc?.Identity ?? fileIdentity,
                    sidecarDoc?.Probe, sidecarDoc?.Vad,
                    new OcrSidecar.PixelSection(OcrSidecar.PixelVersion, entries));
                await _sidecars.SaveAsync(fileIdentity.Path, sidecarDoc, CancellationToken.None);
            }
            catch { }
        }

        using var checkpointWriterCancellation = new CancellationTokenSource();
        var checkpointWriter = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(CheckpointInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(checkpointWriterCancellation.Token))
                {
                    await SaveRecoverableCheckpointAsync();
                    await FlushPixelSidecarAsync();
                }
            }
            catch (OperationCanceledException) when (checkpointWriterCancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                Interlocked.CompareExchange(
                    ref laneFailure,
                    new IOException("Không lưu được checkpoint OCR định kỳ: " + error.Message, error),
                    null);
                laneCancellation.Cancel();
            }
        });

        async Task StopCheckpointWriterAsync()
        {
            checkpointWriterCancellation.Cancel();
            await checkpointWriter;
        }

        async Task<OcrLaneCheckpoint> RunGuardedLaneAsync(OcrLaneCheckpoint lane)
        {
            // Staggered launch (17/10): 8 NVDEC sessions + 2 CUDA worker contexts starting
            // at the same beat used to break NVDEC right at scan start; stagger by 150ms×index,
            // a lane that has already finished passes through with a delay of ~0 (try-quick return still runs).
            var launchDelay = TimeSpan.FromMilliseconds(LaneSpawnStaggerMs * lane.Segment.Index);
            if (launchDelay > TimeSpan.Zero) await Task.Delay(launchDelay, laneCancellation.Token);
            OcrLaneCheckpoint? outcome = null;
            var recoveryAttempts = 0;
            try
            {
                while (true)
                {
                    OcrLaneCheckpoint attemptStart;
                    lock (progressGate)
                        attemptStart = CloneLaneCheckpoint(recoverableLanes[lane.Segment.Index]);
                    try
                    {
                        outcome = await RunLaneWithFallbackAsync(
                            ffmpeg, source, request, mode, attemptStart, decoder,
                            (laneSnapshot, committedCues, checkpointSafe) =>
                            {
                                lock (progressGate)
                                {
                                    progress[lane.Segment.Index] = laneSnapshot.MediaSeconds;
                                    frameProgress[lane.Segment.Index] = laneSnapshot.Frames;
                                    imageProgress[lane.Segment.Index] = laneSnapshot.OcrImages;
                                    if (checkpointSafe)
                                        recoverableLanes[lane.Segment.Index] = CloneLaneCheckpoint(laneSnapshot);
                                    var isLast = lane.Segment.Index == checkpoint.Lanes.Count - 1;
                                    var owned = new List<OcrCue>(committedCues.Count);
                                    foreach (var cue in committedCues)
                                    {
                                        if (cue.Start < lane.Segment.CoreStart
                                            || (!isLast && cue.Start >= lane.Segment.CoreEnd)
                                            || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                                        liveCommitted.Add(cue);
                                        owned.Add(cue);
                                    }
                                    // Trigger live tail reconcile exactly when the tracker commits a new boundary,
                                    // not on every frame. Worker serialization + revision guard are inside.
                                    if (owned.Count > 0) liveTail.AppendCommitted(owned);
                                    // Background judging: only closed cues enter the queue (open live cues are not judged).
                                    // TryWrite does not block — a full queue means drop + count, the lane does not wait.
                                    var (judgeAccepted, judgeSkipped) = trailingJudge.EnqueueCues(owned);
                                    if (judgeAccepted > 0)
                                        Interlocked.Add(ref telemetry.JudgeEnqueued, judgeAccepted);
                                    if (judgeSkipped > 0)
                                        Interlocked.Add(ref telemetry.JudgeSkipped, judgeSkipped);
                                    liveActive[lane.Segment.Index] = laneSnapshot.Active is not null
                                        && laneSnapshot.Active.Start >= lane.Segment.CoreStart
                                        && (isLast ? laneSnapshot.Active.Start <= lane.Segment.CoreEnd : laneSnapshot.Active.Start < lane.Segment.CoreEnd)
                                            ? laneSnapshot.Active
                                            : null;
                                    PublishTelemetry(
                                        job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed,
                                        effectiveWorkers, configuredWorkerKinds, request.Duration, liveTail, liveActive,
                                        started.Elapsed.TotalSeconds, pace);
                                }
                            }, job, processes, telemetry, laneCancellation.Token, vadSource);
                        return outcome;
                    }
                    catch (Exception error) when (
                        !token.IsCancellationRequested
                        && !laneCancellation.IsCancellationRequested
                        && recoveryAttempts < MaxLaneRecoveryAttempts)
                    {
                        recoveryAttempts++;
                        Interlocked.Increment(ref telemetry.LaneRestarts);
                        job.Warn(
                            $"OCR lane {lane.Segment.Index + 1} lỗi tạm thời: {Compact(error.Message)} · "
                            + $"đang khôi phục lần {recoveryAttempts}/{MaxLaneRecoveryAttempts} từ checkpoint an toàn.");
                        await laneRecoveryGate.WaitAsync(token);
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            laneCancellation.Token.ThrowIfCancellationRequested();
                            var workerStatus = _ocr.Status;
                             if (!_ocr.IsPoolUsableForRetry())
                            {
                                 // Pool chet han (kenh chet hoac 0 worker song): dung lai toan bo
                                 // to the runtime capacity that has grown (NOT back to configuredWorkers
                                 // pre-grow: rebuilding to 2 kills grown workers mid-run ->
                                 // other lanes lose in-flight work -> chained retry storm).
                                 // Con lai (meo 1 slot nhung van chay) thi TUYET DOI khong rebuild:
                                 // rebuild retire queue -> huy hang loat viec dang bay cua lane khac
                                 // -> moi lane dot attempt -> rebuild storm (10:40 + 19:00 13/9).
                                 // Slot hong do request-recovery (ReplaceWorkerAsync) tu va lazily.
                                 var collapseNow = Stopwatch.GetTimestamp();
                                 if (!poolRetreatedToBase && poolCollapseCount >= 1
                                     && Stopwatch.GetElapsedTime(lastPoolCollapseTicks, collapseNow) < TimeSpan.FromMinutes(15))
                                 {
                                     poolRetreatedToBase = true;
                                     effectiveWorkers = configuredWorkers;
                                     job.Warn("Pool OCR sập lặp lại trong 15 phút dù đã dựng đủ sức grow; rút về base pre-grow cho hết lượt quét này (chậm mà chắc, không storm).");
                                 }
                                 poolCollapseCount++;
                                 lastPoolCollapseTicks = collapseNow;
                                 var restored = await _ocr.ConfigureWorkerPoolAsync(effectiveWorkers, token);
                                 workerStatus = _ocr.Status;
                                 if (restored != effectiveWorkers || !workerStatus.Ready || workerStatus.Workers != effectiveWorkers)
                                     throw new InvalidOperationException(
                                          $"Cannot restore OCR: {selected} lanes, {workerStatus.Workers}/{effectiveWorkers} workers.");
                             }
                             else if (!workerStatus.Ready || workerStatus.Workers != effectiveWorkers)
                            {
                                job.Log($"OCR pool meo nhe (song {workerStatus.LiveWorkers}/{workerStatus.Workers}): bo qua rebuild giua scan; request-recovery tu va slot hong, lane retry tu checkpoint.");
                            }

                            OcrLaneCheckpoint restart;
                            lock (progressGate)
                            {
                                restart = CloneLaneCheckpoint(recoverableLanes[lane.Segment.Index]);
                                progress[lane.Segment.Index] = restart.MediaSeconds;
                                frameProgress[lane.Segment.Index] = restart.Frames;
                                imageProgress[lane.Segment.Index] = restart.OcrImages;
                                var isLast = lane.Segment.Index == checkpoint.Lanes.Count - 1;
                                liveCommitted.RemoveAll(cue =>
                                    cue.Start >= lane.Segment.CoreStart
                                    && (isLast ? cue.Start <= lane.Segment.CoreEnd : cue.Start < lane.Segment.CoreEnd));
                                foreach (var cue in restart.Cues)
                                {
                                    if (cue.Start < lane.Segment.CoreStart
                                        || (!isLast && cue.Start >= lane.Segment.CoreEnd)
                                        || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                                    liveCommitted.Add(cue);
                                }
                                liveActive[lane.Segment.Index] = restart.Active is not null
                                    && restart.Active.Start >= lane.Segment.CoreStart
                                    && (isLast ? restart.Active.Start <= lane.Segment.CoreEnd : restart.Active.Start < lane.Segment.CoreEnd)
                                        ? restart.Active
                                        : null;
                                // Lane recovery rebuilds committed cues from a safe checkpoint: reset the live tail
                                // so reconciliation restarts from the correct state, keeping no merges from the failed attempt.
                                liveTail.ResetCommitted(liveCommitted);
                            }
                            await SaveRecoverableCheckpointAsync();
                             job.Log(
                                 $"OCR lane {lane.Segment.Index + 1} khôi phục PASS tại {FormatClock(restart.MediaSeconds)} · "
                                  + $"topology {selected} lanes / {effectiveWorkers} workers; retry lane.");
                        }
                        catch (Exception recoveryError) when (
                            !token.IsCancellationRequested
                            && !laneCancellation.IsCancellationRequested)
                        {
                            throw new InvalidOperationException(
                                $"Không khôi phục được OCR lane {lane.Segment.Index + 1} sau lỗi: {Compact(error.Message)} · "
                                + Compact(recoveryError.Message),
                                new AggregateException(error, recoveryError));
                        }
                        finally { laneRecoveryGate.Release(); }
                    }
                }
            }
            catch (Exception error)
            {
                // A lane can throw OperationCanceledException without the user job token
                // being cancelled (for example, an owned child/stream abort). Preserve the
                // first such failure before cancelling sibling lanes; otherwise Task.WhenAll
                // surfaces only cancellation and the outer job incorrectly reports that the
                // user pressed Cancel.
                if (!token.IsCancellationRequested && !laneCancellation.IsCancellationRequested)
                {
                    var failure = error is OperationCanceledException
                        ? new InvalidOperationException(
                            $"OCR lane {lane.Segment.Index + 1} dừng ngoài yêu cầu hủy của người dùng.", error)
                        : error;
                    Interlocked.CompareExchange(ref laneFailure, failure, null);
                }
                laneCancellation.Cancel();
                throw;
            }
            finally
            {
                lock (progressGate)
                {
                    if (outcome is not null)
                    {
                        progress[lane.Segment.Index] = outcome.MediaSeconds;
                        frameProgress[lane.Segment.Index] = outcome.Frames;
                        imageProgress[lane.Segment.Index] = outcome.OcrImages;
                        completed[lane.Segment.Index] = outcome.Completed;
                        recoverableLanes[lane.Segment.Index] = CloneLaneCheckpoint(outcome);
                    }
                    PublishTelemetry(
                        job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed,
                        effectiveWorkers, configuredWorkerKinds, request.Duration, liveTail, liveActive,
                        started.Elapsed.TotalSeconds, pace);
                }
            }
        }
        var laneTasks = checkpoint.Lanes.Select(RunGuardedLaneAsync).ToArray();

        OcrLaneCheckpoint[] lanes;
        try
        {
            lanes = await Task.WhenAll(laneTasks);
        }
        catch
        {
            await StopCheckpointWriterAsync();
            await liveTail.StopAsync();
            if (!token.IsCancellationRequested && laneFailure is not null)
            {
                try
                {
                    await SaveRecoverableCheckpointAsync();
                    var savedFrontier = OcrCheckpointStore.ContiguousFrontier(SnapshotRecoverableCheckpoint().Lanes);
                    throw new InvalidOperationException(
                        $"OCR lane lỗi: {laneFailure.Message} Đã lưu checkpoint an toàn tại {FormatClock(savedFrontier)}; có thể bấm Tiếp tục.",
                        laneFailure);
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception checkpointError)
                {
                    throw new InvalidOperationException(
                        $"OCR lane lỗi: {laneFailure.Message} Đồng thời không lưu được checkpoint: {checkpointError.Message}",
                        new AggregateException(laneFailure, checkpointError));
                }
            }
            throw;
        }
        await StopCheckpointWriterAsync();
        await liveTail.StopAsync();
        // Background judging: when a lane finishes, no new cues remain — stop the service (half-written cues run to completion then
        // stop), summary in 1 line. The remainder is deferred to the next round (the journal keeps finished cues).
        var trailingStats = trailingJudge.Snapshot();
        await trailingJudge.DisposeAsync();
        if (trailingStats.Enqueued > 0)
        {
            job.Log($"Lọc thời gian: {trailingStats.Enqueued} ca vào hàng "
                + $"({trailingStats.Judged} đã chấm: {trailingStats.Keeps} giữ (đứng yên)/{trailingStats.Deleted} xóa khỏi SRT (di chuyển); "
                + $"{trailingStats.Duplicates} trùng/{trailingStats.QueueFullDropped} tràn hàng/{trailingStats.GrabFailed} lỗi frame/{trailingStats.Deferred} hoãn).");
        }

        token.ThrowIfCancellationRequested();
        var paused = lanes.Any(x => !x.Completed) && job.IsPauseRequested;
        var exactWindow = liveWindow;
        var lastReconcilePublish = 0L;
        var frontier = OcrCheckpointStore.ContiguousFrontier(lanes);
        IReadOnlyList<OcrCue> mergedAll;
        int boundaryMerges;
        IReadOnlyList<OcrCue> merged;
        if (paused)
        {
            // Fast pause (the "loading 2529 frames in parallel" wait case 15/9): pixel reconciliation of thousands of
            // frames is FINISH/export work, not pause work. The pause preview is exactly the
            // merged live view (drain the tail loop, bounded) — matching what the user still sees while scanning;
            // on timeout/failure, fall back to the old raw preview (fail-open, pause never hangs).
            // BoundaryMerges keeps its old count (grep across src: nothing reads it). The checkpoint still
            // has enough lanes to resume; finish recomputes everything later.
            job.Set("finalizing", 99.5, "Đang lưu checkpoint tạm dừng · bỏ qua ghép SRT (để dành lúc finish/xuất).");
            mergedAll = OcrTextCleanup.Cleanup(
                await BuildPausedPreviewAsync(liveTail, lanes, source, sharedPixelComparer, liveWindow, frontier, token)).Cues;
            boundaryMerges = checkpoint.BoundaryMerges;
            merged = mergedAll;
        }
        else
        {
            job.Set("finalizing", 99.5, "Đang chuẩn bị ghép SRT cuối · OCR đã xong.");
            (mergedAll, boundaryMerges) = await ReconcileAsync(
                lanes, source, sharedPixelComparer, token, exactWindow,
                (current, total) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (current < total && lastReconcilePublish != 0
                        && Stopwatch.GetElapsedTime(lastReconcilePublish, now) < TimeSpan.FromMilliseconds(500)) return;
                    lastReconcilePublish = now;
                    var fraction = total > 0 ? current / (double)total : 1;
                    job.Set("finalizing", 99.5 + fraction * .4,
                        $"Đang ghép SRT cuối · FFmpeg kiểm tra ranh giới {current:N0}/{total:N0} · OCR đã xong, ứng dụng không bị treo.");
                },
                (current, total) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (current < total && lastReconcilePublish != 0
                        && Stopwatch.GetElapsedTime(lastReconcilePublish, now) < TimeSpan.FromMilliseconds(500)) return;
                    lastReconcilePublish = now;
                    var fraction = total > 0 ? current / (double)total : 1;
                    job.Set("finalizing", 99.5 + fraction * .3,
                        $"Đang ghép SRT cuối · tải song song frame {current:N0}/{total:N0} · OCR đã xong.");
                },
                speechSegments: speechSegments);
            merged = mergedAll;
            // Static background text guard (18/09): compensates for removing silence-drop — banner/watermark text
            // standing still during silent segments must be caught here (written to
            // .exclude.json just like a judge drop so the final list = SRT via the old path).
            // Only runs at finish (not at pause: resume-finish recomputes).
            var bannerDrops = OcrBannerGuard.Evaluate(mergedAll, speechSegments, request.Duration);
            if (bannerDrops.Count > 0)
            {
                var added = await SaveExclusionsAsync(keyRequest,
                    bannerDrops.Select(drop => new OcrCueExclusion(drop.Start, drop.Text)).ToArray(),
                    token);
                var bannerSamples = string.Join(" · ", bannerDrops.Take(5).Select(drop =>
                    FormattableString.Invariant($"{drop.Start:0.0}s {drop.Reason}")));
                job.Log($"Guard banner: {bannerDrops.Count} câu chữ nền tĩnh loại khỏi SRT (+{added} mới): {bannerSamples}");
            }
            // Missing-sub warning (18/09): report only, never generate — the reviewer
            // manually checks segments that have audio but no sub.
            foreach (var warning in OcrMissingSubWarning.Find(speechSegments, mergedAll, request.Duration))
                job.Warn(warning);
        }
        string? pauseMessage = null;
        if (paused)
        {
            var pausedCheckpoint = checkpoint with { Lanes = lanes.ToList(), BoundaryMerges = boundaryMerges };
            await _checkpoints.SaveAsync(keyRequest, pausedCheckpoint, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            await processes.StopAsync();
            await _ocr.StopAsync();
            if (processes.ActiveCount != 0 || _ocr.Status.Workers != 0)
                throw new IOException($"Tạm dừng OCR chưa thu sạch tài nguyên: {processes.ActiveCount} FFmpeg/process tree · {_ocr.Status.Workers} Python worker.");
            pauseMessage = $"Đã tạm dừng an toàn tại {FormatClock(frontier)} · 0 Python worker · 0 FFmpeg/process tree.";
        }
        // KHONG xoa checkpoint khi finish (sua 2026-09-14, vu mat file p06):
        // export SRT chay SAU, o UI. Xoa truoc ma export hong (cong E.6 cu chan)
        // thi mat ca hai, dung nhu loi backup da hua. Giu checkpoint completed lam
        // duong lui cuoi (mo lai app van Tiep tuc/Xuat phuc hoi duoc); chi xoa khi
        // Fresh moi (dau scan) hoac tay (Quet lai tu dau). Ton vai MB/video, re.

        var frames = lanes.Sum(x => x.Frames);
        var images = lanes.Sum(x => x.OcrImages);
        var media = lanes.Sum(x => Math.Max(0, Math.Min(x.Segment.CoreEnd, x.MediaSeconds) - x.Segment.CoreStart));
        LogScanTelemetry(job, telemetry, inferenceBefore, selected, effectiveWorkers,
            frames, images, media, started.Elapsed.TotalSeconds, mergedAll.Count(x => x.NeedsReview), initialUnique);
        // Resume-based like live: media seeded from the old checkpoint, clock restarts from 0.
        var sessionMedia = Math.Max(0, media - initialUnique);
        var sessionRealtime = started.Elapsed.TotalSeconds > 0 ? sessionMedia / started.Elapsed.TotalSeconds : 0;
        var result = new OcrScanResult(
            merged, frames, images, media, started.Elapsed.TotalSeconds,
            sessionRealtime,
            selected, lanes.Count(x => x.Completed), boundaryMerges, decoder,
            effectiveWorkers, configuredWorkerKinds, frontier, mergedAll.Count, paused);

        job.SetResult(result);
        // Dump end-of-session pixel verdicts into the sidecar (best-effort, never throws):
        // the next finish/export/open-checkpoint avoids decoding already-verdicted frames.
        await FlushPixelSidecarAsync();
        if (paused)
        {
            job.PauseComplete(pauseMessage);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    public async Task<OcrCheckpointInfo> InspectCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        var checkpoint = await LoadResumeCheckpointAsync(request, cancellationToken);
        if (checkpoint is null) return new OcrCheckpointInfo(false, 0, 0, 0, 0, 0, 0, 0, [], []);
        return await _checkpoints.AssembleInfoAsync(checkpoint, request, cancellationToken);
    }

    // Loading a resume tolerates container padding (06/10, the pause-then-resume reporting
    // a lost checkpoint even though the file/key are intact): pause-save clamps the last lane to the
    // stream truth (~0.5s shorter than the container) so the 1ms end-match always fails.
    // Try exact first; on a miss, parse candidates with the same key, and if the span is shorter than
    // the duration yet matches the probed stream truth (the container tail has no frames,
    // and a fresh scan clamps identically) then accept it + report clearly. Overshoot/empty span/
    // probe errors are rejected as before. Does not touch IsValidCheckpoint.
    internal async Task<OcrParallelCheckpoint?> LoadResumeCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken)
    {
        var exact = await _checkpoints.LoadAsync(request, cancellationToken);
        if (exact is not null) return exact;
        var candidate = await _checkpoints.ParseCandidateAsync(request, cancellationToken);
        if (candidate?.Lanes is null || candidate.Lanes.Count == 0) return null;
        if (candidate.Lanes.Any(lane => lane is null || lane.Segment is null)) return null;
        var ordered = candidate.Lanes.OrderBy(lane => lane.Segment.Index).ToArray();
        var offset = request.ScanStartOffset;
        var spanEnd = ordered[^1].Segment.CoreEnd;
        if (!(spanEnd > offset && spanEnd < offset + request.Duration - 0.001)) return null;
        double streamEnd;
        try
        {
            var source = Path.GetFullPath(request.Path.Trim());
            var (laneProbe, _, _) = await ProbeVideoAsync(source, cancellationToken);
            streamEnd = laneProbe?.StreamDurationSeconds ?? 0;
        }
        catch { return null; }
        if (!(streamEnd > offset) || Math.Abs(spanEnd - streamEnd) > 0.001) return null;
        if (!OcrCheckpointStore.IsValidCheckpoint(candidate, spanEnd - offset, offset)) return null;
        return candidate;
    }
    public Task RemoveCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.RemoveAsync(request, cancellationToken);
    public IReadOnlyList<OcrCheckpointVideoInfo> FindVideoCheckpoints(string videoPath) => _checkpoints.FindByVideo(videoPath);
    public Task DeleteCheckpointBackupAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.DeleteBackupAsync(request, cancellationToken);
    public Task<IReadOnlyList<OcrCueExclusion>> LoadExclusionsAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.LoadExclusionsAsync(request, cancellationToken);
    public Task<int> SaveExclusionsAsync(OcrScanRequest request, IReadOnlyList<OcrCueExclusion> additions, CancellationToken cancellationToken) => _checkpoints.SaveExclusionsAsync(request, additions, cancellationToken);
    public Task<int> RemoveExclusionsAsync(OcrScanRequest request, IReadOnlyList<OcrCueExclusion> removals, CancellationToken cancellationToken) => _checkpoints.RemoveExclusionsAsync(request, removals, cancellationToken);
    public Task<IReadOnlyList<OcrCueFix>> LoadFixesAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.LoadFixesAsync(request, cancellationToken);
    public Task<int> SaveFixesAsync(OcrScanRequest request, IReadOnlyList<OcrCueFix> additions, CancellationToken cancellationToken) => _checkpoints.SaveFixesAsync(request, additions, cancellationToken);

    private async Task<int> SelectParallelismAsync(
        AppJob job,
        string ffmpeg,
        OcrScanRequest request,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        var value = request.Parallelism.Trim().ToLowerInvariant();
        if (value != "auto" && value.Length > 0)
        {
            if (!int.TryParse(value, out var explicitValue) || explicitValue < 1 || explicitValue > 16)
                throw new ArgumentException("Số luồng OCR phải là auto hoặc 1..16.");
            if (string.Equals(_ocr.Status.ActiveMode, "hybrid", StringComparison.OrdinalIgnoreCase)
                && explicitValue < 2)
                throw new ArgumentException("OCR Hybrid cần ít nhất 2 luồng để chạy đồng thời 1 GPU + 1 CPU.");
            EnsureResourceHeadroom(job, explicitValue, isManual: true, lastStable: 0);
            job.Set("benchmark", -1, $"OCR Manual Probe: {explicitValue} FFmpeg lanes + {OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, explicitValue)} shared workers...");
            await ProbeTopologyLevelAsync(ffmpeg, request, explicitValue, processes, actual =>
            {
                var resources = _hardware.ResourceSnapshot();
                job.SetResult(new OcrBenchmarkTelemetry(
                    explicitValue, 0, 16, actual, _ocr.Status.WorkerKinds,
                         $"{explicitValue} lanes / {actual} workers", OcrAutoResourcePolicy.FormatSnapshot(resources)));
            }, cancellationToken);
            job.Log($"Manual Probe {explicitValue}: topology đầy đủ PASS; bắt đầu quét với đúng {explicitValue} luồng.");
            return explicitValue;
        }

        var lastStable = 0;
        var lastThroughput = 0d;
        long observedVramPerGpuWorkerBytes = 0;
        var ladder = string.Equals(_ocr.Status.ActiveMode, "hybrid", StringComparison.OrdinalIgnoreCase)
            ? "2 → 4 → 8 → 16 (Hybrid bắt buộc tối thiểu 1 GPU + 1 CPU)"
            : "1 → 2 → 4 → 8 → 16";
        job.Log($"Auto Benchmark Predict → Probe → Commit: xét CPU/RAM/VRAM theo mốc {ladder}; VRAM tăng thêm được học từ delta thực tế của chính máy sau mỗi topology PASS, không dùng định mức cố định theo worker. Nếu một mốc không đạt, thử lùi các mức giữa trước khi khóa topology. Mỗi mức chỉ PASS khi đủ đúng N pipeline thật, còn reserve sau probe và throughput tăng ít nhất 10%.");
        var selected = await OcrTopologyBenchmark.SelectForModeAsync(
            _ocr.Status.ActiveMode,
            async (level, token) =>
            {
                var beforeWorkers = _ocr.Status.Workers;
                var beforeResources = _hardware.ResourceSnapshot();
                var preflight = EnsureResourceHeadroom(
                    job, level, isManual: false, lastStable: lastStable,
                    observedVramPerGpuWorkerBytes: observedVramPerGpuWorkerBytes,
                    resources: beforeResources, requireMeasuredVram: true);
                job.Set("benchmark", -1, $"OCR Auto Benchmark {level}/16: {level} FFmpeg lanes + {OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, level)} shared workers...");
                var probe = await ProbeTopologyLevelAsync(ffmpeg, request, level, processes, actual =>
                {
                    var resources = _hardware.ResourceSnapshot();
                    job.SetResult(new OcrBenchmarkTelemetry(
                        level, lastStable, 16, actual, _ocr.Status.WorkerKinds,
                         $"{level} FFmpeg lanes / {actual} OCR workers", OcrAutoResourcePolicy.FormatSnapshot(resources)));
                }, token);

                var afterResources = _hardware.ResourceSnapshot();
                 var addedWorkers = Math.Max(0, _ocr.Status.Workers - beforeWorkers);
                if (addedWorkers > 0
                    && beforeResources.VramTelemetryAvailable
                    && afterResources.VramTelemetryAvailable)
                {
                    var consumedVram = Math.Max(0, beforeResources.AvailableVramBytes - afterResources.AvailableVramBytes);
                    if (consumedVram > 0)
                    {
                        var measuredPerWorker = consumedVram / addedWorkers;
                        observedVramPerGpuWorkerBytes = Math.Max(observedVramPerGpuWorkerBytes, measuredPerWorker);
                        job.Log($"Auto VRAM learn {beforeWorkers}->{_ocr.Status.Workers} workers: delta ~{consumedVram / 1024d / 1024d:0} MiB; estimate ~{observedVramPerGpuWorkerBytes / 1024d / 1024d:0} MiB/GPU worker before margin.");
                    }
                }

                EnsureResourceHeadroom(
                    job, level, isManual: false, lastStable: lastStable,
                    observedVramPerGpuWorkerBytes: observedVramPerGpuWorkerBytes,
                    resources: afterResources, requireMeasuredVram: true, postProbe: true);

                if (!OcrAutoResourcePolicy.HasUsefulThroughputGain(lastThroughput, probe.Throughput))
                {
                    var gain = lastThroughput <= 0 ? 0 : (probe.Throughput / lastThroughput - 1) * 100;
                    throw new InvalidOperationException(
                        $"mức {level} chỉ tăng throughput {gain:0.0}% (< 10%) so với mức {lastStable}; không đáng đổi thêm RAM/VRAM");
                }
                lastStable = level;
                lastThroughput = probe.Throughput;
                job.SetResult(new OcrBenchmarkTelemetry(
                    level, lastStable, 16, _ocr.Status.Workers, _ocr.Status.WorkerKinds,
                    $"PASS · {probe.Throughput:0.00} mẫu/s", OcrAutoResourcePolicy.FormatSnapshot(afterResources)));
                job.Log($"Auto Benchmark {level}: {_ocr.Status.Workers} Python workers + {level} FFmpeg lanes PASS · {probe.Throughput:0.00} samples/s · {preflight.Summary} · {OcrAutoResourcePolicy.FormatSnapshot(afterResources)}.");
            },
            async (best, token) =>
            {
                job.Set("benchmark", -1, $"OCR Auto Rollback: dựng lại topology ổn định {best}...");
                 var workerTarget = OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, best);
                 var restored = await _ocr.ConfigureWorkerPoolAsync(workerTarget, token);
                 if (restored != workerTarget || !_ocr.Status.Ready)
                    throw new InvalidOperationException($"Không phục hồi đủ {best} Python worker sau benchmark lỗi; chỉ có {restored}.");
                var resources = _hardware.ResourceSnapshot();
                job.SetResult(new OcrBenchmarkTelemetry(
                    best, best, 16, restored, _ocr.Status.WorkerKinds,
                    "đã quay về mức PASS an toàn", OcrAutoResourcePolicy.FormatSnapshot(resources)));
            },
            (failed, best, error) =>
                job.Warn($"Auto Benchmark {failed} FAIL: {Compact(error.Message)} · đã quay về {best} pipeline ổn định trước khi xét mức thấp hơn."),
            cancellationToken);
        job.Set("benchmark", 1.5, $"Benchmark hoàn tất · khóa {selected} pipeline; chuẩn bị bắt đầu quét...");
        return selected;
    }

    private OcrResourceDecision EnsureResourceHeadroom(
        AppJob job,
        int candidate,
        bool isManual,
        int lastStable,
        long observedVramPerGpuWorkerBytes = 0,
        HardwareResourceSnapshot? resources = null,
        bool requireMeasuredVram = false,
        bool postProbe = false)
    {
        var hardware = _hardware.Snapshot();
        var live = resources ?? _hardware.ResourceSnapshot();
        var decision = OcrAutoResourcePolicy.Evaluate(
            hardware, live, _ocr.Status.ActiveMode, _ocr.Status.Workers, OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, candidate),
            observedVramPerGpuWorkerBytes, requireMeasuredVram, decoderLanes: candidate);
        var stable = isManual ? 0 : lastStable;
        var phase = postProbe ? "commit check" : "preflight";
        job.SetResult(new OcrBenchmarkTelemetry(
            candidate, stable, 16, _ocr.Status.Workers, _ocr.Status.WorkerKinds,
            decision.Allowed ? $"{phase} PASS; {(postProbe ? "reserve thực tế còn an toàn" : $"chuẩn bị tạo {candidate}")}" : $"{phase} STOP: " + decision.Reason,
            decision.Summary));
        if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
        job.Log($"Resource {phase} {candidate}: PASS · {decision.Summary}.");
        return decision;
    }

    private async Task<OcrTopologyProbe> ProbeTopologyLevelAsync(
        string ffmpeg,
        OcrScanRequest request,
        int level,
        OwnedProcessGroup processes,
        Action<int>? configured,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var workerTarget = OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, level);
            var actual = await _ocr.ConfigureWorkerPoolAsync(workerTarget, timeout.Token);
            if (actual != workerTarget || !_ocr.Status.Ready)
                throw new InvalidOperationException($"Created {actual}/{workerTarget} workers for {level} lanes.");
            configured?.Invoke(actual);
            async Task<OcrResult[]> RunRoundAsync(int round)
            {
                return await Task.WhenAll(Enumerable.Range(0, level).Select(async index =>
                {
                    var at = request.Duration * (round * level + index + 0.5) / (level * 3d);
                    var jpeg = await CaptureFrameWithFfmpegAsync(ffmpeg, request.Path, at, request.Region, false, processes, timeout.Token);
                    return await _ocr.RunAsync(Convert.ToBase64String(jpeg), timeout.Token);
                }));
            }

            var warmup = await RunRoundAsync(0);
            var warmupFailure = warmup.FirstOrDefault(result => !result.Ok);
            if (warmupFailure is not null)
                throw new InvalidOperationException(warmupFailure.Error ?? "OCR worker trả kết quả lỗi trong benchmark warm-up.");
            var watch = Stopwatch.StartNew();
            var probeResults = (await RunRoundAsync(1)).Concat(await RunRoundAsync(2)).ToArray();
            var failed = probeResults.FirstOrDefault(result => !result.Ok);
            if (failed is not null)
                throw new InvalidOperationException(failed.Error ?? "OCR worker trả kết quả lỗi trong benchmark.");
            if (_ocr.Status.Workers != workerTarget || !_ocr.Status.Ready)
                throw new InvalidOperationException($"Topology {level} lost workers during probe: {_ocr.Status.Workers}/{workerTarget}.");
            watch.Stop();
            return new OcrTopologyProbe(level * 2 / Math.Max(0.001, watch.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"topology {level} không hoàn tất probe trong 3 phút");
        }
    }

    private static void PublishTelemetry(
        AppJob job,
        IReadOnlyList<OcrLaneCheckpoint> lanes,
        IReadOnlyList<double> progress,
        IReadOnlyList<int> frames,
        IReadOnlyList<int> images,
        IReadOnlyList<bool> completed,
        int workers,
        string workerKinds,
        double duration,
        OcrLiveTailReconciler liveTail,
        IReadOnlyList<OcrCue?> liveActive,
        double elapsedSeconds,
        ScanPaceTracker pace)
    {
        var unique = lanes.Sum(x =>
            Math.Max(0, Math.Min(x.Segment.CoreEnd, progress[x.Segment.Index]) - x.Segment.CoreStart));
        var percent = duration > 0 ? Math.Clamp(unique / duration * 100, 0, 99.5) : 0;
        var frontier = 0d;
        foreach (var lane in lanes.OrderBy(x => x.Segment.Index))
        {
            if (completed[lane.Segment.Index]) frontier = lane.Segment.CoreEnd;
            else
            {
                frontier = Math.Max(frontier, Math.Clamp(progress[lane.Segment.Index], lane.Segment.CoreStart, lane.Segment.CoreEnd));
                break;
            }
        }
        var scanStart = lanes.Count > 0 ? lanes.Min(lane => lane.Segment.CoreStart) : 0;
        var frontierPercent = duration > 0
            ? Math.Clamp((frontier - scanStart) / duration * 100, 0, 99.5)
            : 0;
        var completedCount = completed.Count(x => x);
        // Live preview: the committed tail already went through incremental pixel-gating (OcrLiveTailReconciler),
        // and active provisional entries are stitched on just for display, never becoming authoritative. Final Finish/Export
        // uses the ReconcileAsync safety pass + _cues, the same collection semantics.
        // PERF (end-of-scan lag 15/9): the tail is fetched DIRECTLY from the reconciler under 1 lock
        // (GetTailBelow), not copying the entire _view every 500ms/lane. Results are IDENTICAL to the old
        // tail loop (max 120 cues at/below the frontier) but at constant cost.
        const int previewCueLimit = 120;
        List<OcrCue> tail;
        try { tail = liveTail.GetTailBelow(frontier, previewCueLimit); }
        catch (ObjectDisposedException) { tail = []; }
        var recentCues = tail
            .Concat(liveActive.Where(cue => cue is not null).Select(cue => cue!))
            .Where(cue => cue.Start <= frontier + .001)
            .OrderBy(cue => cue.Start).ThenBy(cue => cue.End)
            .TakeLast(previewCueLimit)
            .ToArray();
        // Real numbers (the fake .5x number case and the 281× panel): session average + instant over the last 5 minutes
        // + stalled lanes. Unique media is seeded from the old checkpoint but the clock restarts from 0
        // on resume, so SUBTRACT the session start offset — all throughput is extra work / extra run time.
        // A session-average realtime figure hides a gradually slowing pace which the 5m number exposes; a lane idle >3m gets flagged.
        var paceInfo = pace.Observe(unique, progress, completed);
        var sessionUnique = Math.Max(0, unique - pace.BaseUnique);
        var average = elapsedSeconds > 0 ? sessionUnique / elapsedSeconds : 0;
        var instant = paceInfo.Instant > 0 ? paceInfo.Instant : average;
        var stallNote = paceInfo.StalledLanes.Count == 0
            ? string.Empty
            : $" · kẹt lane {string.Join(",", paceInfo.StalledLanes)}>{ScanPaceTracker.StallSeconds / 60:0}p";
        job.Set("scanning", percent,
            $"Đang quét OCR · SRT liên tục {frontierPercent:0.0}% · tổng xử lý {percent:0.0}% · {completedCount}/{lanes.Count} lane xong · TB phiên {average:0.00}× · 5p {instant:0.00}×{stallNote}");
        job.SetResult(new OcrScanResult(
            recentCues, frames.Sum(), images.Sum(), unique, elapsedSeconds,
            average,
            lanes.Count, completedCount, 0, "live", workers, workerKinds, frontier, recentCues.Length, false));
    }

    // Real throughput over time: keep samples (timestamp, media ALREADY MINUS THE SESSION START OFFSET) in
    // the 5-minute sliding window; instant = delta media / delta time. Per-lane: remember media + the last
    // change point; a lane that is not finished and idle beyond StallSeconds is listed as stalled. Purely
    // diagnostic, does not interfere with scheduling/tracker/checkpoint.
    private sealed class ScanPaceTracker
    {
        public const double WindowSeconds = 300;
        public const double StallSeconds = 180;
        private readonly record struct Sample(long Ticks, double Unique);
        private readonly Queue<Sample> _window = new();
        private readonly double[] _lastMedia;
        private readonly long[] _lastChangeTicks;
        private readonly bool[] _stallReported;

        public double BaseUnique { get; }

        public ScanPaceTracker(int lanes, double baseUnique)
        {
            BaseUnique = baseUnique;
            _lastMedia = new double[Math.Max(0, lanes)];
            _lastChangeTicks = new long[Math.Max(0, lanes)];
            _stallReported = new bool[Math.Max(0, lanes)];
            var now = Stopwatch.GetTimestamp();
            for (var i = 0; i < _lastChangeTicks.Length; i++) _lastChangeTicks[i] = now;
        }

        public sealed record PaceInfo(double Average, double Instant, IReadOnlyList<int> StalledLanes);

        public PaceInfo Observe(double unique, IReadOnlyList<double> progress, IReadOnlyList<bool> completed)
        {
            var now = Stopwatch.GetTimestamp();
            var session = Math.Max(0, unique - BaseUnique);
            _window.Enqueue(new Sample(now, session));
            while (_window.Count > 1
                && Stopwatch.GetElapsedTime(_window.Peek().Ticks, now).TotalSeconds > WindowSeconds)
                _window.Dequeue();
            var elapsed = Stopwatch.GetElapsedTime(_window.Peek().Ticks, now).TotalSeconds;
            // Window-first: for a new scan (<10s of samples) without enough baseline, instant = average
            // at the call-site (the average is passed in); here return 0 to report insufficient samples.
            var instant = 0d;
            if (_window.Count > 1 && elapsed >= 10)
            {
                var oldest = _window.Peek();
                instant = Math.Max(0, (session - oldest.Unique) / Math.Max(1, elapsed));
            }
            var stalled = new List<int>();
            for (var i = 0; i < _lastMedia.Length && i < progress.Count && i < completed.Count; i++)
            {
                if (completed[i]) continue;
                if (Math.Abs(progress[i] - _lastMedia[i]) > 1e-9)
                {
                    _lastMedia[i] = progress[i];
                    _lastChangeTicks[i] = now;
                    _stallReported[i] = false;
                }
                else if (Stopwatch.GetElapsedTime(_lastChangeTicks[i], now).TotalSeconds >= StallSeconds)
                {
                    stalled.Add(i + 1);
                }
            }
            return new PaceInfo(0, instant, stalled);
        }
    }

    // Two-tier probe cache (memory/app-lifetime + sidecar/disk): video metadata is static so
    // 8 lanes + padding + NVDEC spawn ffprobe only once per scan (the first time), later scans read the
    // sidecar. Returns the newest sidecar doc + a sidecar-hit flag (for counting, not for gating).

    // Cold start with 8 lanes racing: same memory miss → same probe as today (no worse),
    // the first lane to finish writes the sidecar, later scans hit it. Null is memory-cache only.
    private Task<(VideoProbe? Probe, OcrSidecar.SidecarDocument? Doc, bool SidecarHit)> ProbeVideoAsync(
        string source, CancellationToken cancellationToken)
        => ProbeVideoAsync(source, OcrSidecar.FileIdentity.FromFile(source), cancellationToken);

    private async Task<(VideoProbe? Probe, OcrSidecar.SidecarDocument? Doc, bool SidecarHit)> ProbeVideoAsync(
        string source, OcrSidecar.FileIdentity? identity, CancellationToken cancellationToken)
    {
        var key = identity is null
            ? "noid|" + Path.GetFullPath(source.Trim()).ToLowerInvariant()
            : identity.Size + "|" + identity.ModUnixNano + "|" + identity.Path;
        if (_probeCache.TryGetValue(key, out var cached))
            return (cached, identity is null ? null : _sidecarDocs.GetValueOrDefault(key), false);
        OcrSidecar.SidecarDocument? doc = null;
        if (identity is not null)
        {
            try { doc = await _sidecars.LoadAsync(identity.Path, identity, cancellationToken); } catch { doc = null; }
            _sidecarDocs[key] = doc;
            if (OcrSidecar.ToProbe(doc?.Probe) is { } restored)
            {
                _probeCache[key] = restored;
                return (restored, doc, true);
            }
        }
        var probed = await OcrFrameExtractor.ProbeAsync(_tools, _processes, source, cancellationToken);
        _probeCache[key] = probed;
        if (identity is not null && probed is not null)
        {
            doc = new OcrSidecar.SidecarDocument(
                doc?.Identity ?? identity, OcrSidecar.FromProbe(probed), doc?.Vad, doc?.Pixel);
            _sidecarDocs[key] = doc;
            try { await _sidecars.SaveAsync(identity.Path, doc, CancellationToken.None); } catch { }
        }
        return (probed, doc, false);
    }

    // Sample frames to measure the subtitle style (auto-detection, no manual switch): 12 evenly spaced
    // frames within the scan range, cropped to the exact ROI, the worker measures the text ring. Returns
    // "off"|"medium"|"strong"; every error -> "off" (exactly as before). Best-effort: each
    // broken frame is skipped, and with fewer than 2 valid frames it stops.
    private async Task<string> ProbeSubtitleStyleAsync(
        AppJob job, string ffmpeg, string source, OcrScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "off";
            if (!(request.Duration > 60)) return "off";
            var region = request.Region;
            if (!(region.Width > 0) || !(region.Height > 0)) return "off";
            job.Set("style-probe", -1, "OCR · tự nhận diện kiểu sub (viền đen/bóng nhẹ/chữ màu)...");
            var crops = new List<string>(12);
            for (var index = 0; index < 12; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var at = request.ScanStartOffset + (request.Duration * (index + 0.5) / 12);
                byte[]? jpeg;
                try
                {
                    jpeg = await _processes.CaptureBytesAsync(ffmpeg,
                        ["-hide_banner", "-loglevel", "error", "-nostdin",
                         "-ss", at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                         "-i", source, "-map", "0:v:0", "-an", "-sn", "-dn",
                         "-frames:v", "1", "-q:v", "6", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"],
                        cancellationToken);
                }
                catch { continue; }
                if (jpeg is null || jpeg.Length == 0 || jpeg.Length > 8 * 1024 * 1024) continue;
                var crop = OcrImageCrop.CropJpeg(jpeg, region.X, region.Y, region.Width, region.Height);
                if (crop is null || crop.Length == 0) continue;
                crops.Add(Convert.ToBase64String(crop));
            }
            if (crops.Count < 2) return "off";
            var scores = await _ocr.ProbeSubtitleStyleAsync(crops, cancellationToken);
            return OcrSubtitleStyle.Decide(scores);
        }
        catch
        {
            return "off";
        }
    }

    private async Task<OcrLaneCheckpoint> RunLaneWithFallbackAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint lane,
        string decoder,
        Action<OcrLaneCheckpoint, IReadOnlyList<OcrCue>, bool> onProgress,
        AppJob job,
        OwnedProcessGroup processes,
        OcrScanTelemetryCounters telemetry,
        CancellationToken cancellationToken,
        Func<VadTimeline?>? vadSource = null)
    {
        if (lane.Completed) return lane;
        // A boot that picks software keeps the old path intact (nothing to retry with NVDEC).
        if (decoder != "nvdec")
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, nvdec: false, onProgress, job, processes, telemetry, cancellationToken, vadSource);
        for (var attempt = 1; attempt <= MaxNvdecLaneAttempts; attempt++)
        {
            try
            {
                return await RunLaneAsync(ffmpeg, source, request, mode, lane, nvdec: true, onProgress, job, processes, telemetry, cancellationToken, vadSource);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OcrRecognitionException)
            {
                if (attempt < MaxNvdecLaneAttempts)
                {
                    var backoff = NvdecRetryBackoffSeconds * Math.Pow(2, attempt - 1);
                    Interlocked.Increment(ref telemetry.NvdecRetries);
                    job.Log($"Lane {lane.Segment.Index + 1}: NVDEC lỗi (lần {attempt}/{MaxNvdecLaneAttempts}): {Compact(error.Message)} · retry sau {backoff:0.0}s từ vị trí {FormatClock(lane.MediaSeconds)}.");
                    await Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken);
                    continue;
                }
                Interlocked.Increment(ref telemetry.NvdecFailures);
                Interlocked.Increment(ref telemetry.SoftwareFallbacks);
                job.Log($"Lane {lane.Segment.Index + 1}: NVDEC lỗi {MaxNvdecLaneAttempts}/{MaxNvdecLaneAttempts} lần — fallback software cho phần còn lại của lane ({Compact(error.Message)}).");
                return await RunLaneAsync(ffmpeg, source, request, mode, lane, nvdec: false, onProgress, job, processes, telemetry, cancellationToken, vadSource);
            }
        }
        throw new InvalidOperationException("NVDEC retry ladder không thể tới đây: for-loop luôn return hoặc throw.");
    }

    private async Task<OcrLaneCheckpoint> RunLaneAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint saved,
        bool nvdec,
        Action<OcrLaneCheckpoint, IReadOnlyList<OcrCue>, bool> onProgress,
        AppJob job,
        OwnedProcessGroup processes,
        OcrScanTelemetryCounters telemetry,
        CancellationToken cancellationToken,
        Func<VadTimeline?>? vadSource = null)
    {
        var segment = saved.Segment;
        var startAtMedia = Math.Clamp(saved.MediaSeconds, segment.ScanStart, segment.ScanEnd);
        var (laneProbe, _, laneProbeHit) = await ProbeVideoAsync(source, cancellationToken);
        if (laneProbeHit) Interlocked.Increment(ref telemetry.SidecarProbeHits);
        var videoProbe = laneProbe;
        var streamStart = videoProbe?.StartTimeSeconds ?? 0;
        OcrFrameGeometry? laneGeometry = null;
        RationalTimeBase? laneTb = null;
        if (videoProbe is { } vp)
        {
            if (ProductionPixelComparer.TryPlanOcrFrame(vp.Width, vp.Height, request.Region) is { } gg)
                laneGeometry = gg;
            if (vp.TimeBaseNum > 0 && vp.TimeBaseDen > 0)
                laneTb = new RationalTimeBase(vp.TimeBaseNum, vp.TimeBaseDen);
        }
        var tracker = new SubtitleTracker(mode.Fps, mode.LowConfidence, exactFrameTiming: mode.AdaptiveTiming, sourceRoi: request.Region, laneGeometry: laneGeometry, streamStartTime: streamStart, sourceTimeBase: laneTb);
        tracker.Restore(saved.Cues, saved.Active);
        var publishedCueCount = tracker.Cues.Count;
        // PTS domain: segment/saved.MediaSeconds are media-relative (0-based for SRT).
        // The FFmpeg lane uses absolute (media + streamStart) with -seek_timestamp 1 + -copyts.
        var absoluteStart = startAtMedia + streamStart;
        var absoluteEnd = segment.ScanEnd + streamStart;
        var args = BuildLaneArguments(source, request.Region, mode, absoluteStart, absoluteEnd, nvdec, videoProbe);
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var ownership = processes.Track(process);
        using var registration = cancellationToken.Register(() => Kill(process));
        // PTS domain: showinfo is absolute (copyts, no offset). The tracker receives absolute values and subtracts streamStart itself to get media time for cues/SRT.
        var timestamps = new FrameTimestampReader(process.StandardError);
        var errorTask = timestamps.Completion;
        var reader = new JpegStreamReader(process.StandardOutput.BaseStream);
        var frames = saved.Frames;
        var images = saved.OcrImages;
        var mediaSeconds = startAtMedia;
        // Native frame duration from ffprobe, never the sampling FPS: balanced
        // samples at 2.5 fps but each decoded frame still lasts 1/native fps.
        var probedFrameDuration = videoProbe is null ? 0 : videoProbe.FrameDurationSeconds;
        var nativeFrameDuration = probedFrameDuration > 0 ? probedFrameDuration : 1 / mode.Fps;
        var lastFrameDuration = mode.AdaptiveTiming ? 0 : nativeFrameDuration;
        var paused = false;
        var stoppedAtBoundary = false;
        var adaptiveWindow = new List<OcrBufferedFrame>();
        OcrBufferedFrame? previousVisualAnchor = null;
        var pendingVisualFollowups = 0;
        // E.5 ROI static zones (per-lane, shadow): hoc vung chu lap lai (watermark).
        // Chi ghi (zones + would-filter), khong loc that.
        var roiZones = new OcrRoiStaticZones();
        // E.4 canary state (per-lane): vong retro 0.5Hz (toi da 45 frame ~2.8MB,
        // giu tham chieu JPEG san co) + trigger dem + tat reuse khi hit.
        // Manual-reset = scan moi (flag theo scope lane).
        var retroBuffer = new List<(OcrBufferedFrame Frame, bool Detected, string Text)>();
        long paddleSinceAudit = 0;
        long lastFullOcrTicks = Stopwatch.GetTimestamp();
        var reuseEnabled = OcrGateShadow.ReuseEnabledDefault;
        var canaryFlip = 0L;
        var sampleCounter = 0L;
        // E.4b2: tran carry lien tiep (fade-in/out dan: moi buoc doi < eps nhung
        // cong don thanh chu that, canary 12:01 + 12:17 bat 2 ca 2 huong).
        // Qua N carry thi bat buoc doc that 1 lan de chat chuoi.
        long consecutiveCarries = 0;
        const long MaxConsecutiveCarries = 5;
        // Council v1 (E.5 enforce): blank anchor + count consecutive skips for ShouldCouncilSkip.
        // prevBlankAnchored only becomes true via a full-read BLANK (skip keeps its value, full-read
        // with text/audit/resume clears it) — a skip chain always anchors to the nearest 1 genuine read.
        long consecutiveSkips = 0;
        bool prevBlankAnchored = false;
        // Council skip: RE-ENABLE when the timeline comes from Whisper EAR LISTENING (17/09) — exactly
        // the "fixed VAD" condition OcrGateShadow requires to re-enable it (energy-based VAD
        // false-silence ate real text so it was disabled 16/09; ear listening distinguishes
        // speech/silence correctly so systematic VAD errors end). Signal: the timeline has
        // the HasSub flag (only Whisper generates it). Multi-gate conservatism + cap + canary audit
        // + the kill-switch stays when canary-hit — a wrong skip is caught by the canary immediately.
        // No ear (energy timeline/old sidecar) -> still OFF as before.
        var councilSkipsEnabled = OcrGateShadow.CouncilEnabledDefault
            || vadSource?.Invoke()?.Flags.Contains(VadFlag.HasSub) == true;
        OcrResult? previousAdaptiveSample = mode.AdaptiveTiming && tracker.Active is { } restoredActive
            ? new OcrResult(true, true, restoredActive.Text, restoredActive.Confidence, [])
            : null;
        var pendingTinyGlyphRecovery = 0;
        var nextAdaptiveSampleMedia = startAtMedia;
        var lastProgressPublish = Stopwatch.GetTimestamp();
        async Task<OcrResult> RecognizeSingleAsync(
            OcrBufferedFrame frame,
            bool recoverShortBlank,
            bool allowEnhanced = false,
            string purpose = "sample")
        {
            OcrResult result;
            var activeShortText = tracker.Active?.Text is { } activeText && activeText.EnumerateRunes().Count() == 1
                ? activeText
                : null;
            // The lane's OCR path: if a batch-scavenging scope exists, go through it (cap=1 = old path),
            // if not (outside a scan), call the manager directly. Signature/timing measurement as before.
            Task<OcrResult> SubmitOcrAsync(string base64, bool recover, string? active, Action<OcrPerfSample> perf)
            {
                var scope = _ambientBatchScope.Value;
                return scope is null
                    ? _ocr.RunAsync(base64, cancellationToken, recover, active, perf)
                    : scope.SubmitAsync(base64, cancellationToken, recover, active, perf);
            }
            try
            {
                RecordFlagCall(recoverShortBlank, activeShortText);
                var requestWatch = Stopwatch.StartNew();
                var raw = await SubmitOcrAsync(
                    frame.Base64,
                    recoverShortBlank, activeShortText,
                    sample => RecordPerfSample(sample));
                RecordRequestSample(requestWatch.ElapsedMilliseconds);
                if (HasTinyNonChineseCompanion(raw))
                    pendingTinyGlyphRecovery = Math.Max(pendingTinyGlyphRecovery, 4);
                result = FilterOffBaselineOverlayLines(raw, request.Region);
                result = ApplyLuminanceGate(frame.Jpeg, result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new OcrRecognitionException("OCR worker lỗi: " + error.Message, error);
            }
            if (!result.Ok)
                throw new OcrRecognitionException(result.Error ?? "OCR worker trả kết quả lỗi.");
            Interlocked.Increment(ref telemetry.SingleCalls);
            RecordOcrObservation(telemetry, frame.Timing.SourcePts, result, purpose);
            var tinyGlyphRecovery = pendingTinyGlyphRecovery > 0;
            if (!tinyGlyphRecovery && (!allowEnhanced || !NeedsEnhancedRecognition(result, Math.Max(.78, mode.LowConfidence + .10))))
                return result;
            if (tinyGlyphRecovery) pendingTinyGlyphRecovery--;
            try
            {
                var enhanced = await CaptureFrameWithFfmpegAsync(
                    ffmpeg, source, frame.Timing.PresentationTime, request.Region,
                    enhanced: true, processes, cancellationToken);
                var enhancedWatch = Stopwatch.StartNew();
                var alternate = FilterOffBaselineOverlayLines(await SubmitOcrAsync(
                    Convert.ToBase64String(enhanced),
                    recoverShortBlank, activeShortText, sample => RecordPerfSample(sample)), request.Region);
                alternate = ApplyLuminanceGate(enhanced, alternate);
                RecordRequestSample(enhancedWatch.ElapsedMilliseconds);
                RecordFlagCall(recoverShortBlank, activeShortText);
                if (alternate.Ok && PreferRecognition(alternate, result))
                {
                    var recoveredMoreText = alternate.Text.EnumerateRunes().Count() > result.Text.EnumerateRunes().Count();
                    result = alternate;
                    if (recoveredMoreText) pendingTinyGlyphRecovery = 0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                // A second-pass sample is optional. Never discard the valid
                // first pass or stop a long scan because refinement failed.
                job.Log($"OCR frame {frame.Timing.PresentationTime:0.000}s: enhanced retry skipped ({Compact(error.Message)}).");
            }
            return result;
        }

        async Task<IReadOnlyList<OcrResult>> RecognizeBatchAsync(
            IReadOnlyList<OcrBufferedFrame> batch,
            bool recoverShortBlank,
            string purpose)
        {
            if (batch.Count == 0) return [];
            var activeShortText = tracker.Active?.Text is { } activeText && activeText.EnumerateRunes().Count() == 1
                ? activeText
                : null;
            IReadOnlyList<OcrResult> results;
            try
            {
                RecordFlagCall(recoverShortBlank, activeShortText);
                var batchWatch = Stopwatch.StartNew();
                results = await _ocr.RunBatchAsync(
                    batch.Select(frame => frame.Base64).ToArray(),
                    cancellationToken, recoverShortBlank, activeShortText,
                    sample => RecordPerfSample(sample));
                RecordRequestSample(batchWatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new OcrRecognitionException("OCR worker batch lỗi: " + error.Message, error);
            }
            Interlocked.Increment(ref telemetry.BatchCalls);
            return results.Select(raw =>
            {
                if (HasTinyNonChineseCompanion(raw))
                    pendingTinyGlyphRecovery = Math.Max(pendingTinyGlyphRecovery, 4);
                var result = FilterOffBaselineOverlayLines(raw, request.Region);
                if (!result.Ok) throw new OcrRecognitionException(result.Error ?? "OCR worker trả kết quả batch lỗi.");
                return result;
            }).Select((result, index) =>
            {
                result = ApplyLuminanceGate(batch[index].Jpeg, result);
                RecordOcrObservation(telemetry, batch[index].Timing.SourcePts, result, purpose);
                return result;
            }).ToArray();
        }

        async Task<VisualProbeResult> ProbeVisualChangesAsync(IReadOnlyList<OcrBufferedFrame> probeFrames)
        {
            try
            {
                var probe = await _ocr.ProbeVisualChangesExAsync(
                    probeFrames.Select(frame => frame.Base64).ToArray(),
                    cancellationToken);
                Interlocked.Increment(ref telemetry.ProbeCalls);
                Interlocked.Add(ref telemetry.ProbeImages, probeFrames.Count);
                foreach (var score in probe.ChangeScores)
                {
                    if (IsAdaptiveVisualChange(score)) Interlocked.Increment(ref telemetry.ProbeChangeFrames);
                    else Interlocked.Increment(ref telemetry.ProbeSameFrames);
                }
                return probe;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new OcrRecognitionException("OCR visual probe lỗi: " + error.Message, error);
            }
        }

        // E.3 SHADOW: ghi diem B (band-diff cuoi) + C (edge-density sample) cung
        // ket qua production (detected). CHI GHI, khong bao gio skip/quyet dinh.
        // Worker cu thieu field -> vang mat am tham. Loi shadow khong giet lane.
        void RecordShadowGate(VisualProbeResult probe, bool detected, long elapsedMs)
        {
            try
            {
                if (probe.BandChangeScores is not { Count: > 0 } band
                    || probe.EdgeDensity is not { Count: > 0 } edge)
                    return;
                var bandBucket = OcrGateShadow.Bucketize(band[^1]);
                var edgeBucket = OcrGateShadow.Bucketize(edge[^1]);
                if (detected)
                {
                    Interlocked.Increment(ref telemetry.ShadowBandText[bandBucket]);
                    Interlocked.Increment(ref telemetry.ShadowEdgeText[edgeBucket]);
                }
                else
                {
                    Interlocked.Increment(ref telemetry.ShadowBandBlank[bandBucket]);
                    Interlocked.Increment(ref telemetry.ShadowEdgeBlank[edgeBucket]);
                }
                Interlocked.Increment(ref telemetry.GateBandDiffs);
                Interlocked.Increment(ref telemetry.GateTextPresence);
                Interlocked.Increment(ref telemetry.ShadowObservations);
                Interlocked.Add(ref telemetry.ShadowMsTotal, elapsedMs);
                Interlocked.Add(ref telemetry.ProbeMsTotal, elapsedMs);
            }
            catch { }
        }

        // Candidate 2 luminance SHADOW (pinned spec 16/09): measure the brightness signature of each OCR
        // line (white point >=224 + dark point <64), count would-drops, NEVER drop any line
        // (EnforceDefault=false). GDI+ decode errors -> passthrough. Never throw at the lane.
        // The CHROMA colour gate (locked by user 18/09: subs are always WHITE, coloured text is always excluded):
        // runs AFTER luminance on the filtered result, with direct enforcement
        // (EnforceDefault=true) — a line with no white point and excess colour is dropped,
        // fail-open on every decode/box error. White text on a coloured background (blue sky) is kept
        // thanks to the white gate running first.
        OcrResult ApplyLuminanceGate(byte[] jpeg, OcrResult result)
        {
            try
            {
                var gated = OcrLineLuminance.Apply(jpeg, result,
                    laneGeometry?.CanvasW ?? 0, laneGeometry?.CanvasH ?? 0,
                    OcrLineLuminance.EnforceDefault);
                if (gated.Stats.MeasuredLines > 0 || gated.Stats.DecodeFails > 0)
                    Interlocked.Increment(ref telemetry.LumFrames);
                Interlocked.Add(ref telemetry.LumLines, gated.Stats.MeasuredLines);
                Interlocked.Add(ref telemetry.LumWouldDrop, gated.Stats.WouldDropLines);
                Interlocked.Add(ref telemetry.LumDecodeFails, gated.Stats.DecodeFails);
                var chroma = OcrLineChroma.Apply(jpeg, gated.Result,
                    laneGeometry?.CanvasW ?? 0, laneGeometry?.CanvasH ?? 0,
                    OcrLineChroma.EnforceDefault);
                if (chroma.Stats.MeasuredLines > 0 || chroma.Stats.DecodeFails > 0)
                    Interlocked.Increment(ref telemetry.ChromaFrames);
                Interlocked.Add(ref telemetry.ChromaLines, chroma.Stats.MeasuredLines);
                Interlocked.Add(ref telemetry.ChromaDropped, chroma.Stats.WouldDropLines);
                Interlocked.Add(ref telemetry.ChromaDecodeFails, chroma.Stats.DecodeFails);
                return chroma.Result;
            }
            catch { return result; }
        }

        // Round-trip per OCR request (queue + IPC + inference): total + max (CAS lock-free)
        // + count requests >=30s. Max latency is the gate condition for lowering the 90s timeout later.
        void RecordRequestSample(long milliseconds)
        {
            try
            {
                if (milliseconds < 0) return;
                Interlocked.Add(ref telemetry.RequestMsTotal, milliseconds);
                if (milliseconds >= 30_000) Interlocked.Increment(ref telemetry.Over30sRequests);
                long observed, computed;
                do
                {
                    observed = Interlocked.Read(ref telemetry.MaxRequestMs);
                    computed = Math.Max(observed, milliseconds);
                }
                while (observed != computed
                    && Interlocked.CompareExchange(ref telemetry.MaxRequestMs, computed, observed) != observed);
            }
            catch { }
        }

        // D.1 flag-histogram + det/rec split: dem combo co moi paddle_call + cong don
        // timing. CHI GHI CHEP. Callback perf chay trong worker thread: lock Gate.
        void RecordFlagCall(bool recoverShortBlank, string? activeShortText)
        {
            try
            {
                var bucket = OcrGateShadow.FlagBucket(recoverShortBlank, activeShortText);
                if ((uint)bucket < (uint)telemetry.FlagCalls.Length)
                    Interlocked.Increment(ref telemetry.FlagCalls[bucket]);
            }
            catch { }
        }

        void RecordPerfSample(OcrPerfSample sample)
        {
            try
            {
                lock (telemetry.Gate)
                {
                    telemetry.PaddleDetMs += sample.DetMs;
                    telemetry.PaddleRecMs += sample.RecMs;
                    telemetry.PaddleDetBoxes += sample.DetBoxes;
                    telemetry.PaddleRecCrops += sample.RecCrops;
                    telemetry.PaddleCacheHits += sample.CacheHits;
                }
            }
            catch { }
        }

        // E.4b canary audit: doc lai 1 mau bo-qua bang OCR day du tu vong retro.
        // Stratified: doi nhau blank-carry / text-carry, lay mau moi nhat.
        // Hit -> WARN to + tat skip ca scan (reuse dormant + council: quay ve doc day du).
        // VAD responsibility: an audit sample marked silent that reads as text means the ear lied (counted separately).
        // Khong backfill (nguy hiem PTS nguoc) + khong bao gio giet lane.
        // Manual-reset = scan moi (flag theo scope lane).
        async Task RunCanaryAuditAsync()
        {
            try
            {
                if (retroBuffer.Count == 0) return;
                var wantBlank = canaryFlip++ % 2 == 0;
                var pick = retroBuffer.FindLastIndex(item => item.Detected == !wantBlank);
                if (pick < 0) pick = retroBuffer.Count - 1;
                var sample = retroBuffer[pick];
                var audit = await RecognizeSingleAsync(sample.Frame, recoverShortBlank: false);
                images++;
                consecutiveCarries = 0;
                consecutiveSkips = 0;
                prevBlankAnchored = false;
                Interlocked.Increment(ref telemetry.RetroOcrCalls);
                paddleSinceAudit = 0;
                lastFullOcrTicks = Stopwatch.GetTimestamp();
                if (!OcrGateShadow.IsCanaryHit(sample.Detected, sample.Text, audit.Detected, audit.Text))
                    return;
                Interlocked.Increment(ref telemetry.CanaryHits);
                var auditVote = vadSource?.Invoke()?.At(sample.Frame.Timing.PresentationTime - streamStart) ?? VadFlag.Suspicious;
                if (auditVote == VadFlag.NoSub)
                    Interlocked.Increment(ref telemetry.CanaryVadAttributed);
                reuseEnabled = false;
                councilSkipsEnabled = false;
                retroBuffer.Clear();
                job.Warn($"CANARY HIT o {FormatClock(sample.Frame.Timing.PresentationTime - streamStart)}: mau bo-qua ( {(sample.Detected ? "co chu" : "trang")} ) doc lai ra {(audit.Detected ? "co chu" : "trang")} (tai: {auditVote}) "
                    + "-> da TAT skip ca scan nay, quay ve doc day du. Mo scan moi de bat lai (manual-reset).");
            }
            catch { }
        }

        void PublishProgress(bool force = false)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(lastProgressPublish, now) < TimeSpan.FromMilliseconds(500)) return;
            lastProgressPublish = now;
            var committedCues = tracker.Cues.Count > publishedCueCount
                ? tracker.Cues.Skip(publishedCueCount).ToArray()
                : Array.Empty<OcrCue>();
            publishedCueCount = tracker.Cues.Count;
            onProgress(saved with
            {
                MediaSeconds = mediaSeconds,
                // PERF (end-of-scan lag): the full Cues list is only used when
                // checkpointSafe (cloned into recoverableLanes). Regular progress reports
                // (500ms/lane) have nobody reading Cues -> send empty, avoiding copying thousands of cues
                // each time. The pause/recovery/final paths read tracker.Cues directly themselves.
                Cues = tracker.CanCheckpoint ? tracker.Cues.ToList() : [],
                Active = tracker.Active,
                Frames = frames,
                OcrImages = images,
                Completed = false,
            }, committedCues, tracker.CanCheckpoint);
        }

        async Task ProcessAdaptiveWindowAsync()
        {
            if (adaptiveWindow.Count == 0) return;
            if (mode.ExhaustiveRecognition)
            {
                // Quality-first mode: every native frame reaches PaddleOCR.
                // Batching only changes transport/inference overhead; tracker
                // observations remain in exact source-PTS order.
                for (var offset = 0; offset < adaptiveWindow.Count; offset += OcrRequestBatcher.WorkerBatchLimit)
                {
                    var batch = adaptiveWindow.Skip(offset).Take(OcrRequestBatcher.WorkerBatchLimit).ToArray();
                    var results = await RecognizeBatchAsync(batch, recoverShortBlank: tracker.Active is not null, purpose: "window");
                    images += batch.Length;
                    for (var index = 0; index < batch.Length; index++)
                        tracker.Observe(batch[index].Timing.SourcePts, batch[index].Timing.PresentationTime, batch[index].Timing.Duration, results[index]);
                }
                var last = adaptiveWindow[^1];
                previousAdaptiveSample = tracker.Active is { } active
                    ? new OcrResult(true, true, active.Text, active.Confidence, [])
                    : new OcrResult(true, false, string.Empty, 0, []);
                previousVisualAnchor = last;
                adaptiveWindow.Clear();
                PublishProgress();
                return;
            }
            var sample = adaptiveWindow[^1];
            // E.4a: probe truoc (von da chay moi window) de lay band-diff cho reuse.
            // Thu tu cu (recognize roi probe) doi thanh (probe roi recognize): lane fail
            // khi probe hong thi truoc sau deu fail nhu nhau, khong doi hanh vi.
            VisualProbeResult? earlyProbe = null;
            var probeWatch = Stopwatch.StartNew();
            if (previousVisualAnchor is not null)
            {
                var probeFrames = new[] { previousVisualAnchor }.Concat(adaptiveWindow).ToArray();
                earlyProbe = await ProbeVisualChangesAsync(probeFrames);
            }
            var probeMs = probeWatch.ElapsedMilliseconds;
            OcrResult sampleResult;
            // A skip sample (fake blank, NOT a real read) must not enter the tracker and
            // must not become a scene-change anchor (18/09: a fake ObserveEmpty wrongly closed
            // an open cue + a fake blank hides the real text boundary).
            var skippedSample = false;
            var prevSample = previousAdaptiveSample;
            // Dieu kien provenance: carry text DOI HOI boxes (prev.Lines). Truong hop
            // resume gieo prev rong (Active restore khong co Lines) hoac overlay-loc het
            // lines: buoc doc that de lay boxes, khong carry mu.
            // Carry blank khong can boxes (blank khong tao cue can merge).
            var carryHasProvenance = prevSample is null || !prevSample.Detected || prevSample.Lines.Count > 0;
            if (reuseEnabled && consecutiveCarries < MaxConsecutiveCarries && prevSample is not null && carryHasProvenance && earlyProbe is not null
                && OcrGateShadow.ShouldReuse(earlyProbe.BandChangeScores?[^1], hasPrevious: true))
            {
                // B-reuse: frame gan nhu he frame truoc -> be ket qua truoc sang,
                // KHONG goi Paddle (images khong tang: day la tiet kiem that).
                // GIU Lines (boxes): tracker dung boxes lam pixel provenance cho merge
                // sau nay. Bo Lines ([]) se lam mat provenance -> block merge vinh vien.
                // Boxes van dung vi 2 frame near-dup (tien de cua reuse).
                sampleResult = new OcrResult(true, prevSample.Detected, prevSample.Text, prevSample.Confidence, prevSample.Lines);
                consecutiveCarries++;
                Interlocked.Increment(ref telemetry.CouncilSkips);
                if (sampleCounter % 5 == 0)
                {
                    retroBuffer.Add((sample, sampleResult.Detected, sampleResult.Text));
                    if (retroBuffer.Count > 45) retroBuffer.RemoveAt(0);
                }
            }
            else if (councilSkipsEnabled
                && OcrGateShadow.ShouldCouncilSkip(
                    (vadSource?.Invoke()?.At(sample.Timing.PresentationTime - streamStart) ?? VadFlag.Suspicious) == VadFlag.NoSub,
                    earlyProbe?.BandChangeScores?[^1],
                    prevBlankAnchored,
                    consecutiveSkips))
            {
                // Council SKIP (E.5 enforce): silence + still + real-read blank anchor — do NOT call
                // Paddle (image count does not increase: real savings). Write an empty blank, not boxes
                // (a blank creates no cue so no provenance is needed — the provenance comment above).
                // 18/09: this blank is FAKE (not read) so forbid it from entering the tracker below —
                // the tracker only listens to real read results. Retrospective for the canary to watch; cap the chain
                // with CouncilMaxSkips in the pure function.
                sampleResult = new OcrResult(true, false, string.Empty, 0, []);
                skippedSample = true;
                consecutiveSkips++;
                Interlocked.Increment(ref telemetry.CouncilSkips);
                if (sampleCounter % 5 == 0)
                {
                    retroBuffer.Add((sample, false, string.Empty));
                    if (retroBuffer.Count > 45) retroBuffer.RemoveAt(0);
                }
            }
            else
            {
                sampleResult = await RecognizeSingleAsync(sample, recoverShortBlank: false);
                images++;
                consecutiveCarries = 0;
                consecutiveSkips = 0;
                prevBlankAnchored = !sampleResult.Detected;
                Interlocked.Increment(ref telemetry.CouncilScans);
                paddleSinceAudit++;
                lastFullOcrTicks = Stopwatch.GetTimestamp();
            }
            sampleCounter++;
            if (earlyProbe is not null)
                RecordShadowGate(earlyProbe, sampleResult.Detected, probeMs);
            var transition = previousAdaptiveSample is not null
                && NeedsAdaptiveRefinement(previousAdaptiveSample, sampleResult);

            // The short-glyph recovery policy is deliberately bounded to one
            // sampled transition. It no longer retries every blank source frame.
            if (transition && tracker.Active is not null && !sampleResult.Detected)
            {
                var recovered = await RecognizeSingleAsync(sample, recoverShortBlank: true);
                if (recovered.Detected)
                {
                    // A real read recovering over a blank (including a fake blank from a skip) ->
                    // clear the skip flag so the tracker can hear the real result below.
                    sampleResult = recovered;
                    skippedSample = false;
                }
                paddleSinceAudit++;
                lastFullOcrTicks = Stopwatch.GetTimestamp();
                transition = previousAdaptiveSample is not null
                    && NeedsAdaptiveRefinement(previousAdaptiveSample, sampleResult);
            }

            IReadOnlyList<OcrBufferedFrame> refinementFrames = [];
            if (transition)
            {
                // A sampled text difference alone is weak evidence: Paddle
                // misreads flicker constantly on textured scenes, and refining
                // the whole window on every flicker approaches every-frame
                // inference. Gate on the cheap visual probe instead: refine
                // around real visual boundaries, or confirm with a bounded
                // subset when the probe is quiet.
                // A new transition supersedes confirmations carried for the old
                // sample context; the boundary evidence below replaces them.
                pendingVisualFollowups = 0;
                IReadOnlyList<int> candidateIndexes = [];
                if (previousVisualAnchor is not null)
                {
                    // Probe da chay o dau window (earlyProbe): tai su dung, khong goi lai.
                    // earlyProbe chac chan non-null o nhanh nay (probe hong thi lane da fail o tren).
                    var scores = earlyProbe!.ChangeScores;
                    candidateIndexes = SelectTransitionRefinementFrames(scores, ref pendingVisualFollowups);
                    pendingVisualFollowups = 0;
                }
                var windowLength = adaptiveWindow.Count - 1;
                // Cost guard (26/09): the transition budget must not grow for
                // sparser sampling, or Balanced/Fast do MORE Paddle calls per
                // boundary than Accurate (fewer baselines, costlier transitions)
                // and the promised speed ranking inverts on dense dialogue.
                // Probe-nominated edges stay covered (earliest/latest kept);
                // the quiet-case spread below still nets weak-probe captions.
                refinementFrames = (candidateIndexes.Count > 0
                    ? CapRefinementIndexes(candidateIndexes, windowLength, maximum: 4)
                    : BoundedTransitionConfirmationFrames(windowLength, maximum: mode.Fps >= 4 ? 2 : 5))
                    .Select(index => adaptiveWindow[index]).ToArray();
            }
            else if (previousVisualAnchor is not null)
            {
                // Probe every native frame, but recognize only the two sides of
                // a visual boundary and its confirmation frames. Animated
                // backgrounds can nominate nearly the whole window even while
                // the sampled subtitle is stable; retain four evenly spread
                // representatives so that noise cannot restore exhaustive OCR.
                // Probe da chay o dau window (earlyProbe): tai su dung, khong goi lai.
                var scores = earlyProbe!.ChangeScores;
                var candidateIndexes = SelectAdaptiveRefinementFrames(scores, ref pendingVisualFollowups);
                // When the same subtitle is already present at both sampled
                // ends, changes on most native frames are animated background,
                // particles or camera motion rather than a hidden cue boundary.
                // Do not spend Paddle inference on that dense noise. Sparse
                // paired boundaries and blank-to-blank windows are retained so
                // a complete short caption between baseline samples still has
                // native-frame confirmations.
                // Cost guard (26/09): sparse modes must stay bounded here too.
                // Without a cap, one animated window costs unbounded inference
                // and Balanced/Fast end up SLOWER than Accurate on dense
                // dialogue. Earliest/latest are always kept so edge discovery
                // survives; the 6-frame spread still covers any keepable
                // (>=200ms) caption inside these windows.
                var denseStableTextNoise = sampleResult.Detected
                    && IsDenseAdaptiveVisualNoise(scores);
                var selectedIndexes = denseStableTextNoise && mode.Fps >= 4
                    ? Array.Empty<int>()
                    : CapRefinementIndexes(candidateIndexes, adaptiveWindow.Count - 1, maximum: mode.Fps >= 4 ? 4 : 6);
                refinementFrames = selectedIndexes
                    .Select(index => adaptiveWindow[index]).ToArray();
            }

            for (var offset = 0; offset < refinementFrames.Count; offset += OcrRequestBatcher.WorkerBatchLimit)
            {
                var batch = refinementFrames.Skip(offset).Take(OcrRequestBatcher.WorkerBatchLimit).ToArray();
                var results = await RecognizeBatchAsync(batch, recoverShortBlank: tracker.Active is not null, purpose: "refinement");
                images += batch.Length;
                paddleSinceAudit += batch.Length;
                lastFullOcrTicks = Stopwatch.GetTimestamp();
                for (var index = 0; index < batch.Length; index++)
                    tracker.Observe(batch[index].Timing.SourcePts, batch[index].Timing.PresentationTime, batch[index].Timing.Duration, results[index]);
            }
            // The tracker only listens to real read samples — a skip sample (fake blank) entering here
            // would wrongly close a cue via ObserveEmpty (18/09).
            if (!skippedSample)
                tracker.Observe(sample.Timing.SourcePts, sample.Timing.PresentationTime, sample.Timing.Duration, sampleResult);
            // E.5 SHADOW + council gate:
            // - VAD votes by media time + production outcome -> 2x2 histogram; a silence vote is 1
            //   leg within ShouldCouncilSkip (C abstains — see OcrGateShadow).
            // - ROI static zones: ONLY LEARN when the scan ROI covers the top half of the frame
            //   (LearningEnabledFor); for a bottom subtitle-band ROI every repeated text is dialogue repetition
            //   so disable learning (zones empty, wouldfilter 0) rather than learning a wrong zone.
            try
            {
                var vote = vadSource?.Invoke()?.At(sample.Timing.PresentationTime - streamStart) ?? VadFlag.Suspicious;
                Interlocked.Increment(ref telemetry.GateVadVotes);
                if (vote == VadFlag.NoSub)
                {
                    if (sampleResult.Detected) Interlocked.Increment(ref telemetry.VadNoText);
                    else Interlocked.Increment(ref telemetry.VadNoBlank);
                }
                else
                {
                    if (sampleResult.Detected) Interlocked.Increment(ref telemetry.VadSuspText);
                    else Interlocked.Increment(ref telemetry.VadSuspBlank);
                }
                if (OcrRoiStaticZones.LearningEnabledFor(request.Region))
                    Interlocked.Add(ref telemetry.RoiLinesWouldFilter, roiZones.Observe(sampleResult.Lines));
            }
            catch { }
            // E.4b canary audit: kiem tra mau bo-qua (reuse dormant + council-skip) bang OCR
            // day du tu vong retro. Audit CHI BAO + tu ha cap (tat skip ca scan), khong backfill
            // (E.5+). Audit never kills the lane. Reuse is fully disabled so the real trigger is council.
            if ((reuseEnabled || councilSkipsEnabled) && retroBuffer.Count > 0
                && OcrGateShadow.ShouldTriggerCanary(paddleSinceAudit,
                    Stopwatch.GetElapsedTime(lastFullOcrTicks, Stopwatch.GetTimestamp()).TotalSeconds))
                await RunCanaryAuditAsync();
            // Confirm a new reading/blank with nearby source frames rather than
            // only the next baseline sample, even if the visual probe is quiet.
            if (!tracker.CanCheckpoint)
                pendingVisualFollowups = Math.Max(pendingVisualFollowups, 2);
            // The scene-change anchor keeps the nearest real read sample — a fake blank from a
            // skip becoming an anchor would hide the real text boundary at the next sample (18/09).
            if (!skippedSample)
                previousAdaptiveSample = sampleResult;
            previousVisualAnchor = sample;
            adaptiveWindow.Clear();
            PublishProgress();
        }

        try
        {
            while (await reader.ReadAsync(cancellationToken) is { } jpeg)
            {
                var timing = await timestamps.ReadAsync(cancellationToken);
                var atAbsolute = timing.PresentationTime;
                var atMedia = atAbsolute - streamStart;
                if (atMedia > segment.ScanEnd + 0.001)
                {
                    if (mode.AdaptiveTiming) await ProcessAdaptiveWindowAsync();
                    stoppedAtBoundary = true;
                    break;
                }
                frames++;
                var dur = timing.Duration ?? lastFrameDuration;
                if (dur > 0) lastFrameDuration = dur;
                if (!(dur > 0)) dur = nativeFrameDuration;
                mediaSeconds = Math.Min(segment.ScanEnd, atMedia + dur);
                lastFrameDuration = dur;
                var frame = new OcrBufferedFrame(timing, jpeg);
                if (mode.AdaptiveTiming)
                {
                    adaptiveWindow.Add(frame);
                    var sampleDue = previousAdaptiveSample is null
                        || atMedia + dur / 2 >= nextAdaptiveSampleMedia
                        || adaptiveWindow.Count >= 64
                        || job.IsPauseRequested;
                    if (!sampleDue) continue;
                    await ProcessAdaptiveWindowAsync();
                    do nextAdaptiveSampleMedia += 1 / mode.Fps;
                    while (nextAdaptiveSampleMedia <= atMedia + .000001);
                }
                else
                {
                    var result = await RecognizeSingleAsync(frame, recoverShortBlank: false, allowEnhanced: true, purpose: "exhaustive");
                    images++;
                    tracker.Observe(timing.SourcePts, atAbsolute, timing.Duration, result);
                    PublishProgress();
                }
                if (job.IsPauseRequested && tracker.CanCheckpoint)
                {
                    paused = true;
                    break;
                }
            }
            if (mode.AdaptiveTiming && !paused && !stoppedAtBoundary)
                await ProcessAdaptiveWindowAsync();
            PublishProgress(force: true);
        }
        finally
        {
            if (paused || stoppedAtBoundary) Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { Kill(process); try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
        }
        var stderr = await errorTask;
        if (!paused && !stoppedAtBoundary && process.ExitCode != 0) throw new InvalidOperationException(Compact(stderr));
        if (!paused)
        {
            ValidateLaneCoverage(segment, frames, mediaSeconds, lastFrameDuration);
            tracker.Finish(mediaSeconds);
            var finalCommitted = tracker.Cues.Count > publishedCueCount
                ? tracker.Cues.Skip(publishedCueCount).ToArray()
                : Array.Empty<OcrCue>();
            publishedCueCount = tracker.Cues.Count;
            onProgress(saved with
            {
                MediaSeconds = mediaSeconds,
                Cues = tracker.Cues.ToList(),
                Active = tracker.Active,
                Frames = frames,
                OcrImages = images,
                Completed = false,
            }, finalCommitted, tracker.CanCheckpoint);
            mediaSeconds = segment.CoreEnd;
        }
        // E.5 shadow: so vung tinh lane nay hoc duoc (watermark lap lai).
        Interlocked.Add(ref telemetry.RoiStaticZones, roiZones.Zones.Count);
        return saved with
        {
            MediaSeconds = mediaSeconds,
            Cues = tracker.Cues.ToList(),
            Active = paused ? tracker.Active : null,
            Frames = frames,
            OcrImages = images,
            Completed = !paused,
        };
    }

    private static void ValidateLaneCoverage(OcrScanSegment segment, int frames, double mediaSeconds, double lastFrameDuration)
    {
        // Exit 0 (or showinfo on stderr) does not prove JPEGs reached OCR. Permit
        // one final frame/sample of rounding, but never invent unscanned hours.
        var tolerance = Math.Max(.1, lastFrameDuration);
        if (frames <= 0 || mediaSeconds + tolerance < segment.CoreEnd)
            throw new InvalidDataException(
                $"FFmpeg kết thúc sớm ở lane {segment.Index + 1}: {frames} frame, " +
                $"đã quét tới {FormatClock(mediaSeconds)}, cần tới {FormatClock(segment.CoreEnd)}. " +
                "Chưa quét đủ đoạn video; không thể báo hoàn tất OCR.");
    }

    private static OcrLaneCheckpoint CloneLaneCheckpoint(OcrLaneCheckpoint lane) =>
        lane with { Cues = lane.Cues.ToList() };

    // Raw preview for the fast-pause path: lane-owned cues (exactly Reconcile's bound ownership:
    // a regular lane has Start < CoreEnd, the last lane Start <= CoreEnd, Start >= CoreStart), sliced by the
    // frontier and sorted. Purely in memory, NO FFmpeg/pixel — pixel merging is left to finish/export/
    // open-checkpoint. Tested directly via OcrPausePreviewContract (no real worker/video needed).
    internal static IReadOnlyList<OcrCue> BuildPausedPreview(IReadOnlyList<OcrLaneCheckpoint> lanes, double frontier)
    {
        var owned = new List<OcrCue>();
        foreach (var lane in lanes)
        {
            var isLast = lane.Segment.Index == lanes.Count - 1;
            foreach (var cue in lane.Cues)
            {
                if (cue.Start < lane.Segment.CoreStart
                    || (!isLast && cue.Start >= lane.Segment.CoreEnd)
                    || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                if (cue.Start > frontier + 0.001) continue;
                owned.Add(cue);
            }
        }
        // Raw fallback for pause (no merging): the real merging is in the Async drain
        // of the tail loop below. Keep it pure so pause never hangs.
        return owned.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
    }

    // Pause-preview drains the liveTail's tail loop then takes exactly the live CommittedView
    // being displayed (28/09, the case where pressing Stop made cues scatter). The tail loop only reconciles
    // the last TailWindow cues (pixel-gated, a few FFmpeg checks) so it is bounded to a few seconds —
    // completely unlike the full reconciliation of thousands of frames at finish. Timeout/failure/nothing
    // new -> fall back to the raw preview (fail-open, pause never hangs).
    internal static async Task<IReadOnlyList<OcrCue>> BuildPausedPreviewAsync(
        OcrLiveTailReconciler liveTail,
        IReadOnlyList<OcrLaneCheckpoint> lanes,
        string videoPath,
        IPixelComparer comparer,
        double continuationWindow,
        double frontier,
        CancellationToken cancellationToken)
    {
        try
        {
            liveTail.Start(videoPath, comparer, continuationWindow);
            var deadline = Stopwatch.GetTimestamp() + (long)(15 * Stopwatch.Frequency);
            while (liveTail.AppliedRevision < liveTail.Revision)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetTimestamp() >= deadline)
                    return BuildPausedPreview(lanes, frontier);
                await Task.Delay(100, cancellationToken);
            }
            await liveTail.StopAsync();
            return liveTail.CommittedView.Where(cue => cue.Start <= frontier + 0.001).ToArray();
        }
        catch
        {
            try { await liveTail.StopAsync(); } catch { }
            return BuildPausedPreview(lanes, frontier);
        }
    }

    internal static IReadOnlyList<OcrCue> Reconcile(IReadOnlyList<OcrLaneCheckpoint> lanes, out int merges)
    {
        var owned = new List<(OcrCue Cue, int Lane)>();
        foreach (var lane in lanes)
        {
            var isLast = lane.Segment.Index == lanes.Count - 1;
            foreach (var cue in lane.Cues)
            {
                if (cue.Start < lane.Segment.CoreStart || (!isLast && cue.Start >= lane.Segment.CoreEnd) || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                if (ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text))
                    owned.Add((cue with { Text = text }, lane.Segment.Index));
            }
        }
        owned.Sort((a, b) => a.Cue.Start.CompareTo(b.Cue.Start));
        var output = new List<(OcrCue Cue, int Lane)>();
        merges = 0;
        foreach (var item in owned)
        {
            var cue = item.Cue;
            if (output.Count > 0 && item.Lane != output[^1].Lane
                && cue.Start <= output[^1].Cue.End + 0.25
                && Similarity(output[^1].Cue.Text, cue.Text) >= 0.82)
            {
                output[^1] = (output[^1].Cue with { NeedsReview = true }, output[^1].Lane);
                output.Add((cue with { NeedsReview = true }, item.Lane));
                continue;
            }
            output.Add(item);
        }
        return OcrCueReconciler.MergeTouchingIdentical(output.Select(item => item.Cue));
    }

    // Exact-identical window derived from the native frame rate (all modes): a gap explainable at most by
    // 3 consecutive native frame misses (blink-level) is a candidate; longer is a reappearance.
    // Field evidence: a real 0.667s blank exists (27.35-27.9 with no caption, 6 OCR blanks + naked eye),
    // balanced 4Hz cannot distinguish a real blank from a dropout using cue data => the window must NOT be opened
    // by observation-rate (3/4Hz=0.75s would wrongly merge 2 instances proven to be distinct).
    // Touching (gap ~0) still merges via Epsilon. Probe failure => Epsilon, fail-safe.
    // Shared by the full-scan reconcile and checkpoint/range replay to use the same production window.
    internal static async Task<double> ComputeContinuationWindowAsync(
        Tools.ToolManager tools,
        Processes.ProcessRunner processes,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var windowProbe = await OcrFrameExtractor.ProbeAsync(tools, processes, source, cancellationToken);
            if (windowProbe is { AvgFps: > 0 }) return 3.0 / windowProbe.AvgFps;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return 0.001;
    }

    internal static async Task<(IReadOnlyList<OcrCue> Cues, int Merges)> ReconcileAsync(IReadOnlyList<OcrLaneCheckpoint> lanes, string videoPath, IPixelComparer comparer, CancellationToken cancellationToken, double continuationWindow = 0.001, Action<int, int>? onProgress = null, Action<int, int>? onFrameProgress = null, IReadOnlyList<EditorSpeechSegment>? speechSegments = null)
    {
        var owned = new List<(OcrCue Cue, int Lane)>();
        foreach (var lane in lanes)
        {
            var isLast = lane.Segment.Index == lanes.Count - 1;
            foreach (var cue in lane.Cues)
            {
                if (cue.Start < lane.Segment.CoreStart || (!isLast && cue.Start >= lane.Segment.CoreEnd) || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                if (ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text))
                    owned.Add((cue with { Text = text }, lane.Segment.Index));
            }
        }
        owned.Sort((a, b) => a.Cue.Start.CompareTo(b.Cue.Start));
        var output = new List<(OcrCue Cue, int Lane)>();
        var merges = 0;
        foreach (var item in owned)
        {
            var cue = item.Cue;
            if (output.Count > 0 && item.Lane != output[^1].Lane
                && cue.Start <= output[^1].Cue.End + 0.25
                && Similarity(output[^1].Cue.Text, cue.Text) >= 0.82)
            {
                var prev = output[^1].Cue;
                PixelCheckResult pixel = PixelCheckResult.Unavailable;
                if (prev.LastObservation is not null && cue.FirstObservation is not null)
                {
                    var cmp = await comparer.CheckAsync(videoPath, prev.LastObservation, cue.FirstObservation, cancellationToken);
                    pixel = cmp.Result;
                }
                if (pixel != PixelCheckResult.Same)
                {
                    output[^1] = (output[^1].Cue with { NeedsReview = true }, output[^1].Lane);
                    output.Add((cue with { NeedsReview = true }, item.Lane));
                    continue;
                }
                var previous = output[^1];
                output[^1] = (previous.Cue with
                    {
                        Text = PreferReconciledText(previous.Cue.Text, cue.Text),
                        End = Math.Max(previous.Cue.End, cue.End),
                        Confidence = Math.Max(previous.Cue.Confidence, cue.Confidence),
                        RawSupportCount = previous.Cue.RawSupportCount + cue.RawSupportCount,
                        ConfidentSupportCount = previous.Cue.ConfidentSupportCount + cue.ConfidentSupportCount,
                        FirstObservation = previous.Cue.FirstObservation,
                        LastObservation = cue.LastObservation,
                        NeedsReview = previous.Cue.NeedsReview || cue.NeedsReview,
                    }, previous.Lane);
                merges++;
            }
            else output.Add(item);
        }
        var finalCues = output.Select(item => item.Cue).ToArray();
        if (comparer is ProductionPixelComparer)
        {
            var boundaries = Enumerable.Range(1, Math.Max(0, finalCues.Length - 1))
                .Where(index => OcrCueReconciler.IsChainBoundary(finalCues[index - 1], finalCues[index], continuationWindow))
                .Select(index => (Left: finalCues[index - 1].LastObservation!, Right: finalCues[index].FirstObservation!))
                .ToArray();
            var completedFrames = 0;
            onFrameProgress?.Invoke(0, boundaries.Length);
            await Parallel.ForEachAsync(boundaries, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken,
            }, async (boundary, boundaryToken) =>
            {
                await comparer.CheckAsync(videoPath, boundary.Left, boundary.Right, boundaryToken);
                onFrameProgress?.Invoke(Interlocked.Increment(ref completedFrames), boundaries.Length);
            });
        }
        var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync(finalCues, videoPath, comparer, cancellationToken, continuationWindow, onProgress);
        // Text cleanup (17/09): merge adjacent duplicate cues (OCR flicker splitting one sentence) + delete
        // fragments (1-2 characters adjacent to a longer sentence containing them). Pure text logic, runs for EVERY
        // ReconcileAsync call path (finish/export/checkpoint) so the final list = SRT.
        // Does NOT correct misread text (requires semantic understanding) — keep as-is.
        var cleaned = OcrTextCleanup.Cleanup(merged);
        // Retroactive post-Cleanup pass (18/09): Cleanup removing/merging in the middle makes 2 cues
        // NEWLY adjacent that pixel never verified (gapped duplicates 0.067-0.3s with no
        // review) — judged by exactly the main-pass rule (pixel-gate + review), without
        // touching the pinned micro-gap spec.
        var (reverified, reverifyMerges) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            merged, cleaned.Cues, videoPath, comparer, cancellationToken, continuationWindow);
        // The ear adjudicates wide-gap duplicate pairs (21/09): one speech segment covering the gap continuously =
        // a static caption wrongly split by the tracker → merge; a silence gap = genuine repeated dialogue
        // → keep. Runs at the end of reconcile, before banner guard/missing-sub.
        var echoed = OcrCueReconciler.MergeSpeechCoveredIdentical(reverified, speechSegments);
        return (echoed, merges + cleaned.Merged + reverifyMerges);
    }

    private static string PreferReconciledText(string current, string candidate) =>
        IsStrictSuperset(candidate, current) ? candidate : current;

    // Lane overlap can contain the same rendered caption with an omitted edge
    // glyph in one lane. Pick only a fuller OCR result that actually occurred;
    // never synthesize text from two merely similar readings.
    private static bool IsStrictSuperset(string candidate, string current) =>
        candidate.EnumerateRunes().Count() > current.EnumerateRunes().Count()
        && IsRuneSubsequence(current, candidate);

    private static bool IsRuneSubsequence(string shorter, string longer)
    {
        var shorterRunes = shorter.EnumerateRunes().ToArray();
        var longerRunes = longer.EnumerateRunes().ToArray();
        if (shorterRunes.Length >= longerRunes.Length) return false;
        var matched = 0;
        foreach (var rune in longerRunes)
        {
            if (matched < shorterRunes.Length && shorterRunes[matched] == rune) matched++;
        }
        return matched == shorterRunes.Length;
    }

    private async Task<byte[]> CaptureFrameAsync(
        string path,
        double at,
        OcrRegion region,
        bool enhanced,
        CancellationToken cancellationToken,
        OwnedProcessGroup processes)
    {
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        return await CaptureFrameWithFfmpegAsync(ffmpeg, path, at, region, enhanced, processes, cancellationToken);
    }

    private async Task<(int Width, int Height)?> TryGetSourceDimensionsAsync(string path, CancellationToken cancellationToken)
    {
        // Contract coordinate space: use the coded W/H (ffprobe width/height), and both OCR and the extractor run with -noautorotate
        // so iw/ih are coded (no autorotation from rotation metadata). Every path (OCR crop, bbox provenance,
        // pixel extractor, source crop) uses the same coded space. If a video has rotation=90, both OCR and comparer
        // see 1920x1080 coded, neither sees 1080x1920 rotated.
        try
        {
            var ffprobe = await _tools.EnsureFfprobeAsync(cancellationToken);
            var result = await _processes.RunAsync(ffprobe,
                ["-hide_banner", "-loglevel", "error", "-select_streams", "v:0",
                 "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", Path.GetFullPath(path.Trim())],
                cancellationToken);
            var output = (result.StandardOutput ?? string.Empty).Trim();
            var parts = output.Split(['x', ',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
                return (w, h);
            return null;
        }
        catch { return null; }
    }

    private async Task<byte[]> CaptureFrameWithFfmpegAsync(
        string ffmpeg,
        string path,
        double at,
        OcrRegion region,
        bool enhanced,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        region = OcrCheckpointStore.NormalizeRegion(region);
        var dims = await TryGetSourceDimensionsAsync(path, cancellationToken);
        string filter;
        if (dims is { } d && ProductionPixelComparer.TryPlanOcrFrame(d.Width, d.Height, region) is { } g)
        {
            filter = ProductionPixelComparer.BuildOcrFilter(g, enhanced);
        }
        else
        {
            // Probe failure: fall back to the old filter so the scan is not blocked. Pixel compare will be Unavailable because the geometry is uncertain.
            filter = string.Format(CultureInfo.InvariantCulture,
                "crop=iw*{0}:ih*{1}:iw*{2}:ih*{3},scale=1280:320:force_original_aspect_ratio=decrease,pad=1280:320:(ow-iw)/2:(oh-ih)/2:black{4}",
                region.Width, region.Height, region.X, region.Y, enhanced ? ",eq=contrast=1.25:brightness=0.03,unsharp=5:5:0.8" : string.Empty);
        }
        return await _processes.CaptureBytesAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-noautorotate", "-ss", Math.Max(0, at).ToString("0.000", CultureInfo.InvariantCulture),
            "-i", Path.GetFullPath(path.Trim()), "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
            "-vf", filter, "-q:v", "3", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1",
        ], cancellationToken, processes);
    }

    private async Task<bool> ProbeNvdecAsync(
        string ffmpeg,
        string source,
        OcrRegion region,
        OcrScanMode mode,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        var (nvdecProbe, _, _) = await ProbeVideoAsync(source, cancellationToken);
        var probe = nvdecProbe;
        var args = BuildLaneArguments(source, region, mode, 0, Math.Min(2, mode.Fps > 0 ? 2 : 1), nvdec: true, probe).ToList();
        var frameIndex = args.IndexOf("-f");
        if (frameIndex >= 0)
        {
            args.RemoveRange(frameIndex, args.Count - frameIndex);
            args.AddRange(["-frames:v", "1", "-f", "null", "-"]);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var result = await _processes.RunAsync(ffmpeg, args, timeout.Token, owner: processes);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static IReadOnlyList<string> BuildLaneArguments(string source, OcrRegion region, OcrScanMode mode, double start, double end, bool nvdec, VideoProbe? probe = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-nostdin", "-noautorotate" };
        if (nvdec) args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", "0"]);
        // Decode hardening (17/10): a lane dying mid-stream due to a corrupt frame/driver hiccup
        // (ExitCode != 0) used to make the whole lane drop permanently to software decoding (4/8 lanes,
        // 7h39m video, speed dropped to 0.08x realtime). Flags after now swallow corrupt frames instead of
        /// dying streams, for BOTH the NVDEC and software branches; the n-sequence guard for showinfo
        // (FrameTimestampReader) still blocks every JPEG↔PTS desync so data integrity is unchanged.
        args.AddRange(["-err_detect", "ignore_err", "-ec", "3", "-fflags", "+genpts+discardcorrupt"]);
        args.AddRange(["-copyts", "-seek_timestamp", "1"]);
        if (start > 0 || (probe is not null && probe.StartTimeSeconds != 0))
            args.AddRange(["-ss", start.ToString("0.#########", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", source]);
        // start/end are both absolute (media + streamStart), the output stays absolute thanks to -copyts so -to absolute is correct.
        // Do not use -t duration: with -copyts + start>0, -t stops immediately (proven experimentally: 3 frames then empty).
        if (end > start) args.AddRange(["-to", end.ToString("0.#########", CultureInfo.InvariantCulture)]);
        string crop;
        if (probe is { Width: > 0, Height: > 0 }
            && ProductionPixelComparer.TryPlanOcrFrame(probe.Width, probe.Height, region) is { } g)
        {
            var baseFilter = $"crop={g.SourceCropW}:{g.SourceCropH}:{g.SourceCropX}:{g.SourceCropY},scale={g.ContentW}:{g.ContentH}:flags=fast_bilinear,pad={g.CanvasW}:{g.CanvasH}:{g.ContentLeft}:{g.ContentTop}:black";
            crop = baseFilter;
        }
        else
        {
            crop = string.Format(CultureInfo.InvariantCulture,
                "crop=iw*{0}:ih*{1}:iw*{2}:ih*{3},scale=1280:320:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=1280:320:(ow-iw)/2:(oh-ih)/2:black",
                region.Width, region.Height, region.X, region.Y);
        }
        // Do not use an fps filter before showinfo: fps rewrites PTS (drops/duplicates), provenance would be wrong.
        // 4Hz sampling is done in C# by PTS (adaptiveWindow/nextAdaptiveSample), preserving the source PTS.
        // Every mode currently has AdaptiveTiming:true, so it always takes this PTS-preserving branch.
        string filter;
        if (probe is { TimeBaseNum: > 0, TimeBaseDen: > 0 })
        {
            var tb = $"{probe.TimeBaseNum}/{probe.TimeBaseDen}";
            filter = nvdec
                ? $"hwdownload,format=nv12|p010le|p016le,settb=expr={tb},{crop},showinfo=checksum=0"
                : $"settb=expr={tb},{crop},showinfo=checksum=0";
        }
        else
        {
            filter = nvdec
                ? $"hwdownload,format=nv12|p010le|p016le,{crop},showinfo=checksum=0"
                : $"{crop},showinfo=checksum=0";
        }
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", filter, "-fps_mode", "passthrough", "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"]);
        return args;
    }

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        var leftRunes = left.EnumerateRunes().Select(x => x.Value).ToArray();
        var rightRunes = right.EnumerateRunes().Select(x => x.Value).ToArray();
        if (leftRunes.Length == 0 || rightRunes.Length == 0) return 0;

        var previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        for (var i = 1; i <= leftRunes.Length; i++)
        {
            var current = new int[rightRunes.Length + 1];
            current[0] = i;
            for (var j = 1; j <= rightRunes.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (leftRunes[i - 1] == rightRunes[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(leftRunes.Length, rightRunes.Length);
    }

    private static bool PreferRecognition(OcrResult candidate, OcrResult current)
    {
        if (!ChineseSubtitleNormalizer.TryNormalize(candidate.Text, out var candidateText)) return false;
        if (!ChineseSubtitleNormalizer.TryNormalize(current.Text, out var currentText)) return true;
        var candidateRunes = candidateText.EnumerateRunes().Count();
        var currentRunes = currentText.EnumerateRunes().Count();
        if (candidateRunes < currentRunes) return false;
        return candidate.Confidence > current.Confidence + .01
            || candidateRunes > currentRunes && candidate.Confidence >= current.Confidence - .08;
    }

    private static bool NeedsEnhancedRecognition(OcrResult result, double threshold) =>
        result.Ok && result.Detected && result.Confidence < threshold;

    private static bool NeedsAdaptiveRefinement(OcrResult previous, OcrResult current)
    {
        var previousText = string.Empty;
        var currentText = string.Empty;
        var previousDetected = previous.Ok && previous.Detected
            && ChineseSubtitleNormalizer.TryNormalize(previous.Text, out previousText);
        var currentDetected = current.Ok && current.Detected
            && ChineseSubtitleNormalizer.TryNormalize(current.Text, out currentText);
        if (previousDetected != currentDetected) return true;
        if (!previousDetected) return false;
        // Similar strings can be different captions (including one substituted
        // glyph). Refinement gathers evidence; only the tracker decides continuity.
        return !string.Equals(previousText, currentText, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<int> SelectAdaptiveRefinementFrames(
        IReadOnlyList<double> scores, ref int pendingFollowups) =>
        SelectRefinementFramesCore(scores, ref pendingFollowups, AdaptiveVisualChangeThreshold);

    // Transition tier: a sampled text change is independent evidence that a
    // real boundary sits inside this window, so the visual confirmation bar
    // drops (trace-proven one-rune H.264 boundaries score ~0.019 while grain
    // background stays at ~0.001-0.007). Steady windows keep the strict bar.
    internal static IReadOnlyList<int> SelectTransitionRefinementFrames(
        IReadOnlyList<double> scores, ref int pendingFollowups) =>
        SelectRefinementFramesCore(scores, ref pendingFollowups, AdaptiveTransitionChangeThreshold);

    private static IReadOnlyList<int> SelectRefinementFramesCore(
        IReadOnlyList<double> scores, ref int pendingFollowups, double changeThreshold)
    {
        var candidates = new SortedSet<int>();
        var sampleIndex = scores.Count - 1;
        for (var index = 0; index < Math.Min(pendingFollowups, sampleIndex); index++)
            candidates.Add(index);
        // The last frame is already recognized as the baseline sample and also
        // counts as a followup. Never submit or observe that frame twice.
        pendingFollowups = Math.Max(0, pendingFollowups - scores.Count);
        for (var index = 0; index < scores.Count; index++)
        {
            if (!double.IsFinite(scores[index]) || scores[index] < changeThreshold) continue;
            // Late evidence before disappearance is essential for a short rune:
            // three onset frames alone cannot establish its temporal footprint.
            for (var nearby = Math.Max(0, index - 1); nearby <= index + 2 && nearby < sampleIndex; nearby++)
                candidates.Add(nearby);
            pendingFollowups = Math.Max(pendingFollowups, Math.Max(0, index + 2 - sampleIndex));
        }
        return candidates.ToArray();
    }

    private static bool IsAdaptiveVisualChange(double score) =>
        double.IsFinite(score) && score >= AdaptiveVisualChangeThreshold;

    internal static bool IsDenseAdaptiveVisualNoise(IReadOnlyList<double> scores)
    {
        if (scores.Count < 4) return false;
        var changed = scores.Count(IsAdaptiveVisualChange);
        return changed >= Math.Max(3, (scores.Count + 1) / 2);
    }

    // A sampled text change with no visual boundary is usually recognizer
    // flicker, not a new caption. Confirm it with both ends of the interval
    // instead of the whole
    // window; the tracker still receives ordered evidence plus the sample,
    // and any keepable (>=200ms) caption inside a normal sampling window is
    // still observed. Never returns the baseline sample index itself.
    internal static IReadOnlyList<int> BoundedTransitionConfirmationFrames(int windowLength, int maximum = 5)
    {
        if (windowLength <= 0 || maximum <= 0) return [];
        if (maximum == 1) return [0];
        var indexes = new SortedSet<int>();
        for (var slot = 0; slot < maximum; slot++)
            indexes.Add((slot * (windowLength - 1)) / (maximum - 1));
        return indexes.Where(index => index >= 0 && index < windowLength).ToArray();
    }

    // Dense animation can nominate dozens of visual boundaries in one window.
    // Keep the earliest, the latest and an even subsample so one noisy window
    // cannot become every-frame inference on its own.
    internal static IReadOnlyList<int> CapRefinementIndexes(
        IReadOnlyList<int> candidates, int windowLength, int maximum = 8)
    {
        var ordered = candidates
            .Where(index => index >= 0 && index < windowLength)
            .Distinct().OrderBy(index => index).ToArray();
        if (ordered.Length <= maximum) return ordered;
        var capped = new SortedSet<int> { ordered[0], ordered[^1] };
        for (var slot = 1; slot < maximum - 1; slot++)
            capped.Add(ordered[(slot * (ordered.Length - 1)) / (maximum - 1)]);
        return capped.ToArray();
    }

    private void LogScanTelemetry(
        AppJob job,
        OcrScanTelemetryCounters telemetry,
        OcrInferenceTelemetry inferenceBefore,
        int lanes,
        int workers,
        int nativeFrames,
        int ocrImages,
        double mediaSeconds,
        double elapsedSeconds,
        int needReviewCues,
        double baseMediaSeconds)
    {
        var inference = _ocr.GetInferenceTelemetry();
        // The pause path stops the pool (fresh queue instance) before this
        // summary runs, so clamp post-rebuild deltas instead of going negative.
        long Delta(long after, long before) => Math.Max(0, after - before);
        var ocrCalls = Delta(inference.OcrBatchCalls, inferenceBefore.OcrBatchCalls);
        var ocrRequested = Delta(inference.OcrBatchImages, inferenceBefore.OcrBatchImages);
        var probeCalls = Delta(inference.ProbeCalls, inferenceBefore.ProbeCalls);
        var probeImages = Delta(inference.ProbeImages, inferenceBefore.ProbeImages);
        var replacements = Delta(inference.WorkerReplacements, inferenceBefore.WorkerReplacements);
        var queueExecuted = Delta(inference.QueueExecuted, inferenceBefore.QueueExecuted);
        int unique = telemetry.UniqueOcrPts.Count;
        var process = Process.GetCurrentProcess();
        job.Log(
            "OCR telemetry "
            + $"lanes={lanes} workers={workers} "
            + $"native_frames={nativeFrames} ocr_requested={ocrRequested} ocr_completed={telemetry.OcrCompletedImages} unique_pts={unique} ocr_blank={telemetry.OcrBlankReadings} "
            + $"ocr_ratio={(nativeFrames > 0 ? ocrRequested / (double)nativeFrames : 0):0.000} "
            + $"window={telemetry.WindowBatchImages} refinement={telemetry.RefinementImages} sample={telemetry.SampleImages} exhaustive={telemetry.ExhaustiveImages} "
            + $"batch_calls={ocrCalls} avg_batch={(ocrCalls > 0 ? ocrRequested / (double)ocrCalls : 0):0.00} "
            + $"probe_calls={probeCalls} probe_images={probeImages} probe_change={telemetry.ProbeChangeFrames} probe_same={telemetry.ProbeSameFrames} "
            + $"lane_restarts={telemetry.LaneRestarts} nvdec_retries={telemetry.NvdecRetries} nvdec_fail={telemetry.NvdecFailures} soft_fallback={telemetry.SoftwareFallbacks} worker_replacements={replacements} worker_recycles={Delta(inference.PlannedRecycles, inferenceBefore.PlannedRecycles)} "
            + $"queue_executed={queueExecuted} queue_peak={inference.QueuePeakDepth} queue_avgdepth={inference.QueueAvgDepth:0.00} queue_wait_ms={Delta(inference.QueueTotalWaitMs, inferenceBefore.QueueTotalWaitMs)} "
            + $"gates_vad={telemetry.GateVadVotes} gates_band={telemetry.GateBandDiffs} gates_text={telemetry.GateTextPresence} gates_roi={telemetry.GateRoiDecisions} council_skip={telemetry.CouncilSkips} council_scan={telemetry.CouncilScans} canary_hit={telemetry.CanaryHits} canary_vad={telemetry.CanaryVadAttributed} retro_ocr={telemetry.RetroOcrCalls} "
            + $"shadow_b={OcrGateShadow.FormatHistogram(telemetry.ShadowBandText, telemetry.ShadowBandBlank)} shadow_c={OcrGateShadow.FormatHistogram(telemetry.ShadowEdgeText, telemetry.ShadowEdgeBlank)} shadow_obs={telemetry.ShadowObservations} shadow_ms={telemetry.ShadowMsTotal} "
            + $"cues_needreview={needReviewCues} "
            + $"flags_free={telemetry.FlagCalls[0]} flags_recover={telemetry.FlagCalls[1]} flags_active={telemetry.FlagCalls[2]} flags_both={telemetry.FlagCalls[3]} "
            + $"paddle_det_ms={telemetry.PaddleDetMs:0} paddle_rec_ms={telemetry.PaddleRecMs:0} det_boxes={telemetry.PaddleDetBoxes} rec_crops={telemetry.PaddleRecCrops} cache_hits={telemetry.PaddleCacheHits} "
            + $"probe_ms={telemetry.ProbeMsTotal} req_ms={telemetry.RequestMsTotal} req_max_ms={telemetry.MaxRequestMs} req_over30s={telemetry.Over30sRequests} "
            + $"sidecar_probe={telemetry.SidecarProbeHits} sidecar_vad={telemetry.SidecarVadHits} sidecar_pxv={telemetry.SidecarPxvHits} "
            + $"vad_no_text={telemetry.VadNoText} vad_no_blank={telemetry.VadNoBlank} vad_susp_text={telemetry.VadSuspText} vad_susp_blank={telemetry.VadSuspBlank} roi_zones={telemetry.RoiStaticZones} roi_wouldfilter={telemetry.RoiLinesWouldFilter} lum_frames={telemetry.LumFrames} lum_lines={telemetry.LumLines} lum_woulddrop={telemetry.LumWouldDrop} lum_decodefails={telemetry.LumDecodeFails} chroma_frames={telemetry.ChromaFrames} chroma_lines={telemetry.ChromaLines} chroma_dropped={telemetry.ChromaDropped} chroma_decodefails={telemetry.ChromaDecodeFails} judge_enq={telemetry.JudgeEnqueued} judge_skip={telemetry.JudgeSkipped} "
            + $"elapsed_s={elapsedSeconds:0.0} realtime={(elapsedSeconds > 0 ? Math.Max(0, mediaSeconds - baseMediaSeconds) / elapsedSeconds : 0):0.000} "
            + $"ckpt_saves={telemetry.CheckpointSaves} ckpt_ms={telemetry.CheckpointSaveMs} "
            + $"app_ram_mb={process.WorkingSet64 / 1048576:0} gc_mb={GC.GetTotalMemory(false) / 1048576:0}");
    }

    private static void RecordOcrObservation(
        OcrScanTelemetryCounters telemetry, long sourcePts, OcrResult result, string purpose)
    {
        switch (purpose)
        {
            case "window": Interlocked.Increment(ref telemetry.WindowBatchImages); break;
            case "refinement": Interlocked.Increment(ref telemetry.RefinementImages); break;
            case "exhaustive": Interlocked.Increment(ref telemetry.ExhaustiveImages); break;
            default: Interlocked.Increment(ref telemetry.SampleImages); break;
        }
        Interlocked.Increment(ref telemetry.OcrCompletedImages);
        if (!result.Detected) Interlocked.Increment(ref telemetry.OcrBlankReadings);
        telemetry.UniqueOcrPts.TryAdd(sourcePts, 0);
    }

    private static OcrResult FilterOffBaselineOverlayLines(OcrResult result, OcrRegion region)
    {
        if (!result.Ok) return result;
        if (result.Lines.Count == 1)
        {
            var only = result.Lines[0];
            if (IsUpperOffBaselineOverlay(only, region) || IsStandaloneRightEdgeBranding(only, region))
                return result with { Detected = false, Text = string.Empty, Confidence = 0, Lines = [] };
        }
        if (result.Lines.Count < 2) return result;
        var hasCenteredSubtitle = HasCenteredSubtitleLine(result);
        var dominant = FindDominantSubtitleLine(result);
        var baseline = FindSubtitleBaseline(result);
        if (baseline is null && dominant is null) return result;
        var companionBaseline = baseline ?? dominant;

        var baselineHeight = baseline is null ? 1 : Math.Max(1, Math.Abs(baseline.Box[3] - baseline.Box[1]));
        var baselineCenter = baseline is null ? 0 : baseline.Box[1] + baseline.Box[3];
        // Stacked background text columns (notes/signs/boards falling into the ROI, the 21/09 letter
        // "休书" case): real dialogue is at most 1-2 secondary lines above the baseline, background text
        // stacks in full columns. With a CENTRE baseline on a 1280 frame + ≥4 lines sitting compactly ABOVE
        // it, the entire region above is background text and all is removed (including lines close to
        // the baseline that the single-line farAbove rule cannot reach). Only applies to bottom ROI
        // (respecting manual top ROI); missing a centre baseline -> passthrough
        // (fail-open: ink-black cases without a baseline are already handled by the luminance gate).
        var stackedSceneDrop = new HashSet<OcrLine>(ReferenceEqualityComparer.Instance);
        if (region.Y >= .5 && baseline is not null
            && Math.Min(baseline.Box[0], baseline.Box[2]) < 640
            && Math.Max(baseline.Box[0], baseline.Box[2]) > 640)
        {
            var baselineTop = Math.Min(baseline.Box[1], baseline.Box[3]);
            foreach (var stacked in result.Lines)
            {
                if (ReferenceEquals(stacked, baseline) || stacked.Box.Length < 4) continue;
                if (Math.Max(stacked.Box[1], stacked.Box[3]) < baselineTop)
                    stackedSceneDrop.Add(stacked);
            }
            if (stackedSceneDrop.Count < 4) stackedSceneDrop.Clear();
        }
        var retained = result.Lines.Where(line =>
        {
            if (stackedSceneDrop.Contains(line)) return false;
            if (line.Box.Length < 4) return true;
            if (!hasCenteredSubtitle && IsStandaloneRightEdgeBranding(line, region)) return false;
            if (companionBaseline is not null && IsRightEdgeBrandingCompanion(line, companionBaseline, region)) return false;
            if (baseline is null) return true;
            if (ReferenceEquals(line, baseline)) return true;
            var width = Math.Abs(line.Box[2] - line.Box[0]);
            var height = Math.Abs(line.Box[3] - line.Box[1]);
            var vertical = height > width * 1.5;
            var farAbove = baselineCenter - (line.Box[1] + line.Box[3]) > baselineHeight * 3;
            // Separate scene glyphs beside a centered caption are not inline
            // subtitle fragments when both their size and baseline disagree.
            var sceneCompanion = region.Y >= .5
                && baseline.Box[0] < 640 && baseline.Box[2] > 640
                && (line.Box[2] <= baseline.Box[0] || line.Box[0] >= baseline.Box[2])
                && (height > baselineHeight * 1.35 || height < baselineHeight * .75)
                && Math.Abs(line.Box[3] - baseline.Box[3]) > Math.Min(height, baselineHeight) * .25
                && ChineseSubtitleNormalizer.TryNormalize(line.Text, out _);
            return !(vertical || farAbove || sceneCompanion || IsTinyNonChineseCompanion(line, baseline));
        }).ToArray();
        if (retained.Length == result.Lines.Count) return result;
        if (retained.Length == 0)
            return result with { Detected = false, Text = string.Empty, Confidence = 0, Lines = [] };
        return result with
        {
            Text = string.Join("\n", retained.Select(line => line.Text)),
            Confidence = retained.Average(line => line.Confidence),
            Lines = retained,
        };
    }

    // A genuine caption occupies the horizontal center of the 1280-wide OCR
    // canvas. Lower-right branding never spans the center, so its presence is
    // the signal that a corner detection is a companion, not a standalone cue.
    private static bool HasCenteredSubtitleLine(OcrResult result) => result.Lines.Any(line =>
        line.Box.Length >= 4
        && Math.Min(line.Box[0], line.Box[2]) < 560
        && Math.Max(line.Box[0], line.Box[2]) > 720
        && ChineseSubtitleNormalizer.TryNormalize(line.Text, out _));

    private static bool HasTinyNonChineseCompanion(OcrResult result)
    {
        if (!result.Ok || result.Lines.Count < 2) return false;
        var baseline = FindSubtitleBaseline(result);
        return baseline is not null && result.Lines.Any(line =>
            !ReferenceEquals(line, baseline) && IsTinyNonChineseCompanion(line, baseline));
    }

    // A genuine caption occupies the horizontal center of the 1280-wide OCR
    // canvas, so a detection that starts at the right edge is never the
    // baseline. Excluding it here keeps the companion rule anchored to real
    // dialogue even when the branding reads with high confidence.
    private static OcrLine? FindSubtitleBaseline(OcrResult result) => result.Lines
        .Where(line => line.Box.Length >= 4
            && Math.Min(line.Box[0], line.Box[2]) < 960
            && line.Confidence >= .50
            && ChineseSubtitleNormalizer.TryNormalize(line.Text, out _)
            && Math.Abs(line.Box[2] - line.Box[0]) >= Math.Abs(line.Box[3] - line.Box[1]) * .8)
        // Prefer a centered horizontal caption over lower scene lettering.
        .OrderByDescending(line => line.Box[0] < 640 && line.Box[2] > 640
            && Math.Abs(line.Box[2] - line.Box[0]) >= Math.Abs(line.Box[3] - line.Box[1]) * 2)
        .ThenByDescending(line => line.Box[3])
        .ThenByDescending(line => line.Confidence)
        .FirstOrDefault();

    private static OcrLine? FindDominantSubtitleLine(OcrResult result) => result.Lines
        .Where(line => line.Box.Length >= 4
            && ChineseSubtitleNormalizer.TryNormalize(line.Text, out _)
            && Math.Abs(line.Box[2] - line.Box[0]) >= Math.Abs(line.Box[3] - line.Box[1]) * .8)
        .OrderByDescending(line => (long)Math.Abs(line.Box[2] - line.Box[0]) * Math.Abs(line.Box[3] - line.Box[1]))
        .ThenByDescending(line => line.Confidence)
        .FirstOrDefault();

    private static bool IsRightEdgeBrandingCompanion(OcrLine line, OcrLine baseline, OcrRegion region)
    {
        if (region.Y < .5 || ReferenceEquals(line, baseline) || line.Box.Length < 4 || baseline.Box.Length < 4)
            return false;
        if (!HasRightEdgeBrandingToken(line.Text)) return false;

        // OCR frames use a 1280-wide canvas. A centered subtitle can coexist with
        // a much shorter changing quality/channel mark in the lower-right corner.
        // Keep same-height inline fragments so legitimate text containing digits survives.
        if (baseline.Box[0] >= 640 || baseline.Box[2] <= 640 || line.Box[0] < 960) return false;
        var height = Math.Max(1, Math.Abs(line.Box[3] - line.Box[1]));
        var baselineHeight = Math.Max(1, Math.Abs(baseline.Box[3] - baseline.Box[1]));
        var baselineOffset = Math.Abs(line.Box[3] - baseline.Box[3]);
        if ((long)height * 4 > (long)baselineHeight * 3
            && baselineOffset <= Math.Min(height, baselineHeight) / 4) return false;
        var horizontallySeparated = line.Box[0] - baseline.Box[2] >= Math.Max(8, baselineHeight / 8);
        var belowBaseline = line.Box[1] >= baseline.Box[3] - Math.Max(2, baselineHeight / 12);
        return horizontallySeparated || belowBaseline;
    }

    // A lone detection in the lower-right corner is the same quality/channel
    // branding that the companion rule strips beside real dialogue. Frames
    // without a subtitle must not turn it into a cue of its own, otherwise the
    // changing OCR reading of the mark fragments into repeated SRT entries.
    private static bool IsStandaloneRightEdgeBranding(OcrLine line, OcrRegion region)
    {
        if (region.Y < .5 || line.Box.Length < 4) return false;
        if (!HasRightEdgeBrandingToken(line.Text)) return false;
        var left = Math.Min(line.Box[0], line.Box[2]);
        var right = Math.Max(line.Box[0], line.Box[2]);
        var bottom = Math.Max(line.Box[1], line.Box[3]);
        if (left < 960 || right < 1152) return false;
        return bottom >= 240;
    }

    private static bool HasRightEdgeBrandingToken(string text)
    {
        var asciiDigits = text.Count(character => character is >= '0' and <= '9');
        return asciiDigits >= 4
            || text.Contains('版') && text.Any(character => char.IsAsciiLetterOrDigit(character));
    }

    private static bool IsTinyNonChineseCompanion(OcrLine line, OcrLine baseline)
    {
        if (line.Box.Length < 4 || ReferenceEquals(line, baseline)
            || ChineseSubtitleNormalizer.TryNormalize(line.Text, out _)) return false;
        var width = Math.Max(1, Math.Abs(line.Box[2] - line.Box[0]));
        var height = Math.Max(1, Math.Abs(line.Box[3] - line.Box[1]));
        var baselineWidth = Math.Max(1, Math.Abs(baseline.Box[2] - baseline.Box[0]));
        var baselineHeight = Math.Max(1, Math.Abs(baseline.Box[3] - baseline.Box[1]));
        return (long)width * height * 8 < (long)baselineWidth * baselineHeight
            && height * 2 < baselineHeight;
    }

    private static bool IsUpperOffBaselineOverlay(OcrLine line, OcrRegion region)
    {
        // OCR frames are normalized to a 1280x320 canvas. For the default
        // lower-screen subtitle ROI, a one-glyph detection in the upper band
        // or a left-aligned scene/branding overlay there is not subtitle text.
        // The latter was observed as a stylized scene title which Paddle read
        // differently on adjacent frames, producing a run of false cues.
        // Do not apply this rule to a user-selected upper ROI, where either
        // shape can be genuine caption content.
        if (region.Y < .5 || line.Box.Length < 4 || line.Box[1] + line.Box[3] >= 170) return false;
        return line.Text.EnumerateRunes().Count() == 1 || line.Box[0] <= 80;
    }

    private static string FormatClock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    private static string Compact(string value)
    {
        // Loi that cua FFmpeg/tool nam o CUOI stderr (dong loi cuoi cung).
        // Giu duoi, bo dau (header Input #0 dai vo ich). Truoc day giu dau nen
        // log chi thay header, mat loi that (vu lane 8 p02).
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 300 ? "…" + text[^300..] : text;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    private sealed record OcrTopologyProbe(double Throughput);
    private sealed class OcrRecognitionException(string message, Exception? inner = null) : Exception(message, inner);
    private sealed class OcrBufferedFrame(FrameTiming timing, byte[] jpeg)
    {
        private string? _base64;

        public FrameTiming Timing { get; } = timing;
        public byte[] Jpeg { get; } = jpeg;
        public string Base64 => _base64 ??= Convert.ToBase64String(Jpeg);
    }
    private readonly record struct FrameTiming(long SourcePts, double PresentationTime, double? Duration);

    // Diagnostic-only per-scan counters shared by lane closures. No
    // scheduling, filtering or reconciliation behavior depends on these.
    private sealed class OcrScanTelemetryCounters
    {
        public readonly object Gate = new();
        // PERF (end-of-scan lag 15/9): 8 lanes contending on 1 lock for every OCR image (466k times/scan)
        // just to count unique PTS for the end-of-session telemetry. ConcurrentDictionary gives lock-free Add/Count
        // with the same semantics (each SourcePts counted once), removing contention
        // growing with scan length. The gate is kept for PaddleDetMs/RecMs (double+=).
        public readonly ConcurrentDictionary<long, byte> UniqueOcrPts = new();
        public long BatchCalls;
        public long SingleCalls;
        public long WindowBatchImages;
        public long RefinementImages;
        public long SampleImages;
        public long ExhaustiveImages;
        public long OcrCompletedImages;
        public long OcrBlankReadings;
        public long ProbeCalls;
        public long ProbeImages;
        public long ProbeChangeFrames;
        public long ProbeSameFrames;
        public long LaneRestarts;
        // NVDEC resilience (17/10): count the retry ladder within the lane (retry/fail/fallback)
        // so NVDEC health is visible right on the telemetry line instead of reading logs line by line.
        public long NvdecRetries;
        public long NvdecFailures;
        public long SoftwareFallbacks;
        // Checkpoint save cost (diagnostic only): number of saves + total ms. Saving every 30s
        // serializes all cues (11MB at the 24:27 pause) + copies .bak so it grows slower toward the end;
        // logged to detect when saving becomes a new source of lag.
        public long CheckpointSaves;
        public long CheckpointSaveMs;
        // E.2 dashboard skeleton (gates chua code: giu 0, co so de E.3/E.4 dien).
        // Quy uoc: skip chi dem khi HOI DONG nhat tri (khong gate nao tu dem).
        public long GateVadVotes;
        public long GateBandDiffs;
        public long GateTextPresence;
        public long GateRoiDecisions;
        public long CouncilSkips;
        public long CouncilScans;
        public long CanaryHits;
        public long RetroOcrCalls;
        // E.3 shadow histogram: diem B/C theo ngan x outcome production.
        // CHI GHI CHEP, khong dieu tiet hanh vi.
        public readonly long[] ShadowBandText = new long[OcrGateShadow.Buckets];
        public readonly long[] ShadowBandBlank = new long[OcrGateShadow.Buckets];
        public readonly long[] ShadowEdgeText = new long[OcrGateShadow.Buckets];
        public readonly long[] ShadowEdgeBlank = new long[OcrGateShadow.Buckets];
        public long ShadowObservations;
        public long ShadowMsTotal;
        // D.1 flag-histogram: dem combo co moi paddle_call (khong gioi han spans).
        // 0=free, 1=recover, 2=active, 3=both. D.1 det/rec split: cong don timing.
        public readonly long[] FlagCalls = new long[4];
        public double PaddleDetMs;
        public double PaddleRecMs;
        public long PaddleDetBoxes;
        public long PaddleRecCrops;
        public long PaddleCacheHits;
        // Stage timings (P1 diagnostics, purely cumulative, does not gate behaviour):
        // - ProbeMsTotal: round-trip per probe (queue + IPC + OpenCV).
        // - RequestMsTotal/MaxRequestMs/Over30sRequests: round-trip per OCR request —
        //   latency distribution to gate lowering the 90s timeout (only lower when max is well << 90s).
        public long ProbeMsTotal;
        public long RequestMsTotal;
        public long MaxRequestMs;
        public long Over30sRequests;
        // Sidecar hits (diagnostic): probe/VAD/pxv reused from the cache file instead of recomputed.
        public long SidecarProbeHits;
        public long SidecarVadHits;
        public long SidecarPxvHits;
        // E.5 shadow: VAD vote x outcome (2x2) + ROI static zones.
        // CHI GHI CHEP: co VAD khong bao gio du mot minh de skip (luat hoi dong).
        public long VadNoText;
        public long VadNoBlank;
        public long VadSuspText;
        public long VadSuspBlank;
        // Canary responsibility: a hit while the audit sample is silent (VAD NoSub) means the ear lied.
        public long CanaryVadAttributed;
        public long RoiStaticZones;
        public long RoiLinesWouldFilter;
        // Candidate 2 luminance SHADOW: measure every line, count would-drops, never drop a line.
        // CHI GHI CHEP (EnforceDefault=false).
        public long LumFrames;
        public long LumLines;
        public long LumWouldDrop;
        public long LumDecodeFails;
        // Text colour gate 18/09 (locked by user: subs are always white): direct enforcement,
        // counting lines measured/dropped/broken-decode.
        public long ChromaFrames;
        public long ChromaLines;
        public long ChromaDropped;
        public long ChromaDecodeFails;
        // Background VLM judging during the scan: cues entering the queue / skipped (duplicate/full/dispose).
        // Detailed verdicts (keep/drop/unclear) live in the journal + the end-of-scan summary log.
        public long JudgeEnqueued;
        public long JudgeSkipped;

        // Interlocked(ref field) is not recognized as an assignment by the
        // compiler; touch every counter once so warnings-as-errors stays green.
        public void Touch()
        {
            BatchCalls += 0; SingleCalls += 0; WindowBatchImages += 0;
            RefinementImages += 0; SampleImages += 0; ExhaustiveImages += 0;
            OcrCompletedImages += 0; OcrBlankReadings += 0; ProbeCalls += 0;
            ProbeImages += 0; ProbeChangeFrames += 0; ProbeSameFrames += 0;
            LaneRestarts += 0; CheckpointSaves += 0; CheckpointSaveMs += 0;
            GateVadVotes += 0; GateBandDiffs += 0; GateTextPresence += 0;
            GateRoiDecisions += 0; CouncilSkips += 0; CouncilScans += 0;
            CanaryHits += 0; RetroOcrCalls += 0;
            PaddleDetMs += 0; PaddleRecMs += 0;
            PaddleDetBoxes += 0; PaddleRecCrops += 0; PaddleCacheHits += 0;
            ProbeMsTotal += 0; RequestMsTotal += 0; MaxRequestMs += 0; Over30sRequests += 0;
            SidecarProbeHits += 0; SidecarVadHits += 0; SidecarPxvHits += 0;
            VadNoText += 0; VadNoBlank += 0; VadSuspText += 0; VadSuspBlank += 0; CanaryVadAttributed += 0;
            RoiStaticZones += 0; RoiLinesWouldFilter += 0;
            LumFrames += 0; LumLines += 0; LumWouldDrop += 0; LumDecodeFails += 0;
            ChromaFrames += 0; ChromaLines += 0; ChromaDropped += 0; ChromaDecodeFails += 0;
            JudgeEnqueued += 0; JudgeSkipped += 0;
        }
    }

    // FFmpeg writes showinfo before it emits the matching JPEG. Keep only a small
    // ordered queue so an every-frame scan remains streaming and cannot buffer a video.
    private sealed class FrameTimestampReader
    {
        private static readonly Regex TimestampPattern = new(
            @"Parsed_showinfo_.*?\bn:\s*(?<n>\d+).*?\bpts:\s*(?<pts>-?\d+).*?\bpts_time:(?<ptstime>-?(?:\d+(?:\.\d*)?|\.\d+))(?:.*?\bduration_time:(?<duration>\d+(?:\.\d*)?|\.\d+))?",
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);
        private readonly Channel<FrameTiming> _timestamps = Channel.CreateBounded<FrameTiming>(new BoundedChannelOptions(48)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        private readonly StringBuilder _diagnostic = new();
        // Real decode-error buffer (17/10): _diagnostic previously capped at 48KB keeping the HEAD
        // (header + showinfo junk) so when ffmpeg died mid-stream, the exception only
        // carried the showinfo tail — real CUDA/cuvid/decoder error lines were lost entirely (all 4 lanes
        // in the 16/10 fallback showed no cause). Now useful error lines are collected separately by
        // keyword when the error buffer is EMPTY. The 48KB tail is preserved for deep diagnosis.
        private const int DiagnosticTailCapacity = 48 * 1024;
        private const int DecoderErrorCapacity = 8 * 1024;
        private static readonly string[] DecoderErrorKeywords =
        [
            "error", "cuda", "cuvid", "nvdec", "decoder", "Impossible", "Invalid",
            "corrupt", "damaged", "failed", "session", "out of memory", "no such", "cannot load",
        ];
        private readonly StringBuilder _decoderErrors = new();

        private bool TryCaptureDecoderErrorLine(string line)
        {
            foreach (var keyword in DecoderErrorKeywords)
                if (line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (_decoderErrors.Length < DecoderErrorCapacity) _decoderErrors.AppendLine(line);
                    return true;
                }
            return false;
        }

        private string BuildDiagnostics()
        {
            var errors = _decoderErrors.ToString();
            // Real errors are placed LAST: Compact() (used at throw/lane log) keeps the last 300 characters,
            // so even a short log line falls into the decode error instead of showinfo junk.
            return errors.Length > 0
                ? $"[stderr tail] {_diagnostic} [decoder errors] {errors}"
                : _diagnostic.ToString();
        }

        // Approach A: -copyts keeps the source clock, showinfo pts_time is the source timestamp. Do not add startAt.
        public FrameTimestampReader(StreamReader standardError) => Completion = PumpAsync(standardError);

        public Task<string> Completion { get; }

        public async Task<FrameTiming> ReadAsync(CancellationToken cancellationToken)
        {
            try { return await _timestamps.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException error)
            {
                throw new InvalidDataException(
                    "FFmpeg không trả PTS cho frame OCR chính xác. " + Compact(BuildDiagnostics()), error);
            }
        }

        private async Task<string> PumpAsync(StreamReader standardError)
        {
            Exception? failure = null;
            var expectedN = 0;
            try
            {
                while (await standardError.ReadLineAsync() is { } line)
                {
                    _diagnostic.AppendLine(line);
                    if (_diagnostic.Length > DiagnosticTailCapacity)
                        _diagnostic.Remove(0, _diagnostic.Length - DiagnosticTailCapacity);
                    TryCaptureDecoderErrorLine(line);
                    if (line.IndexOf("Parsed_showinfo_", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("config ", StringComparison.Ordinal) >= 0) continue;
                    var match = TimestampPattern.Match(line);
                    if (!match.Success)
                    {
                        // Ancillary showinfo lines (color_range/color_space/...) are not frame records.
                        // A real frame record always has " n:". Missing => fail the lane, do not shift JPEG↔timestamp.
                        // Omitting a real frame record would be caught by the n-sequence check at the next record.
                        if (line.IndexOf(" n:", StringComparison.Ordinal) < 0) continue;
                        throw new InvalidDataException("FFmpeg showinfo frame không parse được PTS (fail lane, không shift JPEG↔timestamp).");
                    }
                    if (!int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        || !long.TryParse(match.Groups["pts"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spts)
                        || !double.TryParse(match.Groups["ptstime"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                        || double.IsNaN(pts) || double.IsInfinity(pts))
                        throw new InvalidDataException("FFmpeg trả PTS frame OCR không hợp lệ.");
                    double? duration = null;
                    var durGroup = match.Groups["duration"];
                    if (durGroup.Success && double.TryParse(durGroup.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)
                        && double.IsFinite(dur) && dur > 0)
                        duration = dur;
                    if (n != expectedN)
                        throw new InvalidDataException($"FFmpeg showinfo mất record (expected n={expectedN}, got n={n}); fail lane để tránh shift JPEG↔timestamp.");
                    expectedN++;
                    await _timestamps.Writer.WriteAsync(new FrameTiming(spts, pts, duration));
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                _timestamps.Writer.TryComplete(failure);
            }
            return BuildDiagnostics();
        }
    }

    private sealed class JpegStreamReader
    {
        private readonly Stream _stream;
        private readonly List<byte> _pending = [];
        private readonly byte[] _buffer = new byte[64 * 1024];

        public JpegStreamReader(Stream stream) => _stream = stream;

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var start = FindMarker(_pending, 0xFF, 0xD8, 0);
                if (start > 0) _pending.RemoveRange(0, start);
                var end = FindMarker(_pending, 0xFF, 0xD9, 2);
                if (end >= 0)
                {
                    var length = end + 2;
                    var jpeg = _pending.GetRange(0, length).ToArray();
                    _pending.RemoveRange(0, length);
                    return jpeg;
                }
                var read = await _stream.ReadAsync(_buffer, cancellationToken);
                if (read == 0) return null;
                _pending.AddRange(_buffer.AsSpan(0, read).ToArray());
                if (_pending.Count > 32 * 1024 * 1024) throw new InvalidDataException("Frame JPEG OCR vượt giới hạn.");
            }
        }

        private static int FindMarker(List<byte> bytes, byte first, byte second, int from)
        {
            for (var index = Math.Max(0, from); index + 1 < bytes.Count; index++)
                if (bytes[index] == first && bytes[index + 1] == second) return index;
            return -1;
        }
    }
}

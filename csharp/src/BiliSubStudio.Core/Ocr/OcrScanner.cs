using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Ocr;

public sealed class OcrScanner
{
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly OcrManager _ocr;
    private readonly HardwareService _hardware;
    private readonly OcrCheckpointStore _checkpoints;
    private OwnedProcessGroup? _activeProcesses;

    internal OcrScanner(ToolManager tools, ProcessRunner processes, OcrManager ocr, HardwareService hardware, OcrCheckpointStore checkpoints)
    {
        _tools = tools;
        _processes = processes;
        _ocr = ocr;
        _hardware = hardware;
        _checkpoints = checkpoints;
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

    public async Task<OcrScanResult> RunAsync(AppJob job, OcrScanRequest request, OcrScanStartMode startMode)
    {
        request = request with { Region = OcrCheckpointStore.CanonicalRegion(request.Region) };
        var processes = new OwnedProcessGroup();
        if (Interlocked.CompareExchange(ref _activeProcesses, processes, null) is not null)
        {
            await processes.DisposeAsync();
            throw new InvalidOperationException("Một lần quét OCR khác vẫn đang sở hữu tiến trình FFmpeg.");
        }
        try
        {
            return await RunCoreAsync(job, request, startMode, processes);
        }
        catch (Exception) when (job.CancellationToken.IsCancellationRequested)
        {
            // Cancellation is not terminal until every app-owned FFmpeg tree is reaped and
            // Python worker is stopped, and the exact checkpoint identity is verified absent.
            await processes.StopAsync();
            await _ocr.StopAsync();
            await _checkpoints.RemoveAsync(request, CancellationToken.None);
            throw new OperationCanceledException(job.CancellationToken);
        }
        finally
        {
            await processes.DisposeAsync();
            Interlocked.CompareExchange(ref _activeProcesses, null, processes);
        }
    }

    internal int ActiveProcessCount => Volatile.Read(ref _activeProcesses)?.ActiveCount ?? 0;

    private async Task<OcrScanResult> RunCoreAsync(
        AppJob job,
        OcrScanRequest request,
        OcrScanStartMode startMode,
        OwnedProcessGroup processes)
    {
        var token = job.CancellationToken;
        var source = Path.GetFullPath(request.Path.Trim());
        if (!File.Exists(source) || new FileInfo(source).Length <= 0) throw new FileNotFoundException("Thiếu video nguồn.", source);
        if (request.Duration <= 0) throw new ArgumentException("Thiếu thời lượng video.");
        await _ocr.ConfigureDeviceAsync(request.Device, token);
        await _ocr.EnsureAsync(token);
        var ffmpeg = await _tools.EnsureFfmpegAsync(token);
        var mode = OcrCheckpointStore.ModeFor(request.Mode, request.Sensitivity);
        if (startMode == OcrScanStartMode.Fresh)
            await _checkpoints.RemoveAsync(request, token);
        var saved = startMode == OcrScanStartMode.Resume
            ? await _checkpoints.LoadAsync(request, token)
            : null;
        if (startMode == OcrScanStartMode.Resume && saved is null)
            throw new InvalidOperationException("Không còn checkpoint OCR phù hợp để tiếp tục. Hãy Quét từ đầu.");

        int selected;
        if (saved is null)
        {
            job.Set("benchmark", 0.5, "OCR · đo nền CPU/RAM trước benchmark pipeline thật...");
            var baseline = await _hardware.BenchmarkAsync(token);
            job.Log($"Benchmark tốc độ nền: CPU {baseline.CpuMegabytesPerSecond:0} MiB/s · RAM {baseline.MemoryMegabytesPerSecond:0} MiB/s. Auto sẽ quyết định bằng live RAM/VRAM, topology thật và throughput.");
            selected = await SelectParallelismAsync(job, ffmpeg, request, processes, token);
        }
        else
        {
            selected = saved.SelectedParallelism;
            job.Set("resume", -1, $"OCR Resume: dựng lại đúng topology {selected} từ checkpoint...");
            EnsureResourceHeadroom(job, selected, isManual: true, lastStable: 0);
            var restored = await _ocr.ConfigureWorkerPoolAsync(selected, token);
            if (restored != selected || !_ocr.Status.Ready)
                throw new InvalidOperationException($"Không khôi phục đủ topology checkpoint {selected}; chỉ có {restored} Python worker.");
            job.Log($"Resume Commit {selected}: dựng lại đủ {selected} Python worker từ checkpoint; bỏ qua Auto benchmark và throughput probe.");
        }
        var configuredWorkers = _ocr.Status.Workers;
        var configuredWorkerKinds = _ocr.Status.WorkerKinds;
        if (configuredWorkers != selected)
            throw new InvalidDataException($"OCR topology chưa khóa đúng: {selected} FFmpeg lane nhưng có {configuredWorkers} Python worker.");
        var checkpoint = saved ?? await _checkpoints.NewAsync(request, selected, token);
        selected = checkpoint.SelectedParallelism;
        if (checkpoint.Lanes.Count != selected)
            throw new InvalidDataException($"OCR checkpoint topology không hợp lệ: {checkpoint.Lanes.Count}/{selected} lane.");
        if (configuredWorkers != selected)
            throw new InvalidDataException($"OCR checkpoint yêu cầu {selected} lane nhưng worker pool đang có {configuredWorkers}.");
        job.Log(saved is null
            ? $"OCR Commit: benchmark xong, khóa {selected} pipeline = {selected} FFmpeg lane + {configuredWorkers} Python worker ({configuredWorkerKinds})."
            : $"Resume checkpoint schema {checkpoint.Schema}: giữ topology {selected} = {selected} FFmpeg lane + {configuredWorkers} Python worker ({configuredWorkerKinds}).");
        var decoder = await ProbeNvdecAsync(ffmpeg, source, request.Region, mode, processes, token) ? "nvdec" : "software";
        job.Log(decoder == "nvdec" ? "Decoder: NVIDIA NVDEC." : "Decoder: software fallback.");

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
            .TakeLast(120)
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
        Exception? laneFailure = null;
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<OcrLaneCheckpoint> RunGuardedLaneAsync(OcrLaneCheckpoint lane)
        {
            OcrLaneCheckpoint? outcome = null;
            try
            {
                outcome = await RunLaneWithFallbackAsync(
                    ffmpeg, source, request, mode, lane, decoder,
                    (at, frames, images, committedCues, activeCue) =>
                    {
                        lock (progressGate)
                        {
                            progress[lane.Segment.Index] = at;
                            frameProgress[lane.Segment.Index] = frames;
                            imageProgress[lane.Segment.Index] = images;
                            var isLast = lane.Segment.Index == checkpoint.Lanes.Count - 1;
                            foreach (var cue in committedCues)
                            {
                                if (cue.Start < lane.Segment.CoreStart
                                    || (!isLast && cue.Start >= lane.Segment.CoreEnd)
                                    || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                                liveCommitted.Add(cue);
                            }
                            if (liveCommitted.Count > 120)
                                liveCommitted.RemoveRange(0, liveCommitted.Count - 120);
                            liveActive[lane.Segment.Index] = activeCue is not null
                                && activeCue.Start >= lane.Segment.CoreStart
                                && (isLast ? activeCue.Start <= lane.Segment.CoreEnd : activeCue.Start < lane.Segment.CoreEnd)
                                    ? activeCue
                                    : null;
                            PublishTelemetry(
                                job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed,
                                configuredWorkers, configuredWorkerKinds, request.Duration, liveCommitted, liveActive,
                                started.Elapsed.TotalSeconds);
                        }
                    }, job, processes, laneCancellation.Token);
                return outcome;
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested && error is not OperationCanceledException)
                    Interlocked.CompareExchange(ref laneFailure, error, null);
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
                    }
                    PublishTelemetry(
                        job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed,
                        configuredWorkers, configuredWorkerKinds, request.Duration, liveCommitted, liveActive,
                        started.Elapsed.TotalSeconds);
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
            if (!token.IsCancellationRequested && laneFailure is not null)
                throw new InvalidOperationException("OCR lane lỗi: " + laneFailure.Message, laneFailure);
            throw;
        }

        token.ThrowIfCancellationRequested();
        var paused = lanes.Any(x => !x.Completed) && job.IsPauseRequested;
        var mergedAll = Reconcile(lanes, out var boundaryMerges);
        var frontier = OcrCheckpointStore.ContiguousFrontier(lanes);
        var merged = paused ? mergedAll.Where(x => x.Start <= frontier + 0.001).ToArray() : mergedAll;
        string? pauseMessage = null;
        if (paused)
        {
            var pausedCheckpoint = checkpoint with { Lanes = lanes.ToList(), BoundaryMerges = boundaryMerges };
            await _checkpoints.SaveAsync(request, pausedCheckpoint, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            await processes.StopAsync();
            await _ocr.StopAsync();
            if (processes.ActiveCount != 0 || _ocr.Status.Workers != 0)
                throw new IOException($"Tạm dừng OCR chưa thu sạch tài nguyên: {processes.ActiveCount} FFmpeg/process tree · {_ocr.Status.Workers} Python worker.");
            pauseMessage = $"Đã tạm dừng an toàn tại {FormatClock(frontier)} · 0 Python worker · 0 FFmpeg/process tree.";
        }
        else
        {
            await _checkpoints.RemoveAsync(request, CancellationToken.None);
        }

        var frames = lanes.Sum(x => x.Frames);
        var images = lanes.Sum(x => x.OcrImages);
        var media = lanes.Sum(x => Math.Max(0, Math.Min(x.Segment.CoreEnd, x.MediaSeconds) - x.Segment.CoreStart));
        var result = new OcrScanResult(
            merged, frames, images, media, started.Elapsed.TotalSeconds,
            started.Elapsed.TotalSeconds > 0 ? media / started.Elapsed.TotalSeconds : 0,
            selected, lanes.Count(x => x.Completed), boundaryMerges, decoder,
            configuredWorkers, configuredWorkerKinds, frontier, mergedAll.Count, paused);

        job.SetResult(result);
        if (paused)
        {
            job.PauseComplete(pauseMessage);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    public Task<OcrCheckpointInfo> InspectCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.InspectAsync(request, cancellationToken);
    public Task RemoveCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.RemoveAsync(request, cancellationToken);

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
            EnsureResourceHeadroom(job, explicitValue, isManual: true, lastStable: 0);
            job.Set("benchmark", -1, $"OCR Manual Probe: {explicitValue} Python worker + {explicitValue} FFmpeg pipeline...");
            await ProbeTopologyLevelAsync(ffmpeg, request, explicitValue, processes, actual =>
            {
                var resources = _hardware.ResourceSnapshot();
                job.SetResult(new OcrBenchmarkTelemetry(
                    explicitValue, 0, 16, actual, _ocr.Status.WorkerKinds,
                    $"đang chạy đủ {actual} pipeline", OcrAutoResourcePolicy.FormatSnapshot(resources)));
            }, cancellationToken);
            job.Log($"Manual Probe {explicitValue}: topology đầy đủ PASS; bắt đầu quét với đúng {explicitValue} luồng.");
            return explicitValue;
        }

        var lastStable = 0;
        var lastThroughput = 0d;
        long observedVramPerGpuWorkerBytes = 0;
        job.Log("Auto Benchmark Predict → Probe → Commit: xét CPU/RAM/VRAM theo mốc 1 → 2 → 4 → 8 → 16; VRAM tăng thêm được học từ delta thực tế của chính máy sau mỗi topology PASS, không dùng định mức cố định theo worker. Nếu một mốc không đạt, thử lùi các mức giữa trước khi khóa topology. Mỗi mức chỉ PASS khi đủ đúng N pipeline thật, còn reserve sau probe và throughput tăng ít nhất 10%.");
        var selected = await OcrTopologyBenchmark.SelectAsync(
            async (level, token) =>
            {
                var beforeWorkers = _ocr.Status.Workers;
                var beforeResources = _hardware.ResourceSnapshot();
                var preflight = EnsureResourceHeadroom(
                    job, level, isManual: false, lastStable: lastStable,
                    observedVramPerGpuWorkerBytes: observedVramPerGpuWorkerBytes,
                    resources: beforeResources, requireMeasuredVram: true);
                job.Set("benchmark", -1, $"OCR Auto Benchmark {level}/16: tài nguyên đạt ngưỡng; đang tạo đúng {level} Python worker...");
                var probe = await ProbeTopologyLevelAsync(ffmpeg, request, level, processes, actual =>
                {
                    var resources = _hardware.ResourceSnapshot();
                    job.SetResult(new OcrBenchmarkTelemetry(
                        level, lastStable, 16, actual, _ocr.Status.WorkerKinds,
                        $"đang chạy đủ {actual} FFmpeg + OCR pipeline", OcrAutoResourcePolicy.FormatSnapshot(resources)));
                }, token);

                var afterResources = _hardware.ResourceSnapshot();
                var addedWorkers = Math.Max(0, level - beforeWorkers);
                if (addedWorkers > 0
                    && beforeResources.VramTelemetryAvailable
                    && afterResources.VramTelemetryAvailable)
                {
                    var consumedVram = Math.Max(0, beforeResources.AvailableVramBytes - afterResources.AvailableVramBytes);
                    if (consumedVram > 0)
                    {
                        var measuredPerWorker = consumedVram / addedWorkers;
                        observedVramPerGpuWorkerBytes = Math.Max(observedVramPerGpuWorkerBytes, measuredPerWorker);
                        job.Log($"Auto VRAM learn {beforeWorkers}→{level}: delta thực tế ~{consumedVram / 1024d / 1024d:0} MiB · giữ dự toán ~{observedVramPerGpuWorkerBytes / 1024d / 1024d:0} MiB/GPU worker trước margin.");
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
                job.Log($"Auto Benchmark {level}: đủ {level} Python worker + {level} FFmpeg pipeline PASS · {probe.Throughput:0.00} mẫu/s · preflight {preflight.Summary} · sau probe {OcrAutoResourcePolicy.FormatSnapshot(afterResources)}.");
            },
            async (best, token) =>
            {
                job.Set("benchmark", -1, $"OCR Auto Rollback: dựng lại topology ổn định {best}...");
                var restored = await _ocr.ConfigureWorkerPoolAsync(best, token);
                if (restored != best || !_ocr.Status.Ready)
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
            hardware, live, _ocr.Status.ActiveMode, _ocr.Status.Workers, candidate,
            observedVramPerGpuWorkerBytes, requireMeasuredVram);
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
            var actual = await _ocr.ConfigureWorkerPoolAsync(level, timeout.Token);
            if (actual != level || !_ocr.Status.Ready)
                throw new InvalidOperationException($"chỉ tạo được {actual}/{level} Python worker");
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
            if (_ocr.Status.Workers != level || !_ocr.Status.Ready)
                throw new InvalidOperationException($"topology {level} mất worker trong lúc probe; còn {_ocr.Status.Workers}/{level}");
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
        IReadOnlyList<OcrCue> liveCommitted,
        IReadOnlyList<OcrCue?> liveActive,
        double elapsedSeconds)
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
        var completedCount = completed.Count(x => x);
        var active = Math.Max(0, lanes.Count - completedCount);
        var recentCues = OcrCueReconciler.MergeTouchingIdentical(liveCommitted
            .Concat(liveActive.Where(cue => cue is not null).Select(cue => cue!)))
            .TakeLast(120)
            .ToArray();
        job.Set("scanning", percent, $"Đang quét OCR · {lanes.Count} FFmpeg lane · {workers} worker · {percent:0.0}%");
        job.SetResult(new OcrScanResult(
            recentCues, frames.Sum(), images.Sum(), unique, elapsedSeconds,
            elapsedSeconds > 0 ? unique / elapsedSeconds : 0,
            lanes.Count, completedCount, 0, "live", workers, workerKinds, frontier, recentCues.Length, false));
    }

    private async Task<OcrLaneCheckpoint> RunLaneWithFallbackAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint lane,
        string decoder,
        Action<double, int, int, IReadOnlyList<OcrCue>, OcrCue?> onProgress,
        AppJob job,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        if (lane.Completed) return lane;
        try
        {
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, decoder == "nvdec", onProgress, job, processes, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (decoder == "nvdec" && error is not OcrRecognitionException)
        {
            job.Log($"Lane {lane.Segment.Index + 1}: NVDEC lỗi, fallback software: {error.Message}");
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, false, onProgress, job, processes, cancellationToken);
        }
    }

    private async Task<OcrLaneCheckpoint> RunLaneAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint saved,
        bool nvdec,
        Action<double, int, int, IReadOnlyList<OcrCue>, OcrCue?> onProgress,
        AppJob job,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        var segment = saved.Segment;
        var startAt = Math.Clamp(saved.MediaSeconds, segment.ScanStart, segment.ScanEnd);
        var tracker = new SubtitleTracker(mode.Fps, mode.LowConfidence, exactFrameTiming: mode.EveryFrame);
        tracker.Restore(saved.Cues, saved.Active);
        var publishedCueCount = tracker.Cues.Count;
        var args = BuildLaneArguments(source, request.Region, mode, startAt, segment.ScanEnd, nvdec);
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
        // FFmpeg can expose either source-global PTS or seek-relative PTS after
        // an input -ss. Normalize that domain here so every lane reaches the
        // tracker, checkpoint and reconciler on the same absolute video clock.
        var timestamps = new FrameTimestampReader(process.StandardError, startAt);
        var errorTask = timestamps.Completion;
        var reader = new JpegStreamReader(process.StandardOutput.BaseStream);
        var frames = saved.Frames;
        var images = saved.OcrImages;
        var mediaSeconds = startAt;
        var lastFrameDuration = mode.EveryFrame ? 0 : 1 / mode.Fps;
        var paused = false;
        var stoppedAtBoundary = false;
        try
        {
            while (await reader.ReadAsync(cancellationToken) is { } jpeg)
            {
                var timing = await timestamps.ReadAsync(cancellationToken);
                var at = timing.PresentationTime;
                if (at > segment.ScanEnd + 0.001) { stoppedAtBoundary = true; break; }
                frames++;
                images++;
                mediaSeconds = Math.Min(segment.ScanEnd, at + timing.Duration);
                lastFrameDuration = timing.Duration;
                OcrResult result;
                var activeShortText = tracker.Active?.Text is { } activeText && activeText.EnumerateRunes().Count() == 1 ? activeText : null;
                try
                {
                    result = FilterOffBaselineOverlayLines(await _ocr.RunAsync(Convert.ToBase64String(jpeg), cancellationToken,
                        recoverShortBlank: tracker.Active is not null, activeShortText: activeShortText), request.Region);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    throw new OcrRecognitionException("OCR worker lỗi: " + error.Message, error);
                }
                if (!result.Ok)
                    throw new OcrRecognitionException(result.Error ?? "OCR worker trả kết quả lỗi.");
                if (NeedsEnhancedRecognition(result, Math.Max(.78, mode.LowConfidence + .10))
                    || NeedsActiveCueBlankRecovery(result, tracker.Active is not null))
                {
                    try
                    {
                        var enhanced = await CaptureFrameWithFfmpegAsync(ffmpeg, source, at, request.Region, enhanced: true, processes, cancellationToken);
                        var alternate = FilterOffBaselineOverlayLines(await _ocr.RunAsync(Convert.ToBase64String(enhanced), cancellationToken,
                            recoverShortBlank: tracker.Active is not null, activeShortText: activeShortText), request.Region);
                        if (alternate.Ok && PreferRecognition(alternate, result)) result = alternate;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        // A second-pass frame is an accuracy enhancement, never a reason
                        // to discard the valid first OCR result or stop a long scan.
                        job.Log($"OCR frame {at:0.000}s: enhanced retry skipped ({Compact(error.Message)}).");
                    }
                }
                tracker.Observe(at, timing.Duration, result);
                var committedCues = tracker.Cues.Count > publishedCueCount
                    ? tracker.Cues.Skip(publishedCueCount).ToArray()
                    : Array.Empty<OcrCue>();
                publishedCueCount = tracker.Cues.Count;
                onProgress(at, frames, images, committedCues, tracker.Active);
                if (job.IsPauseRequested && tracker.CanCheckpoint)
                {
                    paused = true;
                    break;
                }
            }
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
            onProgress(mediaSeconds, frames, images, finalCommitted, tracker.Active);
            mediaSeconds = segment.CoreEnd;
        }
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

    private static IReadOnlyList<OcrCue> Reconcile(IReadOnlyList<OcrLaneCheckpoint> lanes, out int merges)
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
                var previous = output[^1];
                output[^1] = (previous.Cue with
                    {
                        Text = PreferReconciledText(previous.Cue.Text, cue.Text),
                        End = Math.Max(previous.Cue.End, cue.End),
                        Confidence = Math.Max(previous.Cue.Confidence, cue.Confidence),
                    }, previous.Lane);
                merges++;
            }
            else output.Add(item);
        }
        return OcrCueReconciler.MergeTouchingIdentical(output.Select(item => item.Cue));
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
        var filter = string.Format(CultureInfo.InvariantCulture,
            "crop=iw*{0}:ih*{1}:iw*{2}:ih*{3},scale=1280:320:force_original_aspect_ratio=decrease,pad=1280:320:(ow-iw)/2:(oh-ih)/2:black{4}",
            region.Width, region.Height, region.X, region.Y, enhanced ? ",eq=contrast=1.25:brightness=0.03,unsharp=5:5:0.8" : string.Empty);
        return await _processes.CaptureBytesAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Math.Max(0, at).ToString("0.000", CultureInfo.InvariantCulture),
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
        var args = BuildLaneArguments(source, region, mode, 0, Math.Min(2, mode.Fps > 0 ? 2 : 1), nvdec: true).ToList();
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

    private static IReadOnlyList<string> BuildLaneArguments(string source, OcrRegion region, OcrScanMode mode, double start, double end, bool nvdec)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-nostdin" };
        if (nvdec) args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", "0"]);
        if (start > 0) args.AddRange(["-ss", start.ToString("0.######", CultureInfo.InvariantCulture)]);
        args.Add("-copyts");
        args.AddRange(["-i", source]);
        // With -copyts, output timestamps stay on the source clock after -ss.
        // A relative -t can end later lanes before their first JPEG is emitted.
        if (end > start) args.AddRange(["-to", end.ToString("0.######", CultureInfo.InvariantCulture)]);
        var crop = string.Format(CultureInfo.InvariantCulture,
            "crop=iw*{0}:ih*{1}:iw*{2}:ih*{3},scale=1280:320:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=1280:320:(ow-iw)/2:(oh-ih)/2:black",
            region.Width, region.Height, region.X, region.Y);
        var fps = mode.Fps.ToString("0.######", CultureInfo.InvariantCulture);
        var filter = nvdec
            ? mode.EveryFrame
                ? $"hwdownload,format=nv12|p010le|p016le,{crop},showinfo"
                : $"hwdownload,format=nv12|p010le|p016le,fps={fps},{crop},showinfo"
            : mode.EveryFrame ? $"{crop},showinfo" : $"fps={fps},{crop},showinfo";
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", filter, "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"]);
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

    private static bool NeedsActiveCueBlankRecovery(OcrResult result, bool hasActiveCue) =>
        hasActiveCue && result.Ok && !result.Detected;

    private static OcrResult FilterOffBaselineOverlayLines(OcrResult result, OcrRegion region)
    {
        if (!result.Ok) return result;
        if (result.Lines.Count == 1 && IsUpperOffBaselineOverlay(result.Lines[0], region))
            return result with { Detected = false, Text = string.Empty, Confidence = 0, Lines = [] };
        if (result.Lines.Count < 2) return result;
        var baseline = result.Lines
            .Where(line => line.Box.Length >= 4
                && line.Confidence >= .90
                && Math.Abs(line.Box[2] - line.Box[0]) >= Math.Abs(line.Box[3] - line.Box[1]) * .8)
            .OrderByDescending(line => line.Box[3])
            .ThenByDescending(line => line.Confidence)
            .FirstOrDefault();
        if (baseline is null) return result;

        var baselineHeight = Math.Max(1, Math.Abs(baseline.Box[3] - baseline.Box[1]));
        var baselineCenter = baseline.Box[1] + baseline.Box[3];
        var retained = result.Lines.Where(line =>
        {
            if (ReferenceEquals(line, baseline) || line.Box.Length < 4) return true;
            var width = Math.Abs(line.Box[2] - line.Box[0]);
            var height = Math.Abs(line.Box[3] - line.Box[1]);
            var vertical = height > width * 1.5;
            var farAbove = baselineCenter - (line.Box[1] + line.Box[3]) > baselineHeight * 3;
            return !(vertical || farAbove);
        }).ToArray();
        if (retained.Length == result.Lines.Count) return result;
        return result with
        {
            Text = string.Join("\n", retained.Select(line => line.Text)),
            Confidence = retained.Average(line => line.Confidence),
            Lines = retained,
        };
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
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 300 ? text[..300] + "…" : text;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    private sealed record OcrTopologyProbe(double Throughput);
    private sealed class OcrRecognitionException(string message, Exception? inner = null) : Exception(message, inner);
    private readonly record struct FrameTiming(double PresentationTime, double Duration);

    // FFmpeg writes showinfo before it emits the matching JPEG. Keep only a small
    // ordered queue so an every-frame scan remains streaming and cannot buffer a video.
    private sealed class FrameTimestampReader
    {
        private static readonly Regex TimestampPattern = new(
            @"\bpts_time:(?<pts>-?(?:\d+(?:\.\d*)?|\.\d+))\s+duration:\s*-?\d+\s+duration_time:(?<duration>\d+(?:\.\d*)?|\.\d+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly Channel<FrameTiming> _timestamps = Channel.CreateBounded<FrameTiming>(new BoundedChannelOptions(48)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        private readonly StringBuilder _diagnostic = new();

        public FrameTimestampReader(StreamReader standardError, double startAt) => Completion = PumpAsync(standardError, startAt);

        public Task<string> Completion { get; }

        public async Task<FrameTiming> ReadAsync(CancellationToken cancellationToken)
        {
            try { return await _timestamps.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException error)
            {
                throw new InvalidDataException(
                    "FFmpeg không trả PTS cho frame OCR Chính xác. " + Compact(_diagnostic.ToString()), error);
            }
        }

        private async Task<string> PumpAsync(StreamReader standardError, double startAt)
        {
            Exception? failure = null;
            double? timestampOffset = null;
            try
            {
                while (await standardError.ReadLineAsync() is { } line)
                {
                    if (_diagnostic.Length < 48 * 1024) _diagnostic.AppendLine(line);
                    var match = TimestampPattern.Match(line);
                    if (!match.Success) continue;
                    if (!double.TryParse(match.Groups["pts"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                        || !double.TryParse(match.Groups["duration"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                        || double.IsNaN(pts) || double.IsInfinity(pts) || duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration))
                        throw new InvalidDataException("FFmpeg trả PTS frame OCR không hợp lệ.");
                    if (timestampOffset is null)
                    {
                        var tolerance = Math.Max(.5, duration * 2);
                        var relativeDistance = Math.Abs(pts);
                        var absoluteDistance = Math.Abs(pts - startAt);
                        timestampOffset = startAt > tolerance && relativeDistance + tolerance < absoluteDistance
                            ? startAt
                            : 0d;
                    }
                    await _timestamps.Writer.WriteAsync(new FrameTiming(pts + timestampOffset.Value, duration));
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
            return _diagnostic.ToString();
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

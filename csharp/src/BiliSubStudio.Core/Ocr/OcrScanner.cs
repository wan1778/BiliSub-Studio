using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        await _ocr.ConfigureDeviceAsync(device, cancellationToken);
        await _ocr.EnsureAsync(cancellationToken);
        var jpeg = await CaptureFrameAsync(path, at, region, enhanced: false, cancellationToken);
        var result = await _ocr.RunAsync(Convert.ToBase64String(jpeg), cancellationToken);
        if (result.Ok && result.Confidence < 0.68)
        {
            var enhanced = await CaptureFrameAsync(path, at, region, enhanced: true, cancellationToken);
            var alternate = await _ocr.RunAsync(Convert.ToBase64String(enhanced), cancellationToken);
            if (alternate.Ok && alternate.Confidence > result.Confidence) result = alternate;
        }
        return result;
    }

    public async Task<OcrScanResult> RunAsync(AppJob job, OcrScanRequest request, OcrScanStartMode startMode)
    {
        request = request with { Region = OcrCheckpointStore.CanonicalRegion(request.Region) };
        try
        {
            return await RunCoreAsync(job, request, startMode);
        }
        catch (Exception) when (job.CancellationToken.IsCancellationRequested)
        {
            // Cancellation means discard this incomplete scan. Terminal cancelled state is
            // published only after the exact checkpoint identity is verified absent.
            await _checkpoints.RemoveAsync(request, CancellationToken.None);
            throw new OperationCanceledException(job.CancellationToken);
        }
    }

    private async Task<OcrScanResult> RunCoreAsync(AppJob job, OcrScanRequest request, OcrScanStartMode startMode)
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

        job.Set("benchmark", 0.5, "OCR · đo nền CPU/RAM trước benchmark pipeline thật...");
        var baseline = await _hardware.BenchmarkAsync(token);
        job.Log($"Benchmark nền (chỉ telemetry): CPU {baseline.CpuMegabytesPerSecond:0} MiB/s · RAM {baseline.MemoryMegabytesPerSecond:0} MiB/s. Kết quả này không chặn bậc Auto.");

        int selected;
        if (saved is null)
        {
            selected = await SelectParallelismAsync(job, ffmpeg, request, token);
        }
        else
        {
            selected = saved.SelectedParallelism;
            job.Set("benchmark", -1, $"OCR Resume Probe: kiểm tra lại topology {selected} trước khi tiếp tục...");
            await ProbeTopologyLevelAsync(ffmpeg, request, selected, token);
            job.Log($"Resume Probe {selected}: {selected} Python worker + {selected} FFmpeg pipeline PASS.");
        }
        var configuredWorkers = _ocr.Status.Workers;
        if (configuredWorkers != selected)
            throw new InvalidDataException($"OCR topology chưa khóa đúng: {selected} FFmpeg lane nhưng có {configuredWorkers} Python worker.");
        var checkpoint = saved ?? await _checkpoints.NewAsync(request, selected, token);
        selected = checkpoint.SelectedParallelism;
        if (checkpoint.Lanes.Count != selected)
            throw new InvalidDataException($"OCR checkpoint topology không hợp lệ: {checkpoint.Lanes.Count}/{selected} lane.");
        if (configuredWorkers != selected)
            throw new InvalidDataException($"OCR checkpoint yêu cầu {selected} lane nhưng worker pool đang có {configuredWorkers}.");
        job.Log(saved is null
            ? $"OCR Commit: benchmark xong, khóa {selected} pipeline = {selected} FFmpeg lane + {configuredWorkers} Python worker ({_ocr.Status.WorkerKinds})."
            : $"Resume checkpoint schema 4: giữ và kiểm tra lại {selected} pipeline = {selected} FFmpeg lane + {configuredWorkers} Python worker ({_ocr.Status.WorkerKinds}).");
        var decoder = await ProbeNvdecAsync(ffmpeg, source, request.Region, mode, token) ? "nvdec" : "software";
        job.Log(decoder == "nvdec" ? "Decoder: NVIDIA NVDEC." : "Decoder: software fallback.");

        var started = Stopwatch.StartNew();
        var progress = checkpoint.Lanes.Select(x => x.MediaSeconds).ToArray();
        var frameProgress = checkpoint.Lanes.Select(x => x.Frames).ToArray();
        var imageProgress = checkpoint.Lanes.Select(x => x.OcrImages).ToArray();
        var completed = checkpoint.Lanes.Select(x => x.Completed).ToArray();
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
                    (at, frames, images) =>
                    {
                        lock (progressGate)
                        {
                            progress[lane.Segment.Index] = at;
                            frameProgress[lane.Segment.Index] = frames;
                            imageProgress[lane.Segment.Index] = images;
                            PublishTelemetry(job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed, configuredWorkers, _ocr.Status.WorkerKinds, request.Duration);
                        }
                    }, job, laneCancellation.Token);
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
                    PublishTelemetry(job, checkpoint.Lanes, progress, frameProgress, imageProgress, completed, configuredWorkers, _ocr.Status.WorkerKinds, request.Duration);
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
            pauseMessage = $"Đã tạm dừng an toàn tại {FormatClock(frontier)}.";
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
            configuredWorkers, _ocr.Status.WorkerKinds, frontier, mergedAll.Count, paused);

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
        CancellationToken cancellationToken)
    {
        var value = request.Parallelism.Trim().ToLowerInvariant();
        if (value != "auto" && value.Length > 0)
        {
            if (!int.TryParse(value, out var explicitValue) || explicitValue < 1 || explicitValue > 16)
                throw new ArgumentException("Số luồng OCR phải là auto hoặc 1..16.");
            job.Set("benchmark", -1, $"OCR Manual Probe: {explicitValue} Python worker + {explicitValue} FFmpeg pipeline...");
            await ProbeTopologyLevelAsync(ffmpeg, request, explicitValue, cancellationToken);
            job.Log($"Manual Probe {explicitValue}: topology đầy đủ PASS; bắt đầu quét với đúng {explicitValue} luồng.");
            return explicitValue;
        }

        var lastStable = 0;
        job.Log("Auto Benchmark: bắt buộc thử tuần tự 1 → 2 → 4 → 8 → 16 bằng pipeline thật; không dùng dự đoán phần cứng làm trần.");
        var selected = await OcrTopologyBenchmark.SelectAsync(
            async (level, token) =>
            {
                job.Set("benchmark", -1, $"OCR Auto Benchmark {level}/16: tạo {level} Python worker và chạy {level} FFmpeg + OCR đồng thời...");
                job.SetResult(new OcrBenchmarkTelemetry(level, lastStable, 16, _ocr.Status.Workers, _ocr.Status.WorkerKinds, "đang thử"));
                var watch = Stopwatch.StartNew();
                await ProbeTopologyLevelAsync(ffmpeg, request, level, token);
                var throughput = level / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                lastStable = level;
                job.SetResult(new OcrBenchmarkTelemetry(level, lastStable, 16, _ocr.Status.Workers, _ocr.Status.WorkerKinds, "PASS"));
                job.Log($"Auto Benchmark {level}: {level} Python worker + {level} FFmpeg pipeline PASS · {throughput:0.00} mẫu/s.");
            },
            async (best, token) =>
            {
                job.Set("benchmark", -1, $"OCR Auto Rollback: dựng lại topology ổn định {best}...");
                var restored = await _ocr.ConfigureWorkerPoolAsync(best, token);
                if (restored != best)
                    throw new InvalidOperationException($"Không phục hồi đủ {best} Python worker sau benchmark lỗi; chỉ có {restored}.");
                job.SetResult(new OcrBenchmarkTelemetry(best, best, 16, restored, _ocr.Status.WorkerKinds, "đã quay về"));
            },
            (failed, best, error) =>
                job.Warn($"Auto Benchmark {failed} FAIL: {Compact(error.Message)} · đã quay về {best} pipeline ổn định."),
            cancellationToken);
        job.Set("benchmark", 1.5, $"Benchmark hoàn tất · khóa {selected} pipeline; chuẩn bị bắt đầu quét...");
        return selected;
    }

    private async Task ProbeTopologyLevelAsync(
        string ffmpeg,
        OcrScanRequest request,
        int level,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var actual = await _ocr.ConfigureWorkerPoolAsync(level, timeout.Token);
            if (actual != level)
                throw new InvalidOperationException($"chỉ tạo được {actual}/{level} Python worker");
            var probeResults = await Task.WhenAll(Enumerable.Range(0, level).Select(async index =>
            {
                var at = request.Duration * (index + 0.5) / level;
                var jpeg = await CaptureFrameWithFfmpegAsync(ffmpeg, request.Path, at, request.Region, false, timeout.Token);
                return await _ocr.RunAsync(Convert.ToBase64String(jpeg), timeout.Token);
            }));
            var failed = probeResults.FirstOrDefault(result => !result.Ok);
            if (failed is not null)
                throw new InvalidOperationException(failed.Error ?? "OCR worker trả kết quả lỗi trong benchmark.");
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
        double duration)
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
        job.Set("scanning", percent, $"Đang quét OCR · {lanes.Count} FFmpeg lane · {workers} worker · {percent:0.0}%");
        job.SetResult(new OcrScanTelemetry(
            lanes.Count, active, completedCount, workers, workerKinds, percent, frontier,
            frames.Sum(), images.Sum()));
    }

    private async Task<OcrLaneCheckpoint> RunLaneWithFallbackAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint lane,
        string decoder,
        Action<double, int, int> onProgress,
        AppJob job,
        CancellationToken cancellationToken)
    {
        if (lane.Completed) return lane;
        try
        {
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, decoder == "nvdec", onProgress, job, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (decoder == "nvdec" && error is not OcrRecognitionException)
        {
            job.Log($"Lane {lane.Segment.Index + 1}: NVDEC lỗi, fallback software: {error.Message}");
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, false, onProgress, job, cancellationToken);
        }
    }

    private async Task<OcrLaneCheckpoint> RunLaneAsync(
        string ffmpeg,
        string source,
        OcrScanRequest request,
        OcrScanMode mode,
        OcrLaneCheckpoint saved,
        bool nvdec,
        Action<double, int, int> onProgress,
        AppJob job,
        CancellationToken cancellationToken)
    {
        var segment = saved.Segment;
        var startAt = Math.Clamp(saved.MediaSeconds, segment.ScanStart, segment.ScanEnd);
        var tracker = new SubtitleTracker(mode.Fps, mode.LowConfidence);
        tracker.Restore(saved.Cues, saved.Active);
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
        using var registration = cancellationToken.Register(() => Kill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var reader = new JpegStreamReader(process.StandardOutput.BaseStream);
        var frames = saved.Frames;
        var images = saved.OcrImages;
        var frameIndex = 0;
        var mediaSeconds = startAt;
        var paused = false;
        var stoppedAtBoundary = false;
        try
        {
            while (await reader.ReadAsync(cancellationToken) is { } jpeg)
            {
                var at = startAt + frameIndex / mode.Fps;
                if (at > segment.ScanEnd + 0.001) { stoppedAtBoundary = true; break; }
                frameIndex++;
                frames++;
                images++;
                mediaSeconds = at;
                OcrResult result;
                try
                {
                    result = await _ocr.RunAsync(Convert.ToBase64String(jpeg), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    throw new OcrRecognitionException("OCR worker lỗi: " + error.Message, error);
                }
                if (!result.Ok)
                    throw new OcrRecognitionException(result.Error ?? "OCR worker trả kết quả lỗi.");
                tracker.Observe(at, result);
                onProgress(at, frames, images);
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
            tracker.Finish(segment.ScanEnd);
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

    private static IReadOnlyList<OcrCue> Reconcile(IReadOnlyList<OcrLaneCheckpoint> lanes, out int merges)
    {
        var owned = new List<OcrCue>();
        foreach (var lane in lanes)
        {
            var isLast = lane.Segment.Index == lanes.Count - 1;
            foreach (var cue in lane.Cues)
            {
                if (cue.Start < lane.Segment.CoreStart || (!isLast && cue.Start >= lane.Segment.CoreEnd) || (isLast && cue.Start > lane.Segment.CoreEnd)) continue;
                if (ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text)) owned.Add(cue with { Text = text });
            }
        }
        owned.Sort((a, b) => a.Start.CompareTo(b.Start));
        var output = new List<OcrCue>();
        merges = 0;
        foreach (var cue in owned)
        {
            if (output.Count > 0 && cue.Start <= output[^1].End + 0.25 && Similarity(output[^1].Text, cue.Text) >= 0.82)
            {
                output[^1] = output[^1] with { End = Math.Max(output[^1].End, cue.End), Confidence = Math.Max(output[^1].Confidence, cue.Confidence) };
                merges++;
            }
            else output.Add(cue);
        }
        return output;
    }

    private async Task<byte[]> CaptureFrameAsync(string path, double at, OcrRegion region, bool enhanced, CancellationToken cancellationToken)
    {
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);
        return await CaptureFrameWithFfmpegAsync(ffmpeg, path, at, region, enhanced, cancellationToken);
    }

    private async Task<byte[]> CaptureFrameWithFfmpegAsync(
        string ffmpeg,
        string path,
        double at,
        OcrRegion region,
        bool enhanced,
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
        ], cancellationToken);
    }

    private async Task<bool> ProbeNvdecAsync(string ffmpeg, string source, OcrRegion region, OcrScanMode mode, CancellationToken cancellationToken)
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
            var result = await _processes.RunAsync(ffmpeg, args, timeout.Token);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static IReadOnlyList<string> BuildLaneArguments(string source, OcrRegion region, OcrScanMode mode, double start, double end, bool nvdec)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (nvdec) args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-hwaccel_device", "0"]);
        if (start > 0) args.AddRange(["-ss", start.ToString("0.000", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", source]);
        if (end > start) args.AddRange(["-t", (end - start).ToString("0.000", CultureInfo.InvariantCulture)]);
        var crop = string.Format(CultureInfo.InvariantCulture,
            "crop=iw*{0}:ih*{1}:iw*{2}:ih*{3},scale=1280:320:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad=1280:320:(ow-iw)/2:(oh-ih)/2:black",
            region.Width, region.Height, region.X, region.Y);
        var fps = mode.Fps.ToString("0.######", CultureInfo.InvariantCulture);
        var filter = nvdec
            ? $"hwdownload,format=nv12|p010le|p016le,fps={fps},{crop}"
            : $"fps={fps},{crop}";
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

    private static string FormatClock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    private static string Compact(string value)
    {
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 300 ? text[..300] + "…" : text;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    private sealed class OcrRecognitionException(string message, Exception? inner = null) : Exception(message, inner);

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

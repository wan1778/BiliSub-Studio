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
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;
    private readonly OcrManager _ocr;
    private readonly HardwareService _hardware;
    private readonly OcrCheckpointStore _checkpoints;
    private readonly OcrSidecarStore _sidecars;
    private OwnedProcessGroup? _activeProcesses;
    // Probe/sidecar cache app-lifetime: metadata video tĩnh, key = vân file
    // (đổi file là miss). Null chỉ cache memory (lỗi thoáng không poison disk).
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

    // Scope vét batch của scan đang chạy (ambient theo async-flow: lane/canary/audit
    // đều chảy từ RunCoreAsync nên thấy; scan khác không thấy). Null = đi đường cũ.
    private static readonly AsyncLocal<OcrManager.OcrBatchScope?> _ambientBatchScope = new();

    private async Task<OcrScanResult> RunCoreAsync(
        AppJob job,
        OcrScanRequest request,
        OcrScanRequest keyRequest,
        OcrScanStartMode startMode,
        OwnedProcessGroup processes)
    {
        // Scope vét batch theo scan (cap=1 lúc này = đường cũ y hệt; mở 8 sau parity).
        // Dispose cuối scan (sau lane). finally dọn ambient để scan sau không thấy scope chết.
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
        // Sidecar identity: vân file cho probe/VAD/pxv cache (đổi file là miss).
        // Doc sidecar cập nhật dần trong scan (probe → VAD → pxv), lưu best-effort.
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
            // Lọc thời gian: Fresh = chấm lại từ đầu — xóa journal cũ (verdict
            // của lần trước/logic cũ không được dedup chặn chấm lại).
            try { File.Delete(OcrVerdictJournal.JournalPath(source, string.Empty)); } catch { }
        }
        var saved = startMode == OcrScanStartMode.Resume
            ? await _checkpoints.LoadAsync(keyRequest, token)
            : null;
        if (startMode == OcrScanStartMode.Resume && saved is null)
            throw new InvalidOperationException("Không còn checkpoint OCR phù hợp để tiếp tục. Hãy Quét từ đầu.");

        // Tai nghe Whisper đã bỏ khỏi đường quét (22/09: tốn ~1 tiếng/phim mà
        // chỉ bỏ được ~2% lượt đọc). Quét thuần bằng mắt; các chỗ ăn theo
        // (gộp trùng, banner, judge, cảnh báo sót) tự chạy luật mắt.
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
        if (configuredWorkers != OcrTopologyBenchmark.WorkerTarget(_ocr.Status.ActiveMode, selected))
            throw new InvalidDataException($"OCR topology chưa khóa đúng: {selected} FFmpeg lane nhưng có {configuredWorkers} Python worker.");
        var checkpoint = saved ?? (await _checkpoints.NewAsync(keyRequest, selected, token)) with
        {
            Workers = configuredWorkers,
            WorkerMode = _ocr.Status.ActiveMode,
        };
        selected = checkpoint.SelectedParallelism;
        if (checkpoint.Lanes.Count != selected)
            throw new InvalidDataException($"OCR checkpoint topology không hợp lệ: {checkpoint.Lanes.Count}/{selected} lane.");
        // Tự nhận diện kiểu sub (16/09, khỏi gạt tay): chụp khung cách đều, cắt
        // đúng ROI, worker đo vành chữ -> vote mức lọc cho CẢ lượt quét. Mọi lỗi
        // -> "off" (y hệt cũ, không mất chữ). Mỗi lượt GÁN LẠI từ đầu nên không
        // rò sang lượt khác.
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
        // E.5 VAD NỀN (vụ sidecar chặn phiên 20:34 15/9): decode audio KHÔNG chặn lane nữa.
        // Miss sidecar -> decode chạy nền song song, lane chạy trước với vad=null
        // (vote=Suspicious = đúng luật thiếu-tín-hiệu, council không skip khi chưa có tai);
        // xong tự gắn vào council + lưu sidecar. Hit sidecar -> dùng ngay như cũ.
        // Hủy scan -> nền tự chết theo token, không níu. Không tiếng -> nghỉ (E.0 đã báo).
        VadTimeline? vadSnapshot = null;
        Func<VadTimeline?> vadSource = () => Volatile.Read(ref vadSnapshot);
        // Không tai nghe: sidecar/energy như cũ (timeline không có cờ HasSub
        // nên council skip tắt sẵn — quét thuần bằng mắt).
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
                            // Best-effort: copy Pixel hiện tại để không chà đè verdict pxv của phiên.
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
            ? $"OCR Commit: benchmark xong, khóa {selected} pipeline = {selected} FFmpeg lane + {configuredWorkers} Python worker ({configuredWorkerKinds})."
            : $"Resume checkpoint schema {checkpoint.Schema}: giữ topology {selected} = {selected} FFmpeg lane + {configuredWorkers} Python worker ({configuredWorkerKinds}).");
        var decoder = await ProbeNvdecAsync(ffmpeg, source, request.Region, mode, processes, token) ? "nvdec" : "software";
        job.Log(decoder == "nvdec" ? "Decoder: NVIDIA NVDEC." : "Decoder: software fallback.");
        // Diagnostic baselines captured after the topology lock: the benchmark
        // phase above also flows through the same manager/queue counters.
        var telemetry = new OcrScanTelemetryCounters();
        telemetry.Touch();
        // Sidecar hits xảy ra TRƯỚC khi telemetry ra đời (padding/VAD): dồn vào đây.
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
        // Live incremental reconcile: tail committed cues được pixel-gate ngay trong lúc scan,
        // preview thay tail sạch không đợi Pause. Final ReconcileAsync bên dưới vẫn chạy safety pass.
        // Khai báo trước RunGuardedLaneAsync để local function capture hợp lệ.
        var liveWindow = await ComputeContinuationWindowAsync(_tools, _processes, source, token);
        // Pixel cache share live -> final trong CÙNG một scan session (Task 1 perf gate):
        // một ProductionPixelComparer instance duy nhất cho cả liveTail.Start và final
        // ReconcileAsync. _comparisonCache là instance field nên final reuse kết quả live đã đo
        // (cùng key: video path + SourcePts A/B + shared crop) thay vì decode lại bằng FFmpeg.
        // Lifetime = method scope: tạo khi session bắt đầu, hết scope khi session kết thúc/hủy,
        // scan mới/video khác luôn có instance (cache) mới — không leak xuyên video.
        // Không static/global. Key identity giữ nguyên, không nới lỏng.
        var sharedPixelComparer = new ProductionPixelComparer(_tools, _processes, _activeProcesses);
        // Sidecar pxv: nạp verdict pixel scan trước (key chuẩn hóa theo vân file, xem OcrSidecar).
        // Miss là bình thường (scan đầu/đổi ROI) — đường tính lại giữ nguyên bên dưới.
        // Seed CHỈ thêm hit, không xóa/sửa gì của session này.
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
        // Lọc thời gian (17/09) nằm TRONG pipeline OCR: luôn chạy, KHÔNG toggle,
        // KHÔNG key, KHÔNG AI. Sub cứng thật ĐỨNG YÊN qua nhiều frame (vị trí
        // cố định), chữ trong video (trang sách/bối cảnh) DI CHUYỂN theo camera
        // -> chụp 3 khung, OCR lại, so vị trí -> keep/drop. Journal file RIÊNG
        // (verdict VLM cũ không được nạp — dedup sẽ chặn chấm lại). Dispose cuối
        // scan + finally của await using cho mọi đường thoát.
        var temporalJournal = OcrVerdictJournal.JournalPath(source, string.Empty);
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
        // Tốc độ thật cho vụ số giả .5x: theo dõi trung bình + 5 phút + kẹt lane.
        // Diagnostic thuần túy, không đụng scheduling/tracker/checkpoint nên resume-safe.
        // Resume-base (vụ panel 281×): progress gieo sẵn checkpoint cũ nhưng đồng hồ bấm
        // lại từ 0, nên tracker trừ mốc đầu phiên — mọi tốc độ là PHẦN LÀM THÊM / giờ chạy thêm.
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

        // Sidecar pxv dump: verdict pixel session này -> file đệm cạnh video để scan sau
        // (finish/export/mở-checkpoint) khỏi decode lại frame. Best-effort TUYỆT ĐỐI:
        // mọi lỗi nuốt lặng bên trong, không bao giờ giết lane hay checkpoint.
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
                                    // Trigger live tail reconcile đúng khi tracker commit boundary mới,
                                    // không phải mỗi frame. Worker serialize + revision-guard ở trong.
                                    if (owned.Count > 0) liveTail.AppendCommitted(owned);
                                    // Chấm nền: chỉ cue đã chốt mới vào hàng (câu live đang mở không chấm).
                                    // TryWrite không block — hàng đầy là bỏ + đếm, lane không đợi.
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
                                        configuredWorkers, configuredWorkerKinds, request.Duration, liveTail, liveActive,
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
                                 // Pool chet han (kenh chet hoac 0 worker song): dung lai toan bo.
                                 // Con lai (meo 1 slot nhung van chay) thi TUYET DOI khong rebuild:
                                 // rebuild retire queue -> huy hang loat viec dang bay cua lane khac
                                 // -> moi lane dot attempt -> rebuild storm (10:40 + 19:00 13/9).
                                 // Slot hong do request-recovery (ReplaceWorkerAsync) tu va lazily.
                                 var restored = await _ocr.ConfigureWorkerPoolAsync(configuredWorkers, token);
                                workerStatus = _ocr.Status;
                                 if (restored != configuredWorkers || !workerStatus.Ready || workerStatus.Workers != configuredWorkers)
                                    throw new InvalidOperationException(
                                         $"Cannot restore OCR: {selected} lanes, {workerStatus.Workers}/{configuredWorkers} workers.");
                            }
                            else if (!workerStatus.Ready || workerStatus.Workers != configuredWorkers)
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
                                // Lane recovery dựng lại committed từ checkpoint an toàn: reset live tail
                                // để reconcile lại từ state đúng, không giữ merge từ attempt hỏng.
                                liveTail.ResetCommitted(liveCommitted);
                            }
                            await SaveRecoverableCheckpointAsync();
                            job.Log(
                                $"OCR lane {lane.Segment.Index + 1} khôi phục PASS tại {FormatClock(restart.MediaSeconds)} · "
                                 + $"topology {selected} lanes / {configuredWorkers} workers; retry lane.");
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
                // user pressed Hủy.
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
                        configuredWorkers, configuredWorkerKinds, request.Duration, liveTail, liveActive,
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
        // Chấm nền: lane xong là hết câu mới — dừng service (ca dở chạy nốt rồi
        // thôi), tóm tắt 1 dòng. Số còn lại hoãn cho lượt sau (nhật ký giữ ca xong).
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
            // Pause-nhanh (vụ chờ "tải song song 2529 frame" 15/9): reconcile pixel hàng nghìn
            // frame là việc của FINISH/export, không phải của tạm dừng. Preview pause = câu
            // lane-owned thô cắt theo frontier (đúng thứ vẫn thấy lúc quét), gộp pixel để dành
            // finish/export/mở-checkpoint. BoundaryMerges giữ số cũ (grep toàn src: không chỗ
            // nào đọc). Checkpoint vẫn đủ lanes để resume; finish về sau tính lại từ đầu.
            job.Set("finalizing", 99.5, "Đang lưu checkpoint tạm dừng · bỏ qua ghép SRT (để dành lúc finish/xuất).");
            mergedAll = BuildPausedPreview(lanes, frontier);
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
            // Guard chữ nền tĩnh (18/09): bù cho việc bỏ drop-im-lặng — chữ
            // banner/watermark đứng yên trong đoạn im phải bị bắt ở đây (ghi
            // .exclude.json như judge drop nên list cuối = SRT theo đường cũ).
            // Chỉ chạy ở finish (không chạy pause: resume-finish tính lại).
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
            // Cảnh báo thiếu sub (18/09): chỉ báo, không sinh — người duyệt
            // kiểm tra tay đoạn có tiếng mà không có sub nào.
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
        LogScanTelemetry(job, telemetry, inferenceBefore, selected, configuredWorkers,
            frames, images, media, started.Elapsed.TotalSeconds, mergedAll.Count(x => x.NeedsReview), initialUnique);
        // Resume-base như live: media gieo sẵn checkpoint cũ, đồng hồ bấm lại từ 0.
        var sessionMedia = Math.Max(0, media - initialUnique);
        var sessionRealtime = started.Elapsed.TotalSeconds > 0 ? sessionMedia / started.Elapsed.TotalSeconds : 0;
        var result = new OcrScanResult(
            merged, frames, images, media, started.Elapsed.TotalSeconds,
            sessionRealtime,
            selected, lanes.Count(x => x.Completed), boundaryMerges, decoder,
            configuredWorkers, configuredWorkerKinds, frontier, mergedAll.Count, paused);

        job.SetResult(result);
        // Dump pixel verdicts cuối phiên vào sidecar (best-effort, không throw):
        // finish/export/mở-checkpoint lần sau khỏi decode lại frame đã verdict.
        await FlushPixelSidecarAsync();
        if (paused)
        {
            job.PauseComplete(pauseMessage);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    public Task<OcrCheckpointInfo> InspectCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.InspectAsync(request, cancellationToken);
    public Task RemoveCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _checkpoints.RemoveAsync(request, cancellationToken);
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
        // Preview live: committed tail đã qua pixel-gate incremental (OcrLiveTailReconciler),
        // active provisional ghép riêng để hiển thị, không thành authoritative. Final Finish/Export
        // dùng ReconcileAsync safety pass + _cues, cùng một collection semantics.
        // PERF (lag cuối scan 15/9): tail lấy TRỰC TIẾP từ reconciler dưới 1 lock
        // (GetTailBelow), không copy toàn bộ _view mỗi 500ms/lane. Kết quả Y HỆT vòng
        // lặp tail cũ (tối đa 120 cue ở/below frontier) nhưng cost hằng số.
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
        // Số thật (vụ số giả .5x và panel 281×): trung bình phiên + tức thời 5 phút
        // gần nhất + lane kẹt. unique gieo sẵn checkpoint cũ nhưng đồng hồ bấm lại từ 0
        // khi resume, nên TRỪ mốc đầu phiên — mọi tốc độ là phần làm thêm / giờ chạy thêm.
        // realtime trung bình phiên che đà chậm dần thì số 5p lột ra; lane đứng yên >3p báo tên.
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

    // Tốc độ thật theo thời gian: giữ mẫu (thời điểm, media ĐÃ TRỪ MỐC ĐẦU PHIÊN) trong
    // cửa sổ 5 phút trượt; instant = delta media / delta giờ. Per-lane: nhớ media + mốc
    // đổi cuối; lane chưa xong mà đứng yên quá StallSeconds thì liệt vào kẹt. Thuần
    // diagnostic, không can thiệp scheduling/tracker/checkpoint.
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
            // Cửa sổ-first: khi scan mới (<10s mẫu) chưa đủ baseline thì instant = average
            // ở call-site (truyền average vào); ở đây trả 0 để báo chưa đủ mẫu.
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

    // Probe cache 2 tầng (memory/app-lifetime + sidecar/disk): metadata video tĩnh nên
    // 8 lane + padding + NVDEC chỉ spawn ffprobe 1 lần/scan (lần đầu), các scan sau đọc
    // sidecar. Trả kèm doc sidecar mới nhất + cờ trúng sidecar (để đếm, không để gate).

    // Cold-start 8 lane đua nhau: cùng miss memory → cùng probe như hôm nay (không tệ hơn),
    // lane đầu xong ghi sidecar, các scan sau trúng. Null chỉ cache memory.
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

    // Chụp khung mẫu đo kiểu sub (tự nhận diện, khỏi gạt tay): 12 khung cách
    // đều trong phạm vi lượt quét, cắt đúng ROI, worker đo vành chữ. Trả
    // "off"|"medium"|"strong"; mọi lỗi -> "off" (y hệt cũ). Best-effort: từng
    // khung hỏng là bỏ qua, đủ <2 khung là thôi.
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
        try
        {
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, decoder == "nvdec", onProgress, job, processes, telemetry, cancellationToken, vadSource);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (decoder == "nvdec" && error is not OcrRecognitionException)
        {
            job.Log($"Lane {lane.Segment.Index + 1}: NVDEC lỗi, fallback software: {error.Message}");
            return await RunLaneAsync(ffmpeg, source, request, mode, lane, false, onProgress, job, processes, telemetry, cancellationToken, vadSource);
        }
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
        // PTS domain: segment/saved.MediaSeconds là media-relative (0-based cho SRT).
        // Lane FFmpeg dùng absolute (media + streamStart) với -seek_timestamp 1 + -copyts.
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
        // PTS domain: showinfo absolute (copyts, không offset). Tracker nhận absolute, tự trừ streamStart ra media cho cue/SRT.
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
        // Council v1 (E.5 enforce): neo blank + đếm skip liên tiếp cho ShouldCouncilSkip.
        // prevBlankAnchored chỉ thành true bằng full-read BLANK (skip giữ nguyên, full-read
        // có chữ/audit/resume làm rớt) — chuỗi skip luôn neo vào 1 lần đọc thật gần nhất.
        long consecutiveSkips = 0;
        bool prevBlankAnchored = false;
        // Council skip: MỞ lại khi timeline từ TAI NGHE Whisper (17/09) — đúng
        // điều kiện "VAD được sửa" mà OcrGateShadow đòi để mở lại (VAD năng
        // lượng báo-im-sai ăn chữ thật nên tắt 16/09; tai nghe phân biệt
        // nói/im thật nên chân VAD hết sai hệ thống). Dấu hiệu: timeline có
        // cờ HasSub (chỉ Whisper sinh ra). Đa-cổng bảo thủ + cap + canary audit
        // + kill-switch khi canary-hit giữ nguyên — skip sai là canary bắt ngay.
        // Không tai (timeline năng lượng/sidecar cũ) -> vẫn TẮT như cũ.
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
            // Đường OCR của lane: có scope vét batch thì qua scope (cap=1 = đường cũ),
            // không có (ngoài scan) thì gọi thẳng manager. Chữ ký/timing đo như cũ.
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

        // Ứng viên 2 luminance SHADOW (spec khóa 16/09): đo chữ ký sáng từng dòng
        // OCR (điểm trắng >=224 + điểm tối <64), đếm would-drop, KHÔNG bỏ dòng nào
        // (EnforceDefault=false). GDI+ decode lỗi -> passthrough. Không throw ra lane.
        // Cổng màu CHROMA (user chốt 18/09: sub luôn TRẮNG, chữ màu loại luôn):
        // chạy TIẾP sau luminance trên kết quả đã lọc, enforce TRỰC TIẾP
        // (EnforceDefault=true) — dòng không điểm trắng + thừa điểm màu thì bỏ,
        // fail-open mọi lỗi decode/box. Chữ trắng trên nền màu (trời xanh) giữ
        // nhờ cổng trắng đứng trước.
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

        // Round-trip mỗi OCR request (queue + IPC + inference): tổng + max (CAS lock-free)
        // + đếm request >=30s. Max lành là điều kiện gate để hạ timeout 90s về sau.
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
        // Quy trách nhiệm VAD: mẫu audit im mà đọc ra chữ thì tai hùa sai (đếm riêng).
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
                // PERF (lag cuối scan): danh sách Cues đầy đủ chỉ được dùng khi
                // checkpointSafe (clone vào recoverableLanes). Lần báo tiến độ thường
                // (500ms/lane) không ai đọc Cues -> gửi rỗng, tránh copy nghìn cue
                // mỗi lần. Đường pause/recovery/final tự lấy tracker.Cues trực tiếp.
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
            // Mẫu skip (blank giả, KHÔNG đọc thật) không được vào tracker và
            // không được làm anchor chuyển cảnh (18/09: ObserveEmpty giả chốt
            // cue đang mở oan + blank giả che biên chữ thật).
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
                // Council SKIP (E.5 enforce): im + đứng yên + neo blank-đọc-thật — KHÔNG gọi
                // Paddle (images không tăng: tiết kiệm thật). Ghi blank rỗng, không boxes
                // (blank không tạo cue nên không cần provenance — comment provenance ở trên).
                // 18/09: blank này là GIẢ (không đọc) nên cấm vào tracker ở dưới —
                // tracker chỉ nghe kết quả đọc thật. Retro cho canary soi; cap chuỗi
                // bằng CouncilMaxSkips trong hàm thuần.
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
                    // Đọc thật phục hồi đè blank (kể cả blank giả của skip) ->
                    // gỡ cờ skip để tracker được nghe kết quả thật ở dưới.
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
                refinementFrames = (candidateIndexes.Count > 0
                    ? CapRefinementIndexes(candidateIndexes, windowLength, maximum: mode.Fps >= 4 ? 4 : 8)
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
                // two native-frame confirmations.
                var denseStableTextNoise = mode.Fps >= 4
                    && sampleResult.Detected
                    && IsDenseAdaptiveVisualNoise(scores);
                var selectedIndexes = denseStableTextNoise
                    ? Array.Empty<int>()
                    : mode.Fps >= 4
                    ? CapRefinementIndexes(candidateIndexes, adaptiveWindow.Count - 1, maximum: 4)
                    : candidateIndexes;
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
            // Tracker chỉ nghe mẫu đọc thật — mẫu skip (blank giả) mà vào đây
            // sẽ chốt cue oan qua ObserveEmpty (18/09).
            if (!skippedSample)
                tracker.Observe(sample.Timing.SourcePts, sample.Timing.PresentationTime, sample.Timing.Duration, sampleResult);
            // E.5 SHADOW + council gate:
            // - VAD vote theo gio media + outcome production -> histogram 2x2; vote im là 1
            //   chân trong ShouldCouncilSkip (C abstain — xem OcrGateShadow).
            // - ROI static zones: CHỈ HỌC khi ROI scan phủ nửa trên khung hình
            //   (LearningEnabledFor); ROI dải-sub đáy thì text lặp nào cũng là thoại lặp
            //   nên tắt học (zones rỗng, wouldfilter 0) thay vì học zone oan.
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
            // (E.5+). Audit khong bao gio giet lane. Reuse tắt hẳn nên trigger thực tế là council.
            if ((reuseEnabled || councilSkipsEnabled) && retroBuffer.Count > 0
                && OcrGateShadow.ShouldTriggerCanary(paddleSinceAudit,
                    Stopwatch.GetElapsedTime(lastFullOcrTicks, Stopwatch.GetTimestamp()).TotalSeconds))
                await RunCanaryAuditAsync();
            // Confirm a new reading/blank with nearby source frames rather than
            // only the next baseline sample, even if the visual probe is quiet.
            if (!tracker.CanCheckpoint)
                pendingVisualFollowups = Math.Max(pendingVisualFollowups, 2);
            // Anchor chuyển cảnh giữ mẫu đọc thật gần nhất — blank giả của
            // skip mà thành anchor sẽ che biên chữ thật ở mẫu sau (18/09).
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

    // Preview thô cho đường pause-nhanh: câu lane-owned (đúng bound ownership của Reconcile:
    // lane thường Start < CoreEnd, lane cuối Start <= CoreEnd, Start >= CoreStart), cắt theo
    // frontier, sắp xếp. Thuần bộ nhớ, KHÔNG FFmpeg/pixel — gộp pixel để dành finish/export/
    // mở-checkpoint. Test trực tiếp qua OcrPausePreviewContract (không cần worker/video thật).
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
        return owned.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToArray();
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

    // Exact-identical window từ native frame rate (mọi mode): gap giải thích được tối đa bởi
    // 3 native frame miss liên tiếp (blink-level) thì candidate; dài hơn là reappearance.
    // Field evidence: blank thật 0.667s tồn tại (27.35-27.9 không caption, 6 OCR blank + mắt thường),
    // balanced 4Hz không phân biệt nổi blank thật với dropout bằng cue data => KHÔNG được mở window
    // theo observation-rate (3/4Hz=0.75s sẽ merge nhầm 2 instances đã chứng minh khác nhau).
    // Touching (gap ~0) vẫn merge qua Epsilon. Probe fail => Epsilon, fail-safe.
    // Dùng chung cho full scan reconcile và checkpoint/range replay để cùng một window production.
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
        // Dọn chữ (17/09): gộp cue kề trùng lặp (flicker OCR chẻ 1 câu) + xóa
        // mảnh vụn (1-2 chữ kề câu dài chứa nó). Thuần logic chữ, chạy cho MỌI
        // đường gọi ReconcileAsync (finish/export/checkpoint) nên list cuối =
        // SRT. KHÔNG sửa chữ đọc sai (cần hiểu ngữ nghĩa) — giữ nguyên.
        var cleaned = OcrTextCleanup.Cleanup(merged);
        // Hồi tố sau-Cleanup (18/09): Cleanup xóa/gộp ở giữa khiến 2 cue kề
        // nhau MỚI mà pixel chưa từng verify (cặp trùng hở 0.067-0.3s không
        // review) — chấm đúng luật pass chính (pixel-gate + review), không
        // đụng spec micro-gap đã ghim.
        var (reverified, reverifyMerges) = await OcrCueReconciler.MergeCleanupAdjacentsAsync(
            merged, cleaned.Cues, videoPath, comparer, cancellationToken, continuationWindow);
        // Tai phân xử cặp trùng khe rộng (21/09): 1 đoạn thoại phủ liên tục qua
        // khe = caption tĩnh bị tracker chẻ oan → gộp; khe im = thoại lặp thật
        // → giữ. Chạy cuối reconcile, trước banner guard/missing-sub.
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
        // Contract coordinate-space: dùng coded W/H (ffprobe width/height), và OCR/extractor đều chạy với -noautorotate
        // để iw/ih là coded (không autorotate theo rotation metadata). Mọi path (OCR crop, bbox provenance,
        // pixel extractor, source crop) thống nhất coded space. Nếu video có rotation=90, cả OCR và comparer
        // đều thấy 1920x1080 coded, không bên nào thấy 1080x1920 rotated.
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
            // Probe thất bại: fallback filter cũ để không chặn scan. Pixel compare sẽ Unavailable vì geometry không chắc chắn.
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
        args.AddRange(["-copyts", "-seek_timestamp", "1"]);
        if (start > 0 || (probe is not null && probe.StartTimeSeconds != 0))
            args.AddRange(["-ss", start.ToString("0.#########", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", source]);
        // start/end đều absolute (media + streamStart), output giữ absolute nhờ -copyts nên -to absolute là đúng.
        // Không dùng -t duration: với -copyts + start>0, -t dừng ngay lập tức (đã chứng minh thực nghiệm: 3 frame rồi empty).
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
        // Không dùng fps filter trước showinfo: fps rewrite PTS (drop/duplicate), provenance sẽ sai.
        // Sampling 4Hz thực hiện ở C# theo PTS (adaptiveWindow/nextAdaptiveSample), giữ nguyên source PTS.
        // Mọi mode hiện tại đều AdaptiveTiming:true, nên luôn đi nhánh giữ PTS này.
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
            + $"lane_restarts={telemetry.LaneRestarts} worker_replacements={replacements} worker_recycles={Delta(inference.PlannedRecycles, inferenceBefore.PlannedRecycles)} "
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
        // Cột chữ cảnh xếp chồng (thư giấy/biển/bảng rơi vào ROI, vụ bức thư
        // "休书" 21/09): thoại thật tối đa 1-2 dòng phụ trên baseline, chữ cảnh
        // xếp cả cột. Có baseline CĂN GIỮA khung 1280 + ≥4 dòng nằm gọn TRÊN
        // đỉnh nó → toàn bộ phần trên là chữ cảnh, loại hết (kể cả dòng sát
        // baseline mà luật farAbove đơn-lẻ không với tới). Chỉ áp cho ROI đáy
        // (tôn-trọng ROI tay vùng trên); thiếu baseline căn giữa → passthrough
        // (fail-open: mực-đen không baseline đã có cổng luminance lo).
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
        // PERF (lag cuối scan 15/9): 8 lane tranh 1 lock cho mỗi ảnh OCR (466k lần/scan)
        // chỉ để đếm unique PTS cho telemetry cuối. ConcurrentDictionary cho Add/Count
        // lock-free, cùng semantics (mỗi SourcePts đếm 1 lần), không còn contention
        // tăng dần theo độ dài scan. Gate vẫn giữ cho PaddleDetMs/RecMs (double+=).
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
        // Checkpoint save cost (diagnostic only): số lần lưu + tổng ms. Save 30s/lần
        // serialize toàn bộ cue (11MB ở pause 24:27) + copy .bak nên càng về cuối càng
        // lâu; log để phát hiện khi save thành nguồn lag mới.
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
        // Stage timings (P1 diagnostics, thuần cộng dồn, không gate hành vi):
        // - ProbeMsTotal: round-trip mỗi probe (queue + IPC + OpenCV).
        // - RequestMsTotal/MaxRequestMs/Over30sRequests: round-trip mỗi OCR request —
        //   phân phối latency để gate việc hạ timeout 90s (chỉ hạ khi max lành << 90s).
        public long ProbeMsTotal;
        public long RequestMsTotal;
        public long MaxRequestMs;
        public long Over30sRequests;
        // Sidecar hits (diagnostic): probe/VAD/pxv xài lại từ file đệm thay vì tính lại.
        public long SidecarProbeHits;
        public long SidecarVadHits;
        public long SidecarPxvHits;
        // E.5 shadow: VAD vote x outcome (2x2) + ROI static zones.
        // CHI GHI CHEP: co VAD khong bao gio du mot minh de skip (luat hoi dong).
        public long VadNoText;
        public long VadNoBlank;
        public long VadSuspText;
        public long VadSuspBlank;
        // Canary quy trách nhiệm: hit mà mẫu audit im (VAD NoSub) thì tai hùa sai.
        public long CanaryVadAttributed;
        public long RoiStaticZones;
        public long RoiLinesWouldFilter;
        // Ứng viên 2 luminance SHADOW: đo mọi dòng, đếm would-drop, không bỏ dòng.
        // CHI GHI CHEP (EnforceDefault=false).
        public long LumFrames;
        public long LumLines;
        public long LumWouldDrop;
        public long LumDecodeFails;
        // Cổng màu chữ 18/09 (user chốt: sub luôn trắng): enforce trực tiếp,
        // đếm dòng đo/bỏ/decode-hỏng.
        public long ChromaFrames;
        public long ChromaLines;
        public long ChromaDropped;
        public long ChromaDecodeFails;
        // Chấm nền VLM lúc quét: ca vào hàng / bỏ qua (trùng/tràn/dispose).
        // Verdict chi tiết (giữ/bỏ/chưa rõ) nằm nhật ký + log tóm tắt cuối scan.
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

        // Cách A: -copyts giữ source clock, showinfo pts_time là timestamp nguồn. Không cộng startAt.
        public FrameTimestampReader(StreamReader standardError) => Completion = PumpAsync(standardError);

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

        private async Task<string> PumpAsync(StreamReader standardError)
        {
            Exception? failure = null;
            var expectedN = 0;
            try
            {
                while (await standardError.ReadLineAsync() is { } line)
                {
                    if (_diagnostic.Length < 48 * 1024) _diagnostic.AppendLine(line);
                    if (line.IndexOf("Parsed_showinfo_", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("config ", StringComparison.Ordinal) >= 0) continue;
                    var match = TimestampPattern.Match(line);
                    if (!match.Success)
                    {
                        // Ancillary showinfo lines (color_range/color_space/...) không phải frame record.
                        // Frame record thật luôn có " n:". Thiếu => fail lane, không shift JPEG↔timestamp.
                        // Bỏ sót một frame record thật sẽ bị phát hiện bởi kiểm tra n-sequence ở record kế tiếp.
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

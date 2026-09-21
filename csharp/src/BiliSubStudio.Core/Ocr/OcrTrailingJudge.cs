using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace BiliSubStudio.Core.Ocr;

// Giám khảo THỜI GIAN đi SAU ocr lúc quét (user chốt 17/09): câu nào chốt là
// chấm ngay bằng logic thời gian — sub cứng thật ĐỨNG YÊN qua nhiều frame
// (vị trí cố định), chữ trong video (trang sách/bối cảnh bị camera lia) DI
// CHUYỂN liên tục:
// - chữ di chuyển giữa các khung probe -> DROP: ghi file .exclude.json ->
//   xóa khỏi SRT lúc xuất. Xóa file đó là hoàn tác.
// - chữ đứng yên -> KEEP: giữ nguyên, qua câu tiếp.
// Quy tắc thép:
// - KHÔNG BAO GIỜ chặn đường quét: enqueue là TryWrite (đầy là bỏ + đếm).
// - Chỉ chấm câu đã CHỐT (committed tail), không chấm câu live đang mở.
// - Ghi xóa TRƯỚC, ghi nhật ký SAU: lưu hỏng thì lần quét sau chấm lại
//   (không mất quyết định âm thầm).
// - Hủy: dừng êm, số còn lại hoãn (lượt sau chấm tiếp).
// - Tắt toggle: dùng bản Disabled (no-op), đường quét y hệt.
// Mở rộng theo lane (8 nay, 16 sau): số luồng chấm = ceil(lanes/4), tối đa 4.
// KHÔNG VLM, KHÔNG server, KHÔNG key — OCR qua worker pool có sẵn.
public sealed class OcrTrailingJudge : IAsyncDisposable
{
    public const int DefaultMaxQueue = 200;
    public const int MaxPerEnqueue = 8;
    public const int MaxWorkers = 4;

    public sealed record JudgeTask(
        double GrabAt, double Start, string Text, double End = 0,
        double Confidence = 0.99, bool NeedsReview = false);

    public sealed record SnapshotStats(
        long Enqueued,
        long Judged,
        long Duplicates,
        long QueueFullDropped,
        long GrabFailed,
        long Deferred,
        long Keeps,
        long Deleted);

    // Co giãn theo lane: 1-4 lane -> 1 luồng, 5-8 -> 2, 9-12 -> 3, 13+ -> 4.
    public static int WorkersForLanes(int laneCount) =>
        Math.Clamp((Math.Max(1, laneCount) + 3) / 4, 1, MaxWorkers);

    private readonly Channel<JudgeTask> _queue;
    private readonly Func<double, CancellationToken, Task<byte[]?>> _grabFrame;
    private readonly Func<IReadOnlyList<byte[]>, CancellationToken, Task<IReadOnlyList<OcrResult>>>? _temporalOcr;
    private readonly Func<OcrCueFix, CancellationToken, Task> _saveFix;
    private readonly Func<OcrCueExclusion, CancellationToken, Task> _saveDrop;
    private readonly IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? _speech;
    private readonly string _model;
    private readonly string _journalPath;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    // Warmup tai-mắt: 10 cue đầu chỉ thống kê độ khớp (không drop theo chữ) để
    // phát hiện video lệch ngôn ngữ (tai-mắt khác nhau toàn bộ -> tắt drop-chữ,
    // tránh xóa oan cả video). Drop-im-lặng đã bỏ 18/09 (sub-không-tiếng là
    // thật) — chữ nền tĩnh do guard banner ở finish bắt.
    private long _asrChecked;
    private long _asrAgreed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _loops;
    private readonly object _sync = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<OcrCueVerdict> _journal = new();
    private bool _journalLoaded;
    private long _enqueued;
    private long _judged;
    private long _duplicates;
    private long _queueFullDropped;
    private long _grabFailed;
    private long _deferred;
    private long _keeps;
    private long _deleted;
    private bool _disposed;

    public bool Enabled { get; }
    public int Workers { get; }

    private OcrTrailingJudge(
        Func<double, CancellationToken, Task<byte[]?>> grabFrame,
        Func<IReadOnlyList<byte[]>, CancellationToken, Task<IReadOnlyList<OcrResult>>>? temporalOcr,
        Func<OcrCueFix, CancellationToken, Task> saveFix,
        Func<OcrCueExclusion, CancellationToken, Task> saveDrop,
        string journalPath,
        string model,
        int maxQueue,
        int workers,
        bool enabled,
        int frameWidth = 0,
        int frameHeight = 0,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech = null)
    {
        ArgumentNullException.ThrowIfNull(grabFrame);
        ArgumentNullException.ThrowIfNull(temporalOcr);
        ArgumentNullException.ThrowIfNull(saveFix);
        ArgumentNullException.ThrowIfNull(saveDrop);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _grabFrame = grabFrame;
        _temporalOcr = temporalOcr;
        _saveFix = saveFix;
        _saveDrop = saveDrop;
        _speech = speech;
        _journalPath = journalPath;
        _model = model;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        Enabled = enabled;
        Workers = Math.Clamp(workers, 1, MaxWorkers);
        _queue = Channel.CreateBounded<JudgeTask>(new BoundedChannelOptions(
            Math.Max(1, maxQueue))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _loops = new Task[Workers];
        for (var index = 0; index < _loops.Length; index++)
            _loops[index] = Task.Run(LoopAsync);
    }

    // Bản TEMPORAL cho tests: queue/journal plumbing với temporal OCR giả.
    public static OcrTrailingJudge CreateTemporal(
        Func<double, CancellationToken, Task<byte[]?>> grabFrame,
        Func<IReadOnlyList<byte[]>, CancellationToken, Task<IReadOnlyList<OcrResult>>> temporalOcr,
        string journalPath,
        int maxQueue = DefaultMaxQueue,
        int workers = 1,
        Func<OcrCueFix, CancellationToken, Task>? saveFix = null,
        Func<OcrCueExclusion, CancellationToken, Task>? saveDrop = null,
        int frameWidth = 1920,
        int frameHeight = 1080,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech = null) =>
        new(grabFrame, temporalOcr,
            saveFix ?? ((_, _) => Task.CompletedTask),
            saveDrop ?? ((_, _) => Task.CompletedTask),
            journalPath, "temporal", maxQueue, workers, enabled: true,
            frameWidth: frameWidth, frameHeight: frameHeight, speech: speech);

    // Bản TEMPORAL live cho scanner (user chốt 17/09): KHÔNG VLM, KHÔNG server,
    // KHÔNG key. Chấm từng ca nghi bằng logic thời gian: chụp 3 khung phủ ca,
    // OCR lại qua worker pool có sẵn, so khớp chữ + vị trí:
    // - chữ DI CHUYỂN giữa các khung (trôi > 5% khung) -> DROP (chữ bối cảnh
    //   chạy theo camera: trang sách/tít qua, không phải sub) -> .exclude.json.
    // - chữ ĐỨNG YÊN (trôi ≤ 5%) -> KEEP (sub thật).
    // frameWidth/Height từ input health (độ phân giải thật) để chuẩn hóa drift.
    public static OcrTrailingJudge CreateTemporalLive(
        BiliSubStudio.Core.Tools.ToolManager tools,
        BiliSubStudio.Core.Processes.ProcessRunner processes,
        OcrManager ocr,
        string videoPath,
        string ffmpegPath,
        string journalPath,
        Func<OcrCueFix, CancellationToken, Task> saveFix,
        Func<OcrCueExclusion, CancellationToken, Task> saveDrop,
        int laneCount,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(saveFix);
        ArgumentNullException.ThrowIfNull(saveDrop);
        async Task<byte[]?> Grab(double seconds, CancellationToken token)
        {
            try
            {
                var bytes = await processes.CaptureBytesAsync(ffmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-nostdin",
                     "-ss", seconds.ToString("0.000", CultureInfo.InvariantCulture),
                     "-i", videoPath, "-map", "0:v:0", "-an", "-sn", "-dn",
                     "-frames:v", "1", "-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1"],
                     token);
                if (bytes is null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) return null;
                return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        // OCR qua worker pool có sẵn (batch 3 khung, trong giới hạn worker 4).
        async Task<IReadOnlyList<OcrResult>> OcrBatch(IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var images = frames.Select(Convert.ToBase64String).ToList();
            return await ocr.RunBatchAsync(images, token);
        }
        var lanes = Math.Max(1, laneCount);
        return new OcrTrailingJudge(
            Grab,
            OcrBatch,
            saveFix,
            saveDrop,
            journalPath,
            "temporal",
            Math.Max(DefaultMaxQueue, lanes * 50),
            WorkersForLanes(lanes),
            enabled: true,
            frameWidth: frameWidth,
            frameHeight: frameHeight,
            speech: speech);
    }

    // Bản tắt: mọi gọi no-op, stats 0. Dùng khi tắt toggle —
    // đường quét không cần rẽ nhánh null.
    public static OcrTrailingJudge Disabled(
        Func<double, CancellationToken, Task<byte[]?>>? grabFrame = null,
        string journalPath = "") =>
        new(grabFrame ?? ((_, _) => Task.FromResult<byte[]?>(null)),
            (_, _) => Task.FromResult<IReadOnlyList<OcrResult>>([]),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            string.IsNullOrWhiteSpace(journalPath) ? "(disabled)" : journalPath,
            "disabled",
            maxQueue: 1,
            workers: 1,
            enabled: false);

    // Chấm TẤT CẢ cue đã chốt (KHÔNG lọc ca nghi kiểu VLM — chữ bối cảnh
    // (tít/trang sách) đọc TIN CẬY nên bộ lọc ca-nghi cũ (dài/garbled/thấp-tin/
    // cờ) bỏ sót đúng ca cần xóa). Không block: đầy hàng là bỏ + đếm.
    // Trả (nhận, bỏ qua) để caller cộng telemetry. Tắt/dispose -> (0,0).
    public (int Accepted, int Skipped) EnqueueCues(IEnumerable<OcrCue> cues)
    {
        if (!Enabled || _disposed) return (0, 0);
        var suspects = new List<OcrCue>();
        foreach (var cue in cues)
        {
            if (cue is null || cue.Text is null) continue;
            if (suspects.Count >= MaxPerEnqueue) break;
            suspects.Add(cue);
        }
        var accepted = 0;
        var skipped = 0;
        foreach (var cue in suspects)
        {
            if (cue is null || cue.Text is null) { skipped++; continue; }
            var duration = cue.End - cue.Start;
            if (!(duration > 0) || !double.IsFinite(duration)) { skipped++; continue; }
            var grabAt = cue.Start + Math.Min(0.25, duration / 2);
            var key = FormattableString.Invariant($"{cue.Start:0.000}|{cue.Text}");
            lock (_sync)
            {
                if (_seen.Contains(key))
                {
                    Interlocked.Increment(ref _duplicates);
                    skipped++;
                    continue;
                }
                var task = new JudgeTask(grabAt, cue.Start, cue.Text, cue.End,
                    cue.Confidence, cue.NeedsReview);
                if (!_queue.Writer.TryWrite(task))
                {
                    Interlocked.Increment(ref _queueFullDropped);
                    skipped++;
                    continue;
                }
                _seen.Add(key);
                Interlocked.Increment(ref _enqueued);
                accepted++;
            }
        }
        return (accepted, skipped);
    }

    public SnapshotStats Snapshot()
    {
        lock (_sync)
        {
            return new SnapshotStats(
                Interlocked.Read(ref _enqueued),
                Interlocked.Read(ref _judged),
                Interlocked.Read(ref _duplicates),
                Interlocked.Read(ref _queueFullDropped),
                Interlocked.Read(ref _grabFailed),
                Interlocked.Read(ref _deferred),
                Interlocked.Read(ref _keeps),
                Interlocked.Read(ref _deleted));
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token))
            {
                while (_queue.Reader.TryRead(out var task) && task is not null)
                {
                    try
                    {
                        await JudgeOneAsync(task);
                    }
                    catch (OperationCanceledException) { return; }
                    catch
                    {
                        // Lưới cuối: loop TUYỆT ĐỐI không chết lặng — bỏ task,
                        // chạy tiếp. Phát hiện bằng stats (judged đứng yên
                        // trong khi enqueue tăng).
                        Interlocked.Increment(ref _deferred);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void EnsureJournalLoaded()
    {
        lock (_sync)
        {
            if (_journalLoaded) return;
            _journalLoaded = true;
            try
            {
                foreach (var entry in OcrVerdictJournal.Load(_journalPath))
                    _journal.Add(entry);
            }
            catch { }
        }
    }

    private void SaveJournal()
    {
        try
        {
            OcrVerdictJournal.Save(_journalPath, _journal);
        }
        catch { }
    }

    private async Task JudgeOneAsync(JudgeTask task)
    {
        EnsureJournalLoaded();
        lock (_sync)
        {
            if (OcrVerdictJournal.HasVerdict(_journal, task.Start, task.Text))
            {
                Interlocked.Increment(ref _duplicates);
                return;
            }
        }
        // TEMPORAL (user chốt 17/09): đường chấm DUY NHẤT — chụp 3 khung phủ ca,
        // OCR lại qua worker pool, so khớp chữ + vị trí -> keep/drop.
        await JudgeOneTemporalAsync(task);
    }

    // Chấm 1 ca bằng logic thời gian (user chốt 17/09): chụp 3 khung phủ ca
    // (rìa ±0.3s khuếch đại drift), OCR batch, so khớp chữ + vị trí:
    // - DI CHUYỂN (trôi > 5% khung) -> DROP (ghi .exclude.json, xóa khỏi SRT).
    // - ĐỨNG YÊN -> KEEP (sub thật, qua câu tiếp).
    // Fail-open: không đủ khung/OCR lỗi -> KEEP (không drop oan), chỉ đếm grab fail.
    private async Task JudgeOneTemporalAsync(JudgeTask task)
    {
        var temporalOcr = _temporalOcr;
        if (temporalOcr is null) return;
        // Đối chiếu tai-mắt (Whisper, 18/09 — luật chung user chốt): tai nghe gì
        // vs mắt thấy gì. IM LẶNG THÌ GIỮ, NÓI KHÁC HẲN MỚI XÓA:
        // - KHÔNG còn drop khi thiếu thoại chồng lấn: đoạn sub-không-tiếng là
        //   thật với video user (im lặng -> để mắt quyết qua probe bên dưới).
        //   Chữ nền tĩnh trong đoạn im do guard banner ở finish bắt
        //   (OcrBannerGuard), không phải việc của judge.
        // - Tai nghe khác hẳn -> DROP, nhưng "khác hẳn" phải là MÂU THUẪN DƯƠNG
        //   TÍNH: sim < 0.3 + sau warmup + cue KHÔNG nằm nguyên văn trong tai +
        //   tai PHỦ KÍN cue bằng chữ tự tin. Tai thưa/không phủ (khe im, tai sót)
        //   thì không đủ bằng chứng — để lọc vị trí quyết, tuyệt đối không drop
        //   vì "tai không nghe thấy". Ngoại lệ cứu tai-trễ: tai nói cùng nội
        //   dung trong ±1.5s (sub hiện trễ/tracker tách) thì không mâu thuẫn.
        // - Tai nghe khớp mạnh (≥ 0.6) -> kiểm tra cắt đuôi thừa rồi KEEP sớm
        //   (khỏi chụp khung).
        // - Còn lại (gồm cả không có thoại chồng lấn) -> để lọc vị trí quyết.
        //   Không bao giờ sửa chữ ở đây (fix chỉ ở fallback/trim bên dưới,
        //   có guard riêng).
        var speech = _speech;
        var asrSim = -1.0;
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment> overlapping = [];
        if (speech is { Count: > 0 })
        {
            overlapping = OcrSpeechMatch.OverlappingSegments(speech, task.Start, task.End);
            if (overlapping.Count > 0)
            {
                asrSim = OcrSpeechMatch.MaxSimilarity(task.Text, overlapping);
                Interlocked.Increment(ref _asrChecked);
                if (asrSim >= OcrSpeechMatch.MinSimilarity)
                    Interlocked.Increment(ref _asrAgreed);
                if (asrSim >= OcrSpeechMatch.StrongSimilarity)
                {
                    if (await TryTrimByEarAsync(task, asrSim))
                        return;
                    await ApplyVerdictAsync(task, "keep",
                        $"tai-mắt khớp ({asrSim:0.00}) — thoại thật");
                    return;
                }
                // Mâu thuẫn dương tính (luật chung 18/09): sim thấp + cue KHÔNG nằm
                // nguyên văn trong tai + tai PHỦ KÍN cue bằng chữ tự tin mới được
                // buộc tội. Trước khi buộc tội phải cứu tai-trễ: sub hiện trễ
                // hoặc tracker tách cue khiến tai nói cùng nội dung nằm ngoài
                // ±0.3s (field 2408.1: tai nói xong trước 0.75s mà cue mới hiện)
                // — tai nói cùng nội dung trong ±1.5s thì KHÔNG mâu thuẫn, để
                // probe mắt quyết. Im lặng/thưa thì giữ (không đủ bằng chứng).
                var contradicted = false;
                if (asrSim < OcrSpeechMatch.MinSimilarity && SpeechTextCheckReady()
                    && !OcrSpeechMatch.IsVerbatimContained(task.Text, overlapping))
                {
                    var wide = OcrSpeechMatch.OverlappingSegments(speech,
                        task.Start, task.End, OcrSpeechMatch.MismatchOverlapPadSeconds);
                    contradicted =
                        OcrSpeechMatch.MaxSimilarity(task.Text, wide) < OcrSpeechMatch.MinSimilarity
                        && !OcrSpeechMatch.IsVerbatimContained(task.Text, wide)
                        && OcrSpeechMatch.EarCoversSpan(task.Start, task.End,
                            CollectNarrowWords(speech, task.Start, task.End));
                }
                if (contradicted)
                {
                    await ApplyDropAsync(task,
                        $"tai nghe khác hẳn (giống {asrSim:0.00}) — chữ cảnh, không phải thoại");
                    return;
                }
            }
        }
        // Tít/credit (chức danh/ngày tháng/bản quyền) — drop KHÔNG cần chụp
        // khung, NHƯNG chỉ khi tai KHÔNG nói chữ đó (18/09: lùi sau tai-nghe;
        // trước đây drop ngay đầu đã xóa oan thoại xưng hô "导演，你听我说",
        // nhân vật nói ngày tháng, thoại nhắc 版权). Credit phim thật chiếu
        // trên nền im lặng hoặc nền thoại khác (tai không nói) -> vẫn drop.
        // Tai có nói (khớp mạnh hoặc nguyên văn) -> rơi xuống probe mắt quyết.
        if (OcrTemporalFilter.IsCreditTitle(task.Text))
        {
            var spoken = overlapping.Count > 0
                && (asrSim >= OcrSpeechMatch.StrongSimilarity
                    || OcrSpeechMatch.IsVerbatimContained(task.Text, overlapping));
            if (!spoken)
            {
                await ApplyDropAsync(task, "tít/credit (chức danh/ngày tháng/bản quyền), không phải thoại");
                return;
            }
        }
        // Tai PHÁT HIỆN 1 chữ mắt đọc sai (video Cả Lớp 19/09 + replay Ăn Mày:
        // 21 divergences tai-mắt căn giờ khớp, nhưng đối chiếu sub gốc chỉ 2
        // là đọc sai thật, 19 là thoại paraphrase cố ý trên màn hình) — máy
        // KHÔNG thể phân biệt đọc-sai/paraphrase offline, và sửa chữ mắt theo
        // tai sẽ PHÁ sub paraphrase (đổi chữ đúng thành chữ tai). Nên: KHÔNG
        // sửa text (tai không sửa chữ — cùng luật "tai không đẻ chữ"), chỉ gắn
        // cờ review qua fix giữ-nguyên-chữ, lý do mang gợi ý để người duyệt
        // quyết (hiện ở tooltip ⚠️ trên list).
        if (overlapping.Count > 0 && SpeechTextCheckReady())
        {
            var corrected = OcrSpeechMatch.TryCorrectSingleChar(
                task.Text, overlapping,
                CollectNarrowWords(speech, task.Start, task.End));
            if (!string.IsNullOrWhiteSpace(corrected)
                && !string.Equals(corrected, task.Text, StringComparison.Ordinal)
                && corrected.EnumerateRunes().Count() == task.Text.EnumerateRunes().Count()
                && SingleCharSuggestion(task.Text, corrected) is { } suggestion)
            {
                await ApplyFixAsync(task, task.Text,
                    $"chữ [{suggestion.Old}] tai nghe [{suggestion.New}] — kiểm tra tay",
                    flagReview: true);
                return;
            }
        }
        // LUÔN dùng window RỘNG phủ ngoài ca (±1s): ca ngắn 0.27s mà chỉ quét
        // trong ca thì window 0.16s — drift không bao giờ vượt ngưỡng (bug đã
        // sửa 17/09). Khớp chữ theo đúng CHỮ CA nên không dính sub kế (chữ
        // khác không match); cùng chữ kéo dài (sub dài tách ca) thì cùng vị
        // trí -> keep đúng.
        var duration = Math.Max(0, task.End - task.Start);
        var mid = task.Start + duration * 0.5;
        double[] times =
        [
            Math.Max(0, task.Start - OcrTemporalFilter.EdgePadSeconds),
            mid,
            task.End + OcrTemporalFilter.EdgePadSeconds,
        ];
        var frames = new List<byte[]?>(times.Length);
        foreach (var time in times)
        {
            byte[]? frame = null;
            for (var attempt = 0; attempt < 3 && frame is null; attempt++)
            {
                try { frame = await _grabFrame(time, _stop.Token); }
                // Hủy giữa chừng = ca chưa chấm xong -> hoãn cho lượt sau
                // (đường VLM cũ cũng cộng deferred, không nuốt lặng).
                catch (OperationCanceledException) { Interlocked.Increment(ref _deferred); return; }
                catch { }
            }
            frames.Add(frame);
        }
        var grabbed = frames.Count(frame => frame is { Length: > 0 });
        if (grabbed == 0)
        {
            Interlocked.Increment(ref _grabFailed);
            return;
        }
        // OCR từng khung có ảnh; khung lỗi -> null (OcrTemporalFilter bỏ qua, fail-open).
        var ocrFrames = new List<OcrResult?>(times.Length);
        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index] is not { Length: > 0 } frame)
            {
                ocrFrames.Add(null);
                continue;
            }
            try
            {
                var results = await temporalOcr([frame], _stop.Token);
                ocrFrames.Add(results.Count == 1 ? results[0] : null);
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref _deferred); return; }
            catch
            {
                ocrFrames.Add(null);
                Interlocked.Increment(ref _deferred);
            }
        }
        var verdict = OcrTemporalFilter.Decide(
            ocrFrames, times, task.Text, _frameWidth, _frameHeight);
        if (verdict.Decision == "drop")
        {
            await ApplyVerdictAsync(task, "drop", verdict.Reason);
            return;
        }
        // Fallback đọc lại (17/09): ca đáng ngờ (⚠️/tin thấp) mà chữ ĐỨNG YÊN
        // nhưng có thể bị artifact đè (vệt sáng laser di chuyển theo frame) ->
        // đọc lại khung lân cận SÁT trong ca, chọn bản tin cao nhất. Chỉ FIX
        // khi bản mới CHẮC CHẮN hơn hẳn (≥0.88 và hơn ≥0.08) và khác chữ —
        // không bao giờ "sửa" chữ đúng thành chữ sai.
        if (task.NeedsReview || task.Confidence < OcrTemporalFilter.FallbackTriggerConfidence)
        {
            if (await TryFixFromNeighborsAsync(task, mid, duration))
                return;
            if (await TryTrimByEarAsync(task, asrSim, peelAllowed: true))
                return;
        }
        else
        {
            // Cue tin cao probe-giữ (18/09): chỉ thử cắt rìa (rẻ, thuần logic),
            // KHÔNG đọc lại frame lân cận, KHÔNG peel tường (nặng). Áp dụng
            // khi nhát cắt số/dấu/latin (ShouldApplyTrimFix) — chữ Hán tin cao
            // và dấu câu thuần không bao giờ bị đụng ở đây.
            if (await TryTrimByEarAsync(task, asrSim, peelAllowed: false))
                return;
        }
        // Chữ cảnh nghi vấn (video Cả Lớp 19/09: sổ tay/biển/tường tai không hề
        // nói — frame 731/347/12410): probe đã giữ (đứng yên) mà cue cách MỌI
        // đoạn thoại > 1.5s thì gắn cờ review qua fix giữ-nguyên-chữ (fix
        // OldText==NewText + FlagReview; export/list hiện ⚠️). GIỮ cue, không
        // xóa (quyết xóa 18/09 thuộc về banner/user). Cần tai tồn tại + qua
        // warmup (tai điếc/lệch ngôn ngữ thì mọi cue đều "im" — cấm gắn cờ
        // hàng loạt). Đặt SAU probe để không chặn đường drift-drop.
        if (speech is { Count: > 0 } && SpeechTextCheckReady()
            && OcrSpeechMatch.SilenceGap(task.Start, task.End, speech) > OcrSpeechMatch.SilenceFlagGapSeconds)
        {
            await ApplyFixAsync(task, task.Text, "tai im hoàn toàn (±1.5s không tiếng) — chữ cảnh nghi vấn", flagReview: true);
            return;
        }
        await ApplyVerdictAsync(task, "keep", verdict.Reason);
    }

    private async Task<bool> TryFixFromNeighborsAsync(JudgeTask task, double mid, double duration)
    {
        var temporalOcr = _temporalOcr;
        if (temporalOcr is null) return false;
        // Khung lân cận SÁT trong ca (artifact di chuyển theo frame, sub đứng
        // yên): ±min(0.2s, 25% duration), kẹp ≥ 0. Khác wide probe (±1s ngoài
        // ca, sub khác nhau) — ở đây cần CÙNG sub đọc khác.
        var delta = Math.Clamp(duration * 0.25, 0.05, 0.2);
        double[] times = [Math.Max(0, mid - delta), mid + delta];
        (string Text, double Confidence)? best = null;
        foreach (var time in times)
        {
            byte[]? frame = null;
            for (var attempt = 0; attempt < 2 && frame is null; attempt++)
            {
                try { frame = await _grabFrame(time, _stop.Token); }
                catch (OperationCanceledException) { return false; }
                catch { }
            }
            if (frame is not { Length: > 0 }) continue;
            IReadOnlyList<OcrResult> results;
            try { results = await temporalOcr([frame], _stop.Token); }
            catch (OperationCanceledException) { return false; }
            catch { continue; }
            if (results.Count != 1) continue;
            var candidate = OcrTemporalFilter.FindBestFixMatch(results[0].Lines, task.Text);
            if (candidate is null) continue;
            if (best is null || candidate.Value.Confidence > best.Value.Confidence)
                best = candidate;
        }
        if (best is null) return false;
        var (text, confidence) = best.Value;
        if (confidence < OcrTemporalFilter.FixMinConfidence) return false;
        if (confidence < task.Confidence + OcrTemporalFilter.FixMargin) return false;
        return await ApplyFixAsync(task, text,
            $"đọc lại khung lân cận rõ hơn (tin {confidence:0.00} > {task.Confidence:0.00})");
    }

    // Words tai trong cửa sổ hẹp cue ± OverlapPadSeconds (cùng cửa sổ trim
    // dùng): nguyên liệu cho EarCoversSpan ở đường drop-mismatch.
    private static List<BiliSubStudio.Core.Editor.EditorWordTiming> CollectNarrowWords(
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        double cueStart, double cueEnd)
    {
        var words = new List<BiliSubStudio.Core.Editor.EditorWordTiming>();
        if (speech is null) return words;
        var from = cueStart - OcrSpeechMatch.OverlapPadSeconds;
        var to = cueEnd + OcrSpeechMatch.OverlapPadSeconds;
        foreach (var segment in speech)
        {
            if (segment?.Words is null) continue;
            foreach (var word in segment.Words)
            {
                if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
                if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
                if (word.End >= from && word.Start <= to) words.Add(word);
            }
        }
        return words;
    }

    // Cắt đầu/đuôi thừa theo tai nghe (17/09): chữ OCR dính thêm chữ cảnh ở
    // rìa mà tai nghe không có counterpart -> cắt, giữ phần khớp.
    // peelAllowed=false (cue tin cao, 18/09): chỉ thử cắt rìa (rẻ), bỏ qua gỡ
    // tường lặp (nặng, dành cho ca đáng ngờ). Áp dụng fix qua cổng chung
    // ShouldApplyTrimFix (strong/review/tin-thấp, hoặc nhát cắt số/dấu/latin).
    // Thuần logic + tai nghe, KHÔNG đoán chữ mới (an toàn hơn sửa chữ).
    private async Task<bool> TryTrimByEarAsync(JudgeTask task, double asrSim, bool peelAllowed = true)
    {
        var speech = _speech;
        if (speech is null || speech.Count == 0) return false;
        // Chỉ cắt sau warmup (chống cắt oan video lệch ngôn ngữ/tai điếc —
        // cùng cổng với drop-chữ).
        if (!SpeechTextCheckReady()) return false;
        var from = task.Start - OcrSpeechMatch.OverlapPadSeconds;
        var to = task.End + OcrSpeechMatch.OverlapPadSeconds;
        var words = new List<BiliSubStudio.Core.Editor.EditorWordTiming>();
        // Chữ tai LÂN CẬN RỘNG làm veto cho TrimEdges (bug cue 36 没有之一
        // 18/09): từ thật có thể nằm ngoài cửa sổ words hẹp do lệch timing
        // sub-vs-tiếng — chữ định cắt mà tai có nói gần đó thì cấm cắt.
        var nearbyHeard = new System.Text.StringBuilder();
        var vetoFrom = task.Start - OcrSpeechMatch.TrimNearbyVetoSeconds;
        var vetoTo = task.End + OcrSpeechMatch.TrimNearbyVetoSeconds;
        foreach (var segment in speech
            .OrderBy(segment => segment?.Start ?? double.PositiveInfinity))
        {
            if (segment?.Words is null) continue;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End)) continue;
            if (segment.End >= vetoFrom && segment.Start <= vetoTo
                && !string.IsNullOrWhiteSpace(segment.Text))
                nearbyHeard.Append(segment.Text);
            foreach (var word in segment.Words)
            {
                if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
                if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
                if (word.End >= from && word.Start <= to) words.Add(word);
            }
        }
        if (words.Count == 0) return false;
        var nearbySpoken = nearbyHeard.ToString();
        var trimmed = OcrSpeechMatch.TrimEdges(
            task.Text, task.Start, task.End, words, nearbySpoken);
        if (!string.IsNullOrWhiteSpace(trimmed)
            && !string.Equals(trimmed, task.Text, StringComparison.Ordinal)
            && OcrSpeechMatch.ShouldApplyTrimFix(
                asrSim >= OcrSpeechMatch.StrongSimilarity,
                task.NeedsReview, task.Confidence, task.Text, trimmed))
            return await ApplyFixAsync(task, trimmed, "cắt đầu/đuôi thừa theo tai nghe");
        if (!peelAllowed) return false;
        // Trim rìa không với tới tường lặp (chiếm đa số cue) -> thử gỡ tường.
        return await TryPeelRepeatTilesAsync(task, words, nearbySpoken);
    }

    // Gỡ tường chữ lặp nền (danmaku 陪一支 18/09, cue 5348): tile lặp mà tai
    // không hề nói thì gỡ, giữ lõi tai có nói. Chạy SAU TrimEdges (rác rìa ít
    // chữ) — tường lặp chiếm đa số cue nên TrimEdges không với tới. Cùng cổng
    // warmup + guards tai, KHÔNG đoán chữ mới.
    private async Task<bool> TryPeelRepeatTilesAsync(
        JudgeTask task,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorWordTiming> words,
        string nearbySpoken)
    {
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            task.Text, task.Start, task.End, words, nearbySpoken);
        if (string.IsNullOrWhiteSpace(peeled)) return false;
        if (string.Equals(peeled, task.Text, StringComparison.Ordinal)) return false;
        return await ApplyFixAsync(task, peeled, "gỡ chữ lặp nền theo tai nghe");
    }

    // Ghi fix TRƯỚC, nhật ký SAU (lưu hỏng thì lần sau chấm lại).
    private async Task<bool> ApplyFixAsync(JudgeTask task, string fixedText, string reason, bool flagReview = false)
    {
        try
        {
            await _saveFix(new OcrCueFix(task.Start, task.Text, fixedText, flagReview), _stop.Token);
        }
        catch (OperationCanceledException) { return false; }
        catch { Interlocked.Increment(ref _deferred); return false; }
        lock (_sync)
        {
            _journal.Add(new OcrCueVerdict(task.Start, task.Text, "fix", reason,
                _model, DateTime.UtcNow.ToString("o"), FixedText: fixedText));
            Interlocked.Increment(ref _keeps);
            Interlocked.Increment(ref _judged);
            SaveJournal();
        }
        return true;
    }

    // Vị trí chữ khác nhau duy nhất giữa 2 text cùng độ dài rune (kèm chữ
    // cũ/mới) — phục vụ gợi ý review. Null khi không đúng 1 điểm khác.
    private static (string Old, string New)? SingleCharSuggestion(string from, string to)
    {
        var left = from.EnumerateRunes().ToArray();
        var right = to.EnumerateRunes().ToArray();
        if (left.Length != right.Length || left.Length == 0) return null;
        var at = -1;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Value == right[i].Value) continue;
            if (at >= 0) return null;
            at = i;
        }
        if (at < 0) return null;
        return (left[at].ToString(), right[at].ToString());
    }

    // Cổng warmup tai-mắt: đủ 10 cue so được + tỷ lệ khớp ≥ 0.4 mới cho drop
    // theo chữ. Dưới ngưỡng = video lệch ngôn ngữ hoặc tai điếc -> chỉ giữ +
    // lọc vị trí (không xóa oan cả video).
    private bool SpeechTextCheckReady()
    {
        var @checked = Interlocked.Read(ref _asrChecked);
        if (@checked < OcrSpeechMatch.WarmupSamples) return false;
        return (double)Interlocked.Read(ref _asrAgreed) / @checked >= OcrSpeechMatch.WarmupAgreement;
    }

    private async Task ApplyDropAsync(JudgeTask task, string reason) =>
        await ApplyVerdictAsync(task, "drop", reason);

    // Ghi quyết định keep/drop: xóa ghi .exclude TRƯỚC, nhật ký SAU (lưu hỏng
    // thì lần sau chấm lại, không mất quyết định âm thầm).
    private async Task ApplyVerdictAsync(JudgeTask task, string decision, string reason)
    {
        var applied = true;
        if (decision == "drop")
        {
            try
            {
                await _saveDrop(new OcrCueExclusion(task.Start, task.Text), _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { applied = false; }
        }
        if (!applied)
        {
            Interlocked.Increment(ref _deferred);
            return;
        }
        lock (_sync)
        {
            _journal.Add(new OcrCueVerdict(task.Start, task.Text, decision, reason,
                _model, DateTime.UtcNow.ToString("o")));
            if (decision == "drop") Interlocked.Increment(ref _deleted);
            else Interlocked.Increment(ref _keeps);
            Interlocked.Increment(ref _judged);
            SaveJournal();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop.Cancel(); } catch { }
        _queue.Writer.TryComplete();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { } catch { }
        lock (_sync)
        {
            // Hàng còn lại hoãn cho lượt sau (dedup reset theo scan mới).
            var leftovers = 0;
            while (_queue.Reader.TryRead(out _)) leftovers++;
            Interlocked.Add(ref _deferred, leftovers);
            // Vụ 16/09: job quét 0 giây (dừng ngay) mà vẫn SaveJournal thì ghi đè
            // file nhật ký cũ thành rỗng. Chỉ lưu khi đã nạp (đã chấm thật).
            if (_journalLoaded)
                SaveJournal();
        }
        try { _stop.Dispose(); } catch { }
    }
}

using System.Diagnostics;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Background TEMPORAL JUDGE verdict queue during scanning (user confirmed 17/09): OCR runs
// on its own, the judge verdicts settled cues one by one using time logic (still text =
// keep, moving text = drop), preventing duplicates via the journal + dedup.
// Invariants: never block the scan path (full means drop + count); the verdict only writes the
// journal; cancel is a graceful stop, the remainder are deferred.
internal static class OcrTrailingJudgeContract
{
    public static async Task RunAsync()
    {
        await DisabledIsNoOpAsync();
        await DisposeWithoutJudgingKeepsJournalAsync();
        await EnqueueDedupeAsync();
        await CapDropAsync();
        await VerdictJournalWrittenAsync();
        await DropVerdictSavedBeforeJournalAsync();
        await WorkersScaleWithLanesAsync();
        await AlreadyJudgedSkippedAsync();
        await GrabFailParksAsync();
        await TemporalKeepSteadyTextAsync();
        await TemporalDropMovingTextAsync();
        await TemporalSingleCharIgnoresLongBackgroundDistractorAsync();
        await TemporalSingleCharMovingStillDroppedAsync();
        await TemporalDropCreditTitleAsync();
        await SpeechVocativeCreditShapedKeptAsync();
        await CreditOverUnrelatedSpeechStillDroppedAsync();
        CreditTitleRule();
        FindBestFixMatchRule();
        await TemporalFixLowConfidenceGarbledAsync();
        await TemporalNoFixWhenConfidentAsync();
        await SpeechSilenceDropAsync();
        await SpeechMismatchDropAfterWarmupAsync();
        await SparseEarMismatchGoesToProbeAsync();
        await TimingSlipSimRescueAsync();
        await TimingSlipContainedRescueAsync();
        await HighConfidentDigitAffixGetsFixedAsync();
        await SpeechContainedShortCueKeptAfterWarmupAsync();
        await SpeechMismatchKeptDuringWarmupAsync();
        await SpeechStrongMatchEarlyKeepAsync();
        await DisposeDrainsAsync();
        await SilenceFlagsReviewFixAsync();
        await EarCorrectsSingleCharAsync();
    }

    private static BiliSubStudio.Core.Editor.EditorSpeechSegment Seg(
        double start, double end, string text) =>
        new(start, end, text, -0.2, 0.0, [], "uncertain", 0, 0);

    private static EditorSpeechSegment SegWords(
        double start, double end, string text, IReadOnlyList<EditorWordTiming> words) =>
        new(start, end, text, -0.2, 0.0, words, "uncertain", 0, 0);

    private static EditorWordTiming W(string text, double start, double end, double prob = 0.99) =>
        new(text, start, end, prob);

    private static async Task SpeechSilenceDropAsync()
    {
        // 18/09 (fix #1): a cue deep inside silence (100s from speech) -> NO longer dropped
        // (a subtitle with no audio is real) — let the eye decide via probe (must grab +
        // keep fail-open). Static background text is caught by the banner guard at finish.
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[] { Seg(100.0, 200.0, "啊") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(300, "印")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue im lặng");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) > 0, "cue im lặng phải qua probe mắt (grab)");
        Check(service.Snapshot().Deleted == 0, "im lặng sâu không được drop");
        Check(service.Snapshot().Keeps == 11, "im lặng sâu phải giữ hết");
    }

    private static async Task SpeechMismatchDropAfterWarmupAsync()
    {
        // The ear hears something entirely different from the text shown (sim ~0), warmup is
        // done, and the EAR COVERS the cue with confident words (positive contradiction 18/09) -> drop.
        var journal = TempJournal();
        var grabs = 0;
        var words = new[]
        {
            W("啊", 100.0, 125.0), W("啊", 125.0, 150.0),
            W("啊", 150.0, 175.0), W("啊", 175.0, 200.0),
        };
        var speech = new[] { SegWords(100.0, 200.0, "啊啊啊啊", words) };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "啊啊啊啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(150, "印")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue lệch");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) == 0, "mismatch drop không được grab");
        Check(service.Snapshot().Deleted == 1, "tai nghe khác hẳn phải drop");
    }

    private static async Task SparseEarMismatchGoesToProbeAsync()
    {
        // 18/09 (general rule): low sim but a SPARSE ear around the cue (not a full cover)
        // -> not enough evidence to convict -> dropping is forbidden, let the eye probe decide
        // (SteadyOcr keeps). The old code dropped wrongly here.
        var journal = TempJournal();
        var grabs = 0;
        var words = new[] { W("啊", 100.0, 104.0) };
        var speech = new[] { SegWords(100.0, 200.0, "啊啊啊啊", words) };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "啊啊啊啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(150, "印")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue lệch");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "tai thưa không được drop");
        Check(service.Snapshot().Keeps == 11, "cue thưa phải để probe giữ hết");
        Check(Volatile.Read(ref grabs) > 0, "phải chạy probe mắt thay vì drop");
    }

    private static async Task SpeechContainedShortCueKeptAfterWarmupAsync()
    {
        // 18/09 (containment exemption): the short cue 好的 sits verbatim inside the long
        // segment 好的我们开始吧 — Levenshtein sim is low but the ear DID say it -> dropping is
        // forbidden, let the eye probe decide (SteadyOcr keeps).
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[] { Seg(100.0, 200.0, "啊"), Seg(100.0, 200.0, "好的我们开始吧") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(150, "好的")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue ngắn");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "cue tai có nói nguyên văn không được drop");
        Check(service.Snapshot().Keeps == 11, "cue ngắn thật phải giữ hết");
    }

    // 18/09: HIGH-confidence cue + mid ear (mid-sim) + trailing digit garbage -> try trimming
    // (new flow) and apply it because the cut edge is digits (ShouldApplyTrimFix). The old code
    // skipped the fallback so it never fixed anything -> red.
    private static async Task HighConfidentDigitAffixGetsFixedAsync()
    {
        var journal = TempJournal();
        var grabs = 0;
        var fixes = new List<OcrCueFix>();
        var words = new[]
        {
            W("AB", 100.0, 100.5), W("ok", 100.5, 101.0),
            W("AB", 149.8, 150.4), W("ok", 150.4, 151.0),
        };
        var speech = new[] { SegWords(100.0, 200.0, "AB ok", words) };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech,
            saveFix: (fix, _) => { lock (fixes) { fixes.Add(fix); } return Task.CompletedTask; });
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "AB ok")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(150, "AB 12")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue số rác");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "số rác tin cao không được drop");
        Check(fixes.Count == 1 && fixes[0].OldText == "AB 12" && fixes[0].NewText == "AB",
            "phải lưu fix cắt số rác");
        Check(Volatile.Read(ref grabs) == 3, "chỉ probe 3 khung, không đọc lại lân cận");
    }

    // Field 2408.1: OCR misreads 1 character (克~直) + the subtitle appears 0.4s after the audio —
    // the ear says the same content just outside ±0.3s. The old code dropped wrongly (narrow sim = 0);
    // the new rule rescues with wide sim then lets the probe keep it.
    private static async Task TimingSlipSimRescueAsync()
    {
        var journal = TempJournal();
        var grabs = 0;
        var nearWords = new[]
        {
            W("你", 101.9, 102.1), W("好", 102.1, 102.3), W("吗", 102.3, 102.5),
            W("今", 102.5, 102.7), W("天", 102.7, 102.9),
        };
        var speech = new[]
        {
            Seg(100.0, 101.5, "登上直升机的"),
            SegWords(101.9, 102.9, "你好吗今天", nearWords),
        };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101.0 + i * 0.05, "你好吗今天")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(101.9, "登上克和的")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue trễ");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "tai nói cùng nội dung gần đó thì cấm drop");
        Check(service.Snapshot().Keeps == 11, "cue trễ phải để probe giữ hết");
        Check(Volatile.Read(ref grabs) > 0, "phải chạy probe mắt thay vì drop");
    }

    private static async Task TimingSlipContainedRescueAsync()
    {
        // Containment variant: the short cue's core sits verbatim inside a LONG segment
        // outside the narrow window (wide sim is still low because of the length gap!) — only the
        // wide-containment branch rescues it (wide sim is not enough). The old code dropped wrongly.
        var journal = TempJournal();
        var grabs = 0;
        var nearWords = new[]
        {
            W("你", 101.9, 102.1), W("好", 102.1, 102.3), W("吗", 102.3, 102.5),
            W("今", 102.5, 102.7), W("天", 102.7, 102.9),
        };
        var speech = new[]
        {
            Seg(100.0, 101.5, "啊啊啊啊啊啊啊啊啊啊啊啊戊己庚辛"),
            SegWords(101.9, 102.9, "你好吗今天", nearWords),
        };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101.0 + i * 0.05, "你好吗今天")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(101.9, "戊己庚辛")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue trễ");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "lõi nằm trong tai rộng thì cấm drop");
        Check(service.Snapshot().Keeps == 11, "cue trễ phải để probe giữ hết");
        Check(Volatile.Read(ref grabs) > 0, "phải chạy probe mắt thay vì drop");
    }

    private static async Task SpeechMismatchKeptDuringWarmupAsync()
    {
        // Warmup not finished (the mismatched cue arrives BEFORE): no text-based drop -> take
        // the position path (SteadyOcr keeps) -> keep. Guards against wrongful deletion on
        // language-mismatched video.
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[] { Seg(100.0, 200.0, "啊") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 16, speech: speech);
        service.EnqueueCues([Cue(150, "川")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "không chấm");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "warmup không được drop-chữ");
        Check(service.Snapshot().Keeps == 1, "phải keep");
        Check(Volatile.Read(ref grabs) == 3, "phải chạy position (3 probes)");
    }

    private static async Task SpeechStrongMatchEarlyKeepAsync()
    {
        // Strong ear-eye match (sim 1.0): keep early, NO grab (saves work).
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[] { Seg(100.0, 200.0, "啊") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 16, speech: speech);
        service.EnqueueCues([Cue(150, "啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "không chấm");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) == 0, "khớp mạnh không được grab");
        Check(service.Snapshot().Keeps == 1, "phải keep sớm");
    }

    private static OcrCue Cue(double start, string text) =>
        new(start, start + 1.0, text, 0.99);

    private static string TempJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-trail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "j.verdicts.json");
    }

    private static async Task WaitForAsync(Func<bool> condition, string message, int timeoutMs = 8000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new InvalidOperationException("timeout: " + message);
            await Task.Delay(15);
        }
    }

    // Fake OCR: every frame sees the same character at the same position (center 0.5, 0.9) —
    // still -> keep.
    private static Task<IReadOnlyList<OcrResult>> SteadyOcr(
        IReadOnlyList<byte[]> frames, CancellationToken token)
    {
        foreach (var _ in frames) { }
        return Task.FromResult<IReadOnlyList<OcrResult>>(
            [LineWithText("川", 0.5, 0.9)]);
    }

    private static OcrResult LineWithText(string text, double centerX, double centerY, double confidence = 0.99) =>
        new(true, true, text, confidence,
            [new OcrLine(text, confidence,
                [(int)(centerX * 1920 - 50), (int)(centerY * 1080 - 10),
                 (int)(centerX * 1920 + 50), (int)(centerY * 1080 - 10),
                 (int)(centerX * 1920 + 50), (int)(centerY * 1080 + 10),
                 (int)(centerX * 1920 - 50), (int)(centerY * 1080 + 10)])],
            null);

    private static async Task DisabledIsNoOpAsync()
    {
        var calls = 0;
        await using var service = OcrTrailingJudge.Disabled(
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<byte[]?>([7]); });
        var (accepted, skipped) = service.EnqueueCues([Cue(1, "a")]);
        Check(accepted == 0 && skipped == 0, "bản tắt phải no-op");
        Check(calls == 0, "bản tắt không được đụng grabber");
        var stats = service.Snapshot();
        Check(stats.Enqueued == 0 && stats.Judged == 0, "bản tắt stats phải 0");
    }

    private static async Task DisposeWithoutJudgingKeepsJournalAsync()
    {
        // Case 16/09: a 0-second scan job (stops immediately, no cue judged yet) whose Dispose
        // still writes the journal would wipe all old verdicts. Not judged = do not touch the file.
        var journal = TempJournal();
        OcrVerdictJournal.Save(journal,
            [new OcrCueVerdict(40.0, "嗯", "keep", "old", "m", "2026-09-16")]);
        var before = File.ReadAllText(journal);
        var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), SteadyOcr, journal, maxQueue: 16);
        await service.DisposeAsync();
        Check(File.ReadAllText(journal) == before, "chưa chấm câu nào mà file nhật ký bị đụng?");
        Check(OcrVerdictJournal.Load(journal).Count == 1, "verdict cũ phải còn nguyên");
    }

    private static async Task EnqueueDedupeAsync()
    {
        var journal = TempJournal();
        var ocrCalls = 0;
        Task<IReadOnlyList<OcrResult>> Ocr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            Interlocked.Increment(ref ocrCalls);
            return SteadyOcr(frames, token);
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), Ocr, journal, maxQueue: 64);
        // The same cue 3 times (2 duplicates) + a different cue.
        service.EnqueueCues([Cue(10, "啊"), Cue(10, "啊"), Cue(11, "嗯"), Cue(10, "啊")]);
        // Wait for JUDGING TO FINISH (not just the OCR call) before disposing — disposing midway
        // cancels the in-flight task (flaky).
        await WaitForAsync(() => service.Snapshot().Judged == 2, "judge không chạy xong");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(stats.Judged == 2, $"trùng phải chấm đúng 1 lần mỗi câu (judged={stats.Judged})");
        Check(stats.Duplicates >= 2, "trùng phải đếm");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded.Count == 2, "journal phải có 2 verdict");
    }

    private static async Task CapDropAsync()
    {
        var journal = TempJournal();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<OcrResult>> Gated(
            IReadOnlyList<byte[]> frames, CancellationToken token) =>
            gate.Task.ContinueWith(_ => (IReadOnlyList<OcrResult>)[LineWithText("z", 0.5, 0.9)], token);
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), Gated, journal, maxQueue: 2);
        // The first task enqueues so the loop sticks at the gate (not yet open) -> the queue holds at most 2.
        service.EnqueueCues([Cue(20, "z")]);
        await WaitForAsync(() => service.Snapshot().Enqueued == 1, "task đầu chưa vào");
        var (accepted, skipped) = service.EnqueueCues(
            [Cue(21, "a"), Cue(22, "b"), Cue(23, "c"), Cue(24, "d")]);
        gate.TrySetResult();
        // Wait for the ENTIRE pending queue to finish judging BEFORE disposing (each task is 3 OCR
        // round-trips; disposing immediately cancels midway -> judged is short, flaky).
        await WaitForAsync(() => service.Snapshot().Judged == 1 + accepted, "chưa chấm hết hàng tồn");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(accepted + skipped == 4, "mọi submit phải có số phận (nhận/bỏ)");
        Check(stats.QueueFullDropped >= 1, "tràn hàng phải đếm");
        // Every accepted task is either judged or deferred — the counts must add up.
        Check(stats.Judged + stats.Deferred == 1 + accepted, "judged+deferred phải khớp đã nhận");
    }

    private static async Task VerdictJournalWrittenAsync()
    {
        var journal = TempJournal();
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), SteadyOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(30, "川")]);
        await WaitForAsync(() => OcrVerdictJournal.Load(journal).Count >= 1, "verdict không ghi journal");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "keep" && loaded[0].CueText == "川", "journal sai nội dung");
        Check(loaded[0].Model == "temporal", "journal mất model");
        await service.DisposeAsync();
        Check(service.Snapshot().Keeps == 1, "đếm keep sai");
    }

    private static async Task DropVerdictSavedBeforeJournalAsync()
    {
        // A drop must write the .exclude file BEFORE writing the journal (if the save fails the next
        // run judges again, the decision is not lost).
        var journal = TempJournal();
        var drops = new List<OcrCueExclusion>();
        // Fake OCR: the text MOVES between frame 1 (x=0.4) and frame 3 (x=0.7)
        // (drifts 0.3 > 5%) -> drop. Frame 2 returns empty (OCR is blurry) — the drift is still
        // seen from the frame 1+3 pair.
        var calls = 0;
        Task<IReadOnlyList<OcrResult>> MovingOcr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<OcrResult>>(call switch
            {
                1 => [LineWithText("印", 0.4, 0.9)],
                2 => [],
                _ => [LineWithText("印", 0.7, 0.9)],
            });
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), MovingOcr, journal, maxQueue: 16,
            saveDrop: (drop, _) => { lock (drops) { drops.Add(drop); } return Task.CompletedTask; });
        // A fresh 1-character cue passes the suspicious-cue filter.
        service.EnqueueCues([Cue(90, "印")]);
        await WaitForAsync(() => OcrVerdictJournal.Load(journal).Count >= 1, "verdict không ghi journal");
        await service.DisposeAsync();
        Check(drops.Count == 1 && drops[0].Text == "印", "drop phải ghi file exclude");
        var stats = service.Snapshot();
        Check(stats.Deleted == 1, "đếm xóa sai");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "drop", "journal phải giữ quyết định drop");
    }

    private static async Task WorkersScaleWithLanesAsync()
    {
        // 8 lanes now -> 2 threads, 16 lanes later -> 4 (cap 4).
        Check(OcrTrailingJudge.WorkersForLanes(1) == 1, "1 lane phải 1 luồng");
        Check(OcrTrailingJudge.WorkersForLanes(4) == 1, "4 lane phải 1 luồng");
        Check(OcrTrailingJudge.WorkersForLanes(8) == 2, "8 lane phải 2 luồng");
        Check(OcrTrailingJudge.WorkersForLanes(16) == 4, "16 lane phải 4 luồng");
        Check(OcrTrailingJudge.WorkersForLanes(64) == 4, "trần 4 luồng");
        // Genuine multi-threaded parallel judging: 2 tasks stuck on the same gate open together.
        var journal = TempJournal();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<OcrResult>> Gated(
            IReadOnlyList<byte[]> frames, CancellationToken token) =>
            gate.Task.ContinueWith(_ => (IReadOnlyList<OcrResult>)[LineWithText("a", 0.5, 0.9)], token);
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), Gated, journal,
            maxQueue: 16, workers: 2);
        Check(service.Workers == 2, "workers phải đúng 2");
        service.EnqueueCues([Cue(100, "a"), Cue(101, "b")]);
        await WaitForAsync(() => service.Snapshot().Enqueued == 2, "enqueue chưa xong");
        gate.TrySetResult();
        await WaitForAsync(() => service.Snapshot().Judged == 2, "2 luồng không chấm song song?");
        await service.DisposeAsync();
    }

    private static async Task AlreadyJudgedSkippedAsync()
    {
        var journal = TempJournal();
        OcrVerdictJournal.Save(journal,
            [new OcrCueVerdict(40.0, "嗯", "keep", "old", "m", "2026-09-16")]);
        var ocrCalls = 0;
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]),
            (frames, token) =>
            {
                Interlocked.Increment(ref ocrCalls);
                return SteadyOcr(frames, token);
            },
            journal, maxQueue: 16);
        service.EnqueueCues([Cue(40, "嗯"), Cue(41, "啊")]);
        // Wait for JUDGING TO FINISH before disposing (disposing midway is a flaky cancel).
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "câu mới không chấm");
        await service.DisposeAsync();
        // Temporal: each judged cue = 3 OCR runs (3 frames). An old already-judged cue must be skipped
        // before the grab (journal dedup) -> only the new cue is judged = exactly 3 runs.
        Check(Volatile.Read(ref ocrCalls) == 3, "câu đã chấm rồi phải bỏ qua");
        Check(OcrVerdictJournal.Load(journal).Count == 2, "journal phải giữ cũ + thêm mới");
    }

    private static async Task GrabFailParksAsync()
    {
        var journal = TempJournal();
        var ocrCalls = 0;
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>(null),
            (frames, token) =>
            {
                Interlocked.Increment(ref ocrCalls);
                return SteadyOcr(frames, token);
            },
            journal, maxQueue: 16);
        service.EnqueueCues([Cue(70, "a")]);
        await WaitForAsync(() => service.Snapshot().GrabFailed >= 1, "grab hỏng không đếm");
        Check(Volatile.Read(ref ocrCalls) == 0, "grab hỏng vẫn gọi OCR?");
        await service.DisposeAsync();
    }

    private static async Task TemporalKeepSteadyTextAsync()
    {
        // Fake OCR: 3/3 frames same text same position -> STILL -> KEEP.
        var journal = TempJournal();
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), SteadyOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(110, "川")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(stats.Keeps == 1 && stats.Deleted == 0, "chữ đứng yên phải keep");
    }

    private static async Task TemporalDropMovingTextAsync()
    {
        // Fake OCR: 3/3 frames same text but MOVING (0.3 -> 0.5 -> 0.7,
        // drifts 0.4 > 5%) -> DROP.
        var journal = TempJournal();
        var calls = 0;
        Task<IReadOnlyList<OcrResult>> MovingOcr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<OcrResult>>([
                LineWithText("动", 0.3 + call * 0.1, 0.9)]);
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), MovingOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(120, "动")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(stats.Deleted == 1 && stats.Keeps == 0, "chữ di chuyển phải drop");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "drop" && loaded[0].Reason.Contains("DI CHUYỂN"),
            "reason phải nói di chuyển");
    }

    private static OcrLine LineAt(string text, double centerX, double centerY, double confidence = 0.99) =>
        new(text, confidence,
            [(int)(centerX * 1920 - 50), (int)(centerY * 1080 - 10),
             (int)(centerX * 1920 + 50), (int)(centerY * 1080 - 10),
             (int)(centerX * 1920 + 50), (int)(centerY * 1080 + 10),
             (int)(centerX * 1920 - 50), (int)(centerY * 1080 + 10)]);

    private static OcrResult FrameWithLines(params OcrLine[] lines) =>
        new(true, true, string.Concat(lines.Select(line => line.Text)), 0.99, lines, null);

    private static async Task TemporalSingleCharIgnoresLongBackgroundDistractorAsync()
    {
        // A 1-character cue "哼" standing still + a long background line containing it ("哼哼唧唧说不停")
        // MOVING (0.4 -> 0.5 -> 0.6): "contains" matching + the old longest-match would lock
        // onto the wrong background line -> wrongful drop. Absolute matching must lock onto the still
        // character -> KEEP.
        var journal = TempJournal();
        var calls = 0;
        Task<IReadOnlyList<OcrResult>> DistractorOcr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<OcrResult>>([
                FrameWithLines(
                    LineAt("哼", 0.5, 0.9),
                    LineAt("哼哼唧唧说不停", 0.3 + call * 0.1, 0.9, 0.9))]);
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), DistractorOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(140, "哼")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(stats.Keeps == 1 && stats.Deleted == 0, "cue 1 chữ đứng yên phải keep dù có chữ nền dài chứa nó");
    }

    private static async Task TemporalSingleCharMovingStillDroppedAsync()
    {
        // A 1-character background MOVING for real ("滚" 0.4 -> 0.5 -> 0.6, no long line
        // contains it): absolute matching still catches it correctly -> DROP as before.
        // Guards against over-fixing into keeping every short text.
        var journal = TempJournal();
        var calls = 0;
        Task<IReadOnlyList<OcrResult>> MovingSingleOcr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<OcrResult>>([
                FrameWithLines(LineAt("滚", 0.3 + call * 0.1, 0.9))]);
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), MovingSingleOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(150, "滚")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        var stats = service.Snapshot();
        Check(stats.Deleted == 1 && stats.Keeps == 0, "chữ 1 chữ di chuyển thật vẫn phải drop");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "drop" && loaded[0].Reason.Contains("DI CHUYỂN"),
            "reason phải nói di chuyển");
    }

    private static void CreditTitleRule()
    {
        // Title/credit (NOT speech) must drop — text logic, no AI.
        Check(OcrTemporalFilter.IsCreditTitle("立书人：冷暖暖 2026年6月3日"), "立书人： phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("立书人，冷暖暖 2026年6月3日"), "立书人， phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("立书人:2026年6月3日"), "立书人: phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("出品人：张三"), "出品人 phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("摄制：某某公司"), "摄制 phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("导演：李四"), "导演： phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("2026年6月3日"), "ngày thuần phải là credit");
        Check(OcrTemporalFilter.IsCreditTitle("© 2026 bilibili"), "bản quyền phải là credit");
        // Real speech mentioning a job title but NOT opening with the title +
        // a separator -> must keep (no wrongful drop).
        Check(!OcrTemporalFilter.IsCreditTitle("导演说明天开拍"), "thoại nhắc 导演 phải giữ");
        Check(!OcrTemporalFilter.IsCreditTitle("导演！"), "gọi đạo diễn phải giữ");
        Check(!OcrTemporalFilter.IsCreditTitle("宝子们看完了吧"), "thoại thật phải giữ");
        Check(!OcrTemporalFilter.IsCreditTitle("是不是觉得"), "thoại thật phải giữ");
        Check(!OcrTemporalFilter.IsCreditTitle(""), "rỗng phải giữ");
    }

    private static async Task TemporalDropCreditTitleAsync()
    {
        // Title/credit drops IMMEDIATELY without grabbing a frame (text logic).
        var journal = TempJournal();
        var grabs = 0;
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>(null); },
            SteadyOcr, journal, maxQueue: 16);
        service.EnqueueCues([Cue(130, "立书人：冷暖暖 2026年6月3日")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) == 0, "credit phải drop không cần grab");
        Check(service.Snapshot().Deleted == 1, "credit phải drop");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "drop" && loaded[0].Reason.Contains("tít/credit"),
            "reason phải nói tít/credit");
    }

    private static async Task SpeechVocativeCreditShapedKeptAsync()
    {
        // 18/09: a credit-shaped cue (主持人，+ separator) BUT the ear is saying exactly
        // that text (a vocative) -> NOT a title -> dropping forbidden, let the probe decide.
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[]
        {
            Seg(100.0, 200.0, "啊"),
            Seg(100.0, 200.0, "各位观众主持人你好欢迎收看"),
        };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech);
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "啊")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(150, "主持人，你好")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm câu xưng hô");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "thoại xưng hô không được drop kiểu credit");
        Check(service.Snapshot().Keeps == 11, "câu xưng hô phải giữ hết");
    }

    private static async Task CreditOverUnrelatedSpeechStillDroppedAsync()
    {
        // A real credit shown over a background with DIFFERENT audio (the ear does not say that
        // text) -> still drops as before, no grab needed.
        var journal = TempJournal();
        var grabs = 0;
        var speech = new[] { Seg(100.0, 200.0, "啊") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>(null); },
            SteadyOcr, journal, maxQueue: 16, speech: speech);
        service.EnqueueCues([Cue(150, "出品人：张三")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) == 0, "credit trên nền thoại lạ phải drop không grab");
        Check(service.Snapshot().Deleted == 1, "credit thật phải drop");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded[0].Decision == "drop" && loaded[0].Reason.Contains("tít/credit"),
            "reason phải nói tít/credit");
    }

    private static void FindBestFixMatchRule()
    {
        // A different reading of the same line (high sim) -> pick the most confident reading.
        const string garbled = "我们早已不是一个世界的人，我己经确定新的眼惘叶象";
        const string clean = "我们早已不是一个世界的人，我已经确定新的联姻对象";
        var lines = new[]
        {
            new OcrLine(clean, 0.95, [0, 0, 100, 20]),
            new OcrLine("龙王在她眼里只会做饭", 0.99, [0, 0, 100, 20]),
        };
        var best = OcrTemporalFilter.FindBestFixMatch(lines, garbled);
        Check(best is not null && best.Value.Text == clean, "phải tìm bản đọc khác cùng câu");
        // Identical text is not a "different reading" -> null.
        Check(OcrTemporalFilter.FindBestFixMatch(
            [new OcrLine(garbled, 0.99, [0, 0, 100, 20])], garbled) is null,
            "chữ y hệt phải null");
        // Entirely different text -> null.
        Check(OcrTemporalFilter.FindBestFixMatch(
            [new OcrLine("龙王在她眼里只会做饭", 0.99, [0, 0, 100, 20])], garbled) is null,
            "chữ khác hẳn phải null");
        Check(OcrTemporalFilter.FindBestFixMatch([], garbled) is null, "rỗng phải null");
    }

    private static async Task TemporalFixLowConfidenceGarbledAsync()
    {
        // A suspicious cue (⚠️) with still text but a wrong reading: 3 wide frames read wrong
        // (confidence 0.7, same position -> keep), 2 tight frames read correctly (confidence 0.95) ->
        // FIX (save .fix.json + journal), not a blind keep.
        const string garbled = "我们早已不是一个世界的人，我己经确定新的眼惘叶象";
        const string clean = "我们早已不是一个世界的人，我已经确定新的联姻对象";
        var journal = TempJournal();
        var fixes = new List<OcrCueFix>();
        var calls = 0;
        Task<IReadOnlyList<OcrResult>> OrderedOcr(
            IReadOnlyList<byte[]> frames, CancellationToken token)
        {
            var call = Interlocked.Increment(ref calls);
            // Calls 1-3: wide probes (wrong, low confidence). Calls 4-5: tight (correct, high confidence).
            var text = call <= 3 ? garbled : clean;
            var conf = call <= 3 ? 0.7 : 0.95;
            return Task.FromResult<IReadOnlyList<OcrResult>>([LineWithText(text, 0.5, 0.9, conf)]);
        }
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), OrderedOcr, journal, maxQueue: 16,
            saveFix: (fix, _) => { lock (fixes) { fixes.Add(fix); } return Task.CompletedTask; });
        var cue = Cue(200, garbled) with { Confidence = 0.7, NeedsReview = true };
        service.EnqueueCues([cue]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        Check(fixes.Count == 1 && fixes[0].NewText == clean && fixes[0].OldText == garbled,
            "phải lưu fix chữ đúng");
        var loaded = OcrVerdictJournal.Load(journal);
        Check(loaded.Count == 1 && loaded[0].Decision == "fix" && loaded[0].FixedText == clean,
            "journal phải giữ fix");
    }

    private static async Task TemporalNoFixWhenConfidentAsync()
    {
        // A high-confidence cue without ⚠️: NO reread (saves work), keep straight away.
        var journal = TempJournal();
        var ocrCalls = 0;
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]),
            (frames, token) =>
            {
                Interlocked.Increment(ref ocrCalls);
                return SteadyOcr(frames, token);
            },
            journal, maxQueue: 16);
        var cue = Cue(210, "川") with { Confidence = 0.95, NeedsReview = false };
        service.EnqueueCues([cue]);
        await WaitForAsync(() => service.Snapshot().Judged >= 1, "giám khảo không chấm");
        await service.DisposeAsync();
        // Only 3 wide probes, no tight reread (3 OCR runs).
        Check(Volatile.Read(ref ocrCalls) == 3, "ca tin cao không được đọc lại");
        Check(service.Snapshot().Keeps == 1, "phải keep");
    }

    // F2 (Cả Lớp video 19/09): a probe-kept cue more than 1.5s from all speech gets a
    // keep-text fix + FlagReview (the list shows ⚠️), NOT a drop, and the probe is NOT blocked.
    private static async Task SilenceFlagsReviewFixAsync()
    {
        var journal = TempJournal();
        var grabs = 0;
        var fixes = new System.Collections.Generic.List<OcrCueFix>();
        var speech = new[] { Seg(100.0, 140.0, "对话内容") };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => { Interlocked.Increment(ref grabs); return Task.FromResult<byte[]?>([7]); },
            SteadyOcr, journal, maxQueue: 64, speech: speech,
            saveFix: (fix, _) => { lock (fixes) fixes.Add(fix); return Task.CompletedTask; });
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "对话内容")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([Cue(300, "?")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue im lặng");
        await service.DisposeAsync();
        Check(Volatile.Read(ref grabs) > 0, "cue im lặng phải qua probe (grab)");
        Check(service.Snapshot().Deleted == 0, "im lặng không được drop");
        Check(service.Snapshot().Keeps == 11, "im lặng phải giữ hết");
        Check(fixes.Count == 1, $"phải ghi đúng 1 fix giữ-chữ (được {fixes.Count})");
        Check(fixes[0].OldText == "?" && fixes[0].NewText == "?",
            "fix giữ chữ không được đổi text");
        Check(fixes[0].FlagReview, "fix giữ chữ phải mang cờ review");
    }

    // F5 (Ăn Mày replay 19/09: ear-eye differ by 1 character but the original subtitle shows most
    // are deliberate paraphrases — the machine cannot tell): do NOT edit the text, only
    // flag review via a keep-text fix + a reason carrying a hint.
    private static async Task EarCorrectsSingleCharAsync()
    {
        var journal = TempJournal();
        var fixes = new System.Collections.Generic.List<OcrCueFix>();
        var words = new[]
        {
            W("云", 150.0, 150.4), W("烟", 150.4, 150.8),
            W("很", 150.8, 151.2), W("大", 151.2, 151.6),
        };
        var speech = new[]
        {
            Seg(100.0, 140.0, "对话内容"),
            SegWords(140.0, 200.0, "昨晚云烟很大", words),
        };
        await using var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]),
            SteadyOcr, journal, maxQueue: 64, speech: speech,
            saveFix: (fix, _) => { lock (fixes) fixes.Add(fix); return Task.CompletedTask; });
        for (var i = 0; i < 10; i++)
            service.EnqueueCues([Cue(101 + i, "对话内容")]);
        await WaitForAsync(() => service.Snapshot().Judged >= 10, "warmup không xong");
        service.EnqueueCues([new OcrCue(150.0, 151.0, "云烟很天", 0.85)]);
        await WaitForAsync(() => service.Snapshot().Judged >= 11, "không chấm cue sai chữ");
        await service.DisposeAsync();
        Check(service.Snapshot().Deleted == 0, "sai 1 chữ không được drop");
        Check(fixes.Count == 1, $"phải ghi đúng 1 fix gắn cờ (được {fixes.Count})");
        Check(fixes[0].OldText == "云烟很天" && fixes[0].NewText == "云烟很天",
            "không được sửa text (giữ nguyên cho người duyệt)");
        Check(fixes[0].FlagReview, "fix phát hiện phải mang cờ review");
    }

    private static async Task DisposeDrainsAsync()
    {
        var journal = TempJournal();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<OcrResult>> Gated(
            IReadOnlyList<byte[]> frames, CancellationToken token) =>
            gate.Task.ContinueWith(_ => (IReadOnlyList<OcrResult>)[LineWithText("a", 0.5, 0.9)], token);
        var service = OcrTrailingJudge.CreateTemporal(
            (_, _) => Task.FromResult<byte[]?>([7]), Gated, journal, maxQueue: 16);
        service.EnqueueCues([Cue(81, "a"), Cue(82, "b")]);
        await WaitForAsync(() => service.Snapshot().Enqueued == 2, "enqueue chưa xong");
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var stats = service.Snapshot();
        Check(stats.Deferred >= 1, "dispose phải hoãn hàng tồn");
        gate.TrySetResult();
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace BiliSubStudio.Core.Ocr;

// TIME-based judge running BEHIND OCR during a scan (locked by user 17/09): as soon as a cue closes it is
// judged immediately by time logic — a real hardcoded sub STAYS STILL across many frames
// (fixed position), while text inside the video (book pages/background panned by the camera) MOVES
// continuously:
// - text moving between probe frames -> DROP: write the .exclude.json file ->
//   removed from the SRT at export. Deleting that file undoes it.
// - text standing still -> KEEP: keep as-is, move to the next cue.
// Iron rules:
// - NEVER block the scan path: enqueue is TryWrite (when full, drop + count).
// - Only judge CLOSED cues (committed tail), never judge an open live cue.
// - Write deletions FIRST, write the journal AFTER: if a save fails, the next scan judges again
//   (no silent loss of decisions).
// - Cancel: stop cleanly, the remainder is deferred (judged next round).
// - Toggle off: use the Disabled implementation (no-op), identical scan path.
// Scale by lane (8 now, 16 later): judging threads = ceil(lanes/4), max 4.
// NO VLM, NO server, NO key — OCR via the existing worker pool.
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

    // Scale by lane: 1-4 lanes -> 1 thread, 5-8 -> 2, 9-12 -> 3, 13+ -> 4.
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
    // Ear-eye warmup: the first 10 cues only gather match rates (no text-based dropping) to
    // detect language-mismatched videos (ear and eye completely different -> disable text-drop,
    // avoiding wrongly deleting the whole video). Silence-drop was removed 18/09 (soundless subs are
    // real) — static background text is caught by the banner guard at finish.
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

    // TEMPORAL implementation for tests: queue/journal plumbing with a fake temporal OCR.
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

    // Live TEMPORAL implementation for the scanner (locked by user 17/09): NO VLM, NO server,
    // NO key. Judge each suspicious cue with time logic: capture 3 frames covering the cue,
    // re-OCR via the existing worker pool, compare text + position:
    // - text MOVING between frames (drift > 5% of frame) -> DROP (background text
    //   panning with the camera: book pages/credits passing by, not a sub) -> .exclude.json.
    // - text STILL (drift ≤ 5%) -> KEEP (real sub).
    // frameWidth/Height from input health (real resolution) to normalize drift.
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
        // OCR via the existing worker pool (batch 3 frames, within the worker limit of 4).
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

    // Disabled implementation: every call is a no-op, stats 0. Used when the toggle is off —
    // the scan path needs no null branch.
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

    // Judge ALL closed cues (NO VLM-style suspicious-cue filtering — background text
    // (credits/book pages) is read CONFIDENTLY, so the old suspicious-cue filter (long/garbled/low-confidence/
    // flagged) missed exactly the cues that should be deleted). Non-blocking: a full queue means drop + count.
    // Returns (accepted, skipped) so the caller can add telemetry. Disabled/disposed -> (0,0).
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
                        // Last-resort net: the loop absolutely must not deadlock — drop the task,
                        // keep going. Detected via stats (judged stays still
                        // while enqueue increases).
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
        // TEMPORAL (locked by user 17/09): the ONLY judging path — capture 3 frames covering the cue,
        // re-OCR via the worker pool, compare text + position -> keep/drop.
        await JudgeOneTemporalAsync(task);
    }

    // Judge one cue with time logic (locked by user 17/09): capture 3 frames covering the cue
    // (±0.3s margins to amplify drift), batch OCR, compare text + position:
    // - MOVING (drift > 5% of frame) -> DROP (write .exclude.json, remove from SRT).
    // - STILL -> KEEP (real sub, move to the next cue).
    // Fail-open: too few frames/OCR error -> KEEP (no wrongful drop), only count grab failures.
    private async Task JudgeOneTemporalAsync(JudgeTask task)
    {
        var temporalOcr = _temporalOcr;
        if (temporalOcr is null) return;
        // Ear-eye cross-check (Whisper, 18/09 — the user's pinned general rule): what the ear hears
        // vs what the eye sees. SILENCE MEANS KEEP, ONLY A CLEAR POSITIVE CONTRADICTION DELETES:
        // - no more dropping when overlapping speech is missing: soundless-sub segments are
        //   real in user videos (silence -> let the eye decide via the probe below).
        //   Static background text during silence is caught by the banner guard at finish
        //   (OcrBannerGuard), not the judge's job.
        // - The ear hears clearly differently -> DROP, but "clearly different" must be a POSITIVE
        //   CONTRADICTION: sim < 0.3 + after warmup + the cue not appearing verbatim in the ear +
        //   the ear FULLY COVERS the cue with confident text. A sparse/non-covering ear (silence gaps, ear misses)
        //   is insufficient evidence — let the position filter decide, and never drop
        //   because "the ear didn't hear it". Late-ear rescue exception: the ear says the same
        //   content within ±1.5s (late subs/tracker splitting) means no contradiction.
        // - Strong ear match (≥ 0.6) -> check for trailing junk trim then KEEP early
        //   (avoids capturing frames).
        // - The rest (including no overlapping speech at all) -> let the position filter decide.
        //   Never correct text here (fixes only in the fallback/trim below,
        //   which have their own guards).
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
                // Positive contradiction (general rule 18/09): low sim + the cue not appearing
                // verbatim in the ear + the ear FULLY COVERS the cue with confident text is required to
                // accuse. Before accusing, rescue late ears: a late-displaying sub
                // or tracker cue splitting can put the ear saying the same content outside
                // ±0.3s (field 2408.1: the ear finished 0.75s earlier while the cue only then appeared)
                // — if the ear says the same content within ±1.5s there is NO contradiction, leave it for the
                // eye probe to decide. Silence/sparse means keep (insufficient evidence).
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
        // Titles/credits (role names/dates/copyright) — drop WITHOUT capturing
        // frames, but ONLY when the ear does not speak that text (18/09: moved to after the ear-listening rule;
        // previously dropping immediately wrongly removed address dialogue "导演，你听我说",
        // characters speaking dates, dialogue mentioning 版权). Real film credits display
        // over silence or over other speech (the ear doesn't speak them) -> still drop.
        // If the ear speaks it (strong match or verbatim) -> fall through to the eye probe to decide.
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
        // The ear DETECTS one character the eye misread (video Ca Lop 19/09 + An May replay:
        // 21 time-aligned ear-eye divergences, but comparing the original subs only 2
        // were real misreads, 19 were deliberate on-screen paraphrases) — a machine
        // CANNOT distinguish misread/paraphrase offline, and correcting eye text per the
        // ear would BREAK paraphrase subs (changing correct text into the ear's words). Therefore: do NOT
        // correct text (the ear does not correct characters — same rule as "the ear never generates text"), only attach
        // a review flag via a text-preserving fix, with the reason carrying a hint for the human reviewer
        // to decide (shown in the ⚠️ tooltip on the list).
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
        // ALWAYS use a WIDE window covering outside the cue (±1s): a 0.27s cue scanned only
        // inside itself gives a 0.16s window — drift never crosses the threshold (bug
        // fixed 17/09). Text matching by exact CUE TEXT means no bleed into the next sub (different
        // text does not match); the same text spread out (a long sub split into cues) shares a
        // position -> correct keep.
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
                // Cancelled midway = the cue was not fully judged -> defer to the next round
                // (the old VLM path also counted deferred, never silently swallowed).
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
        // OCR each frame that has an image; a failed frame -> null (OcrTemporalFilter skips it, fail-open).
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
        // Re-read fallback (17/09): a suspicious cue (⚠️/low confidence) whose text is STILL
        // but may be obscured by artifacts (a laser flare sweeping across frames) ->
        // re-read closely adjacent frames within the cue, pick the highest-confidence read. Only FIX
        // when the new read is CLEARLY better (≥0.88 and ahead by ≥0.08) and the text differs —
        // never "correct" correct text into wrong text.
        if (task.NeedsReview || task.Confidence < OcrTemporalFilter.FallbackTriggerConfidence)
        {
            if (await TryFixFromNeighborsAsync(task, mid, duration))
                return;
            if (await TryTrimByEarAsync(task, asrSim, peelAllowed: true))
                return;
        }
        else
        {
            // High-confidence probe-keep cue (18/09): only try edge trimming (cheap, pure logic),
            // NO re-reading of neighbouring frames, NO wall peeling (expensive). Applies
            // when the trim is digits/marks/latin only (ShouldApplyTrimFix) — confident Han
            // characters and pure punctuation are never touched here.
            if (await TryTrimByEarAsync(task, asrSim, peelAllowed: false))
                return;
        }
        // Suspicious context text (video Ca Lop 19/09: notes/signs/walls the ear never
        // speaks — frames 731/347/12410): if the probe kept it (still) but the cue is > 1.5s from EVERY
        // speech segment, attach a review flag via a text-preserving fix (fix
        // OldText==NewText + FlagReview; export/list shows ⚠️). KEEP the cue, do not
        // delete (per 18/09, deletion belongs to banner/user). Requires the ear to exist + after
        // warmup (a deaf/language-mismatched ear makes every cue "silent" — bulk flagging
        // is forbidden). Placed AFTER the probe so it does not block the drift-drop path.
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
        // Closely adjacent frames within the cue (artifacts sweeping across frames, sub stands
        // still): ±min(0.2s, 25% duration), clamped ≥ 0. Unlike the wide probe (±1s outside
        // the cue, different subs) — here we need the SAME sub read differently.
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

    // Ear words within the cue's narrow window ± OverlapPadSeconds (the same window the trim
    // path uses): raw material for EarCoversSpan on the drop-mismatch path.
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

    // Trim leading/trailing junk per the ear (17/09): OCR text sticking extra context text at
    // the edges with no ear counterpart -> trim, keep the matching part.
    // peelAllowed=false (high-confidence cue, 18/09): only try edge trimming (cheap), skip
    // repeat-wall removal (expensive, for suspicious cues). Apply the fix through the shared gate
    // ShouldApplyTrimFix (strong/review/low-confidence, or a digits/marks/latin trim).
    // Pure logic + ear listening, NEVER guess new text (safer than correcting text).
    private async Task<bool> TryTrimByEarAsync(JudgeTask task, double asrSim, bool peelAllowed = true)
    {
        var speech = _speech;
        if (speech is null || speech.Count == 0) return false;
        // Only trim after warmup (against wrongly trimming language-mismatched/deaf-ear videos —
        // the same gate as text-drop).
        if (!SpeechTextCheckReady()) return false;
        var from = task.Start - OcrSpeechMatch.OverlapPadSeconds;
        var to = task.End + OcrSpeechMatch.OverlapPadSeconds;
        var words = new List<BiliSubStudio.Core.Editor.EditorWordTiming>();
        // Widely-adjacent ear text vetoes TrimEdges (bug with cue 36 没有之一
        // 18/09): real words may lie outside the narrow words window due to sub-vs-audio timing
        // drift — text to trim that the ear speaks nearby is forbidden to trim.
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
        // Edge trimming cannot reach a repeat wall (occupying most of the cue) -> try wall removal.
        return await TryPeelRepeatTilesAsync(task, words, nearbySpoken);
    }

    // Remove repeated background text walls (danmaku 陪一支 18/09, cue 5348): repeated tiles the ear
    // never speaks are removed, keeping the core the ear does speak. Runs AFTER TrimEdges (edge junk is few
    // characters) — repeat walls occupy most of the cue so TrimEdges cannot reach them. Same
    // warmup gate + ear guards, NEVER guess new text.
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

    // Write fixes FIRST, journal AFTER (if the save fails, judge again next round).
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

    // The unique differing character position between 2 texts of equal rune length (with old and new
    // characters) — supports the review hint. Null when there is not exactly one differing position.
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

    // Ear-eye warmup gate: 10 comparable cues + match rate ≥ 0.4 are required before text-based
    // dropping. Below the threshold means a language-mismatched video or a deaf ear -> keep only +
    // position filtering (do not wrongly delete the whole video).
    private bool SpeechTextCheckReady()
    {
        var @checked = Interlocked.Read(ref _asrChecked);
        if (@checked < OcrSpeechMatch.WarmupSamples) return false;
        return (double)Interlocked.Read(ref _asrAgreed) / @checked >= OcrSpeechMatch.WarmupAgreement;
    }

    private async Task ApplyDropAsync(JudgeTask task, string reason) =>
        await ApplyVerdictAsync(task, "drop", reason);

    // Record keep/drop decisions: deletions written to .exclude FIRST, journal AFTER (if the save fails,
    // judge again next round, no silent loss of decisions).
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
            // The remaining queue is deferred to the next round (dedup resets with each new scan).
            var leftovers = 0;
            while (_queue.Reader.TryRead(out _)) leftovers++;
            Interlocked.Add(ref _deferred, leftovers);
            // Case 16/09: a 0-second scan job (stopped immediately) that still calls SaveJournal overwrites
            // the old journal file with empty content. Only save after loading (real judging happened).
            if (_journalLoaded)
                SaveJournal();
        }
        try { _stop.Dispose(); } catch { }
    }
}

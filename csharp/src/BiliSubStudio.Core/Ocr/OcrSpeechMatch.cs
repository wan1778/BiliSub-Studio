using System.Text;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Ocr;

// Ear-vs-eye cross-check (17/09): what Whisper hears vs what OCR sees.
// - Silence timeline derived from speech segments: the lane skips OCR where it is silent (speed).
// - Judge: a cue not matching any overlapping speech segment -> not speech -> drop;
//   strong match -> early keep (no frame capture needed); in between -> let the position filter decide.
// Pure logic, NO AI. Fail-open: missing ear data means skip everything.
public static class OcrSpeechMatch
{
    // Speech pad when building the timeline (±0.5s: subs are usually slightly early/late relative to audio).
    public const double TimelinePadSeconds = 0.5;
    // Overlap pad when finding segments overlapping a cue (±0.3s: both sides' timing drift slightly).
    public const double OverlapPadSeconds = 0.3;
    // Deep-silence threshold (kept as a test utility; the judge no longer drops via
    // this path since 18/09 — soundless subs are real, static background text is caught by the banner
    // guard at finish).
    public const double SilenceDropGapSeconds = 1.5;
    // Ear-vs-eye text similarity threshold (actually measured on video 0916: real dialogue 0.38-0.9,
    // scene text ~0.0-0.2).
    public const double MinSimilarity = 0.3;
    public const double StrongSimilarity = 0.6;
    // Warmup gate: the first 10 cues are statistics only (no text-based dropping) to detect
    // language-mismatched videos (ear and eye completely different -> disable text-drop, to avoid
    // wrongly deleting the whole video). Silence-drop is also only enabled after warmup (a deaf Whisper
    // produces phantom silence everywhere).
    public const int WarmupSamples = 10;
    public const double WarmupAgreement = 0.4;
    // Careful-scan region, option A (18/09, the user's safest choice): a silence gap (no
    // Whisper speech) longer than this threshold gets EVERY frame OCR'd by the lane (marked
    // Suspicious instead of NoSub) — soundless-subs/fade-ins inside a long silent
    // segment cannot slip through a skip gap. Judge/guard read the original segments so they are not
    // affected. Shorter gaps stay as-is (skip as before, fast).
    public const double LongSilenceFullScanSeconds = 60;

    // Find silence intervals longer than the threshold (sorted by Start for callers'
    // convenience — the function sorts itself). Clamped to [0, duration]. Empty/meaningless -> empty.
    public static IReadOnlyList<(double Start, double End)> FindLongSilences(
        IReadOnlyList<EditorSpeechSegment>? segments, double durationSeconds)
    {
        var zones = new List<(double Start, double End)>();
        if (!(durationSeconds > 0) || !double.IsFinite(durationSeconds)) return zones;
        var ordered = (segments ?? [])
            .Where(segment => segment is not null
                && double.IsFinite(segment.Start) && double.IsFinite(segment.End)
                && segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .ToArray();
        var cursor = 0.0;
        foreach (var segment in ordered)
        {
            var gapStart = Math.Max(0, cursor);
            var gapEnd = Math.Min(durationSeconds, segment.Start);
            if (gapEnd - gapStart > LongSilenceFullScanSeconds)
                zones.Add((gapStart, gapEnd));
            if (segment.End > cursor) cursor = segment.End;
            if (cursor >= durationSeconds) break;
        }
        if (durationSeconds - cursor > LongSilenceFullScanSeconds)
            zones.Add((Math.Max(0, cursor), durationSeconds));
        return zones;
    }

    // Stamp the careful-scan region onto the timeline: the NoSub flag inside the region -> Suspicious (read
    // fully). Keep HasSub unchanged (speech core + pad) so the global council switch
    // still has the ear-listening signal.
    public static void MarkLongSilences(
        VadTimeline? timeline,
        IReadOnlyList<(double Start, double End)>? zones)
    {
        if (timeline?.Flags is not { Length: > 0 } flags) return;
        if (timeline.FrameSeconds <= 0) return;
        if (zones is null) return;
        foreach (var (start, end) in zones)
        {
            if (!(end > start)) continue;
            var first = Math.Max(0, (int)Math.Floor(Math.Max(0, start) / timeline.FrameSeconds));
            var last = Math.Min(flags.Length - 1, (int)Math.Floor(end / timeline.FrameSeconds));
            for (var index = first; index <= last; index++)
                if (flags[index] == VadFlag.NoSub)
                    flags[index] = VadFlag.Suspicious;
        }
    }

    // Build a 0.2s timeline from speech segments: has sound -> HasSub (scan as before;
    // HasSub is generated correctly for Whisper (E.5b) — reading sites only split out NoSub
    // so HasSub behaves exactly like Suspicious, plus it acts as the "timeline came from the
    // ear" signal to open council skip without changing the signature), silence -> NoSub
    // (council is skipped when the other gates agree + the canary watches).
    public static VadTimeline BuildTimeline(
        IReadOnlyList<EditorSpeechSegment> segments, double durationSeconds)
    {
        var frameCount = Math.Max(1, (int)Math.Ceiling(Math.Max(0, durationSeconds) / OcrVadProbe.FrameSeconds));
        var flags = new VadFlag[frameCount];
        for (var index = 0; index < flags.Length; index++) flags[index] = VadFlag.NoSub;
        if (segments is null) return new VadTimeline(OcrVadProbe.FrameSeconds, flags);
        foreach (var segment in segments)
        {
            if (segment is null) continue;
            var from = Math.Max(0, segment.Start - TimelinePadSeconds);
            var to = segment.End + TimelinePadSeconds;
            if (!(to > from) || !double.IsFinite(from) || !double.IsFinite(to)) continue;
            var first = Math.Max(0, (int)Math.Floor(from / OcrVadProbe.FrameSeconds));
            var last = Math.Min(flags.Length - 1, (int)Math.Floor(to / OcrVadProbe.FrameSeconds));
            for (var index = first; index <= last; index++) flags[index] = VadFlag.HasSub;
        }
        return new VadTimeline(OcrVadProbe.FrameSeconds, flags);
    }

    // Late-ear rescue pad for the drop-mismatch path (±1.5s = veto horizon): subs can
    // appear LATE after the audio (the tracker opens the cue late) or the tracker splits cues so the
    // ear saying the same sentence falls outside ±0.3s (field 2408.1: the ear finished 0.75s earlier!).
    // Used only to RESCUE (waive a drop), not to accuse/keep early.
    public const double MismatchOverlapPadSeconds = 1.5;

    // Overlapping segments [start-pad, end+pad] (sorted by Start for callers' convenience).
    public static IReadOnlyList<EditorSpeechSegment> OverlappingSegments(
        IReadOnlyList<EditorSpeechSegment> segments, double start, double end,
        double padSeconds = OverlapPadSeconds)
    {
        var output = new List<EditorSpeechSegment>();
        if (segments is null) return output;
        var from = start - padSeconds;
        var to = end + padSeconds;
        foreach (var segment in segments)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End)) continue;
            if (segment.Start > to) break;
            if (segment.End >= from) output.Add(segment);
        }
        return output;
    }

    // Ear fully covers the span (18/09, the user's pinned general rule): CONFIDENT text (prob ≥ 0.8)
    // covering ≥ 75% of the cue duration is positive evidence to accuse (only
    // combined with low sim + not contained outside does it become a contradiction). A sparse,
    // low-confidence, or empty ear -> false (insufficient evidence -> keep, fail-closed).
    // This function does NOT look at text content (similarity/difference is decided by sim/containment).
    public const double ContradictMinCoverage = 0.75;

    public static bool EarCoversSpan(
        double cueStart, double cueEnd, IReadOnlyList<EditorWordTiming>? words)
    {
        if (words is null) return false;
        var covered = 0.0;
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
            if (!double.IsFinite(word.Probability) || word.Probability < TrimMinWordProb) continue;
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
            covered += Math.Max(0, Math.Min(word.End, cueEnd) - Math.Max(word.Start, cueStart));
        }
        var duration = Math.Max(0.001, cueEnd - cueStart);
        return covered / duration >= ContradictMinCoverage;
    }

    // Containment waiver (18/09): a short cue appearing VERBATIM inside a long ear-spoken
    // sentence means the ear DID say it — Levenshtein sim is low only because of length difference
    // (e.g. cue 好的 inside segment 好的我们开始吧 scores only 0.25 < drop threshold
    // 0.3 even though the ear says exactly those characters). Fold numerals on both sides (2 vs 两) so containment
    // does not miss due to different numeral forms. Used to block wrongful drops, NOT to keep early.
    public static bool IsVerbatimContained(
        string cueText, IReadOnlyList<EditorSpeechSegment> overlapping)
    {
        if (string.IsNullOrWhiteSpace(cueText) || overlapping is null) return false;
        var needle = FoldDigits(OcrTemporalFilter.Normalize(cueText));
        if (needle.Length == 0) return false;
        foreach (var segment in overlapping)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            var hay = FoldDigits(OcrTemporalFilter.Normalize(segment.Text));
            if (hay.Length == 0) continue;
            if (hay.Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Highest similarity between the cue's text and the overlapping segments.
    public static double MaxSimilarity(
        string cueText, IReadOnlyList<EditorSpeechSegment> overlapping)
    {
        var best = 0.0;
        if (string.IsNullOrWhiteSpace(cueText) || overlapping is null) return best;
        foreach (var segment in overlapping)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            var similarity = OcrTextCleanup.TextSimilarity(cueText, segment.Text);
            if (similarity > best) best = similarity;
        }
        return best;
    }

    // Distance from the cue to the nearest speech segment (0 if currently overlapping).
    // Used for silence-drop: only drop cues sitting deep inside silence.
    // Suspicious-context-text flagging threshold (video Ca Lop 19/09: signs/walls the ear
    // never speaks): a cue > 1.5s from every speech segment gets review flagged (keep the cue,
    // do not delete — deletion is decided by banner/user).
    public const double SilenceFlagGapSeconds = 1.5;
    public static double SilenceGap(
        double cueStart, double cueEnd, IReadOnlyList<EditorSpeechSegment> segments)
    {
        if (segments is null) return double.PositiveInfinity;
        var gap = double.PositiveInfinity;
        foreach (var segment in segments)
        {
            if (segment is null) continue;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End)) continue;
            if (segment.End >= cueStart && segment.Start <= cueEnd) return 0;
            var distance = segment.End < cueStart
                ? cueStart - segment.End
                : segment.Start - cueEnd;
            if (distance < gap) gap = distance;
        }
        return gap;
    }

    // Trim leading/trailing junk per the ear (17/09, tightened 18/09): OCR text sticks on extra context
    // text at the edges. Try every edge trim (total letters/digits trimmed ≤ 4, prefer the fewest),
    // pick the first option whose remainder is a SUBSTRING of what the ear spoke.
    // IRON RULE 18/09 (the harness tested 8778 real cues proving the old guards did not
    // rescue them: 后世之人→世之, 龙王逃课→王逃, 没有之一→之一...): FORBID CUTTING HAN CHARACTERS.
    // Han characters may only be kept intact or the whole cue deleted (the judge drop has its reason on record),
    // never nibbled at the inside (that is silent data loss). A trim may only
    // contain digits/marks/latin — exactly the class of junk users reported (trailing digits, (), ♡, E, boss).
    // Han junk stuck at the edge (rare — 人第一) is fixed by hand, not guessed.
    // The remaining guards (safety first):
    // - every ear word with confidence ≥ 0.75 (the ear is listening clearly; case 那 2 with 76% ear confidence still allowed a
    //   non-Han trim);
    // - the ear covers ≥ 75% of the cue duration (a sparse ear means the ear missed characters);
    // - EDGE ANCHOR (18/09): trim the head only when the ear's FIRST word starts near the cue head,
    //   trim the tail only when the LAST word ends near the cue tail;
    // - NEIGHBOUR VETO (18/09): text to be trimmed that the ear DOES speak nearby is forbidden;
    // - FOLD NUMERAL-HAN (18/09): fold both sides to numerals for exact matching;
    // - if the cue already fully matches what the ear spoke, do not trim; the kept part is ≥ 1 character (non-Han
    //   trims, cues ≤5 runes due to the total trim cap).
    // Do not touch wrong characters IN THE MIDDLE of a sentence — keep as-is. Return null when no trim is possible.
    public const int TrimMaxRun = 4;
    public const int TrimMinKept = 2;
    public const double TrimMinWordProb = 0.8;
    public const double TrimWordProbFloor = 0.75;
    public const double TrimMinCoverage = 0.75;
    public const double TrimEdgeAnchorSeconds = 0.35;
    public const double TrimNearbyVetoSeconds = 1.5;

    // Fold Han-numeral characters to Arabic numerals for exact two-sided matching (ear 九 vs eye 9,
    // 两 vs 2). Single counting digits only (十百千万亿 are arithmetic — left untouched;
    // missing folds make veto/containment miss toward the safe side: keep, no trim).
    private static string FoldDigits(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var output = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            output.Append(rune.Value switch
            {
                '零' => "0",
                '一' => "1",
                '二' => "2",
                '三' => "3",
                '四' => "4",
                '五' => "5",
                '六' => "6",
                '七' => "7",
                '八' => "8",
                '九' => "9",
                '两' => "2",
                _ => rune.ToString(),
            });
        }
        return output.ToString();
    }

    // Duplicated 1:1 from ChineseSubtitleNormalizer.IsHan (kept private to avoid opening the API):
    // core Han block + extension. Kana/hangul/other characters do NOT count as Han
    // here — a trim touching only that group is treated like trimming digits/marks (less dangerous
    // than cutting Han characters).
    private static bool IsHanLetter(Rune rune) => rune.Value is
        >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x323AF;

    public static string? TrimEdges(
        string cueText, double cueStart, double cueEnd,
        IReadOnlyList<EditorWordTiming> words, string? nearbySpoken = null)
    {
        if (string.IsNullOrWhiteSpace(cueText) || words is null) return null;
        var heard = new StringBuilder();
        var covered = 0.0;
        var firstStart = double.PositiveInfinity;
        var lastEnd = double.NegativeInfinity;
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
            if (!double.IsFinite(word.Probability) || word.Probability < TrimWordProbFloor) return null;
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
            heard.Append(word.Text);
            covered += Math.Max(0, Math.Min(word.End, cueEnd) - Math.Max(word.Start, cueStart));
            if (word.Start < firstStart) firstStart = word.Start;
            if (word.End > lastEnd) lastEnd = word.End;
        }
        var spoken = NormalizeSpoken(heard.ToString());
        if (spoken.Length == 0) return null;
        var duration = Math.Max(0.001, cueEnd - cueStart);
        if (covered / duration < TrimMinCoverage) return null;
        var nearbyNorm = string.IsNullOrWhiteSpace(nearbySpoken)
            ? ""
            : FoldDigits(new string(nearbySpoken.Where(char.IsLetterOrDigit).ToArray()));
        var seen = cueText.EnumerateRunes().ToArray();
        var seenLetters = seen.Count(Rune.IsLetterOrDigit);
        if (seenLetters == 0) return null;
        // If the cue already fully matches what the ear spoke there is nothing to trim (do not nibble away
        // correct characters — a bug caught 17/09: "冷氏集团" was trimmed to "冷氏集").
        var cueNorm = FoldDigits(new string(cueText.Where(char.IsLetterOrDigit).ToArray()));
        if (spoken.Contains(cueNorm, StringComparison.Ordinal)) return null;
        return FindKeptByEdgeTrim(seen, seenLetters, spoken,
            cueStart, cueEnd, firstStart, lastEnd, nearbyNorm);
    }

    // Find an edge trim (total trimmed characters ≤ TrimMaxRun, preferring the fewest trimmed) whose
    // remainder is a SUBSTRING of what the ear spoke. Shared by TrimEdges (trim edge junk)
    // and TrimRepeatTiles (trim leftover fragments after removing repeated walls). floorLetters > 0 keeps
    // the "keep ≥ half the cue" rule (TrimEdges); = 0 means a short core is still valid if the
    // ear confirms it verbatim. Keeping 1 character is accepted (trims are always
    // non-Han so the total trim cap limits cues to ≤5 runes; an ear-confirmed core).
    private static string? FindKeptByEdgeTrim(
        Rune[] seen, int floorLetters, string spoken,
        double cueStart, double cueEnd, double firstStart, double lastEnd,
        string nearbyNorm)
    {
        for (var total = 1; total <= TrimMaxRun; total++)
        {
            for (var trailing = Math.Min(total, seen.Length); trailing >= 0; trailing--)
            {
                var leading = total - trailing;
                if (leading > seen.Length - trailing) continue;
                // Edge anchor: no evidence at the edge means no trim of that edge.
                if (leading > 0 && !(firstStart <= cueStart + TrimEdgeAnchorSeconds)) continue;
                if (trailing > 0 && !(lastEnd >= cueEnd - TrimEdgeAnchorSeconds)) continue;
                var cutRunes = seen.Take(leading)
                    .Concat(seen.Skip(seen.Length - trailing).Take(trailing))
                    .ToArray();
                // IRON RULE: forbid cutting Han characters (every observed wrongful nibble
                // cut Han characters; the junk users need trimmed is all digits/marks/latin).
                if (cutRunes.Any(IsHanLetter)) continue;
                // NUMERAL BOUNDARY (18/09): forbid splitting/absorbing an already ear-confirmed numeral cluster.
                // R1: a trim boundary adjacent to a kept-digit boundary (18岁->8岁 changes the value!) ->
                // reject. R2: digits inside the trimmed part that the ear DOES speak (folded) ->
                // reject (those digits may be real; keeping as-is is safer, even though vetoing
                // the whole cluster may let some through). Digits include ASCII + fullwidth.
                if (SplitsDigitRun(seen, leading, trailing)) continue;
                if (cutRunes.Any(rune => IsDigitChar(rune.Value)
                    && spoken.Contains(rune.ToString(), StringComparison.Ordinal))) continue;
                // R3 (video Ca Lop 19/09: cue "A…2…35?" had its head trimmed to "A…2"): forbid
                // trimming when the text to trim belongs to a latin+digit token (A2, p01, 1080P):
                // hybrid tokens are usually names/ranks/codes — trimming changes meaning, keep.
                if (CutTouchesDigitToken(seen, leading, trailing)) continue;
                // Neighbour veto: text to trim that the ear speaks nearby is kept.
                if (nearbyNorm.Length > 0)
                {
                    var cutNorm = FoldDigits(string.Concat(cutRunes.Where(Rune.IsLetterOrDigit)));
                    if (cutNorm.Length > 0
                        && nearbyNorm.Contains(cutNorm, StringComparison.Ordinal)) continue;
                }
                var keptRunes = seen.Skip(leading).Take(seen.Length - leading - trailing).ToArray();
                var keptLetters = keptRunes.Count(Rune.IsLetterOrDigit);
                // Trims already forbid Han characters above, so keeping 1 ear-confirmed
                // character is fine (那 2 -> 那); the total trim cap limits cues to ≤5 runes.
                if (keptLetters < 1) continue;
                if (floorLetters > 0 && keptLetters * 2 < floorLetters) continue;
                var keptText = string.Concat(keptRunes).Trim();
                if (keptText.Length == 0) continue;
                var keptNorm = FoldDigits(new string(keptText.Where(char.IsLetterOrDigit).ToArray()));
                if (keptNorm.Length == 0) continue;
                if (spoken.Contains(keptNorm, StringComparison.Ordinal)) return keptText;
            }
        }
        return null;
    }

    // Gate applying trim fixes (18/09): strong/review/low-confidence as before, OR a trim that only
    // removes non-Han characters CONTAINING digits/latin (danmaku/counter junk: 482, 0101,
    // 那 2, boss, DEC — cannot nibble Han meaning). Pure punctuation (。、？) still
    // needs the old gate (keeps sentence tone). Shared between runtime + offline-harness
    // (a single source, no drift).
    public static bool ShouldApplyTrimFix(
        bool strongMatch, bool needsReview, double confidence,
        string oldText, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText)
            || string.Equals(newText, oldText, StringComparison.Ordinal)) return false;
        if (strongMatch || needsReview || confidence < OcrTemporalFilter.FallbackTriggerConfidence)
            return true;
        // High-confidence cue with a partial ear match: only trims of digits/marks/latin containing digits/latin are allowed.
        var removedDigitLatin = false;
        var remaining = new Dictionary<int, int>();
        foreach (var rune in newText.EnumerateRunes())
            if (remaining.ContainsKey(rune.Value)) remaining[rune.Value]++;
            else remaining[rune.Value] = 1;
        foreach (var rune in oldText.EnumerateRunes())
        {
            if (remaining.TryGetValue(rune.Value, out var count) && count > 0)
                remaining[rune.Value] = count - 1;
            else if (IsHanLetter(rune)) return false;
            else if (IsDigitChar(rune.Value) || IsLatinLetter(rune.Value)) removedDigitLatin = true;
        }
        foreach (var leftover in remaining.Values)
            if (leftover != 0) return false;
        return removedDigitLatin;
    }

    private static bool IsLatinLetter(int value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
        >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A;

    private static string NormalizeSpoken(string text) =>
        FoldDigits(new(text.Where(char.IsLetterOrDigit).ToArray()));

    // Remove repeated background text walls (18/09, tightened: only remove NON-HAN tiles — repeated digits/marks/latin
    // like 482/0101/()/♡; Han tiles are NOT removed (iron rule against wrongful nibbling:
    // the ear missing proper nouns is normal, removing Han is silent data loss)):
    // a 2-6 character repeated tile (grouping a SAME-LENGTH non-Han VARIANT family, similarity
    // ≥0.66) that the ear NEVER speaks (neither in the narrow window nor nearby) is removed, keeping the core
    // the ear does speak. 3 layers of protection (safety first):
    // (1) only remove tile families absent from the ear text — numerals the ear speaks (2023年) stay unchanged;
    //     the threshold only needs ≥2 because ear-absence is already the main evidence;
    // (2) the remaining core must appear VERBATIM in the ear + ≥1 character — never
    //     nibble characters the ear speaks (leftover edge fragments are trimmed further via
    //     FindKeptByEdgeTrim, which also forbids Han);
    // (3) the ear fully covers (confidence ≥ floor) + anchors on both ends like TrimEdges.
    // Do NOT apply the "keep ≥ half the cue" rule: a repeat wall can occupy most of the cue (the
    // evidence for removal is the repetition itself + ear absence, not a ratio).
    // Return null when nothing can be removed (including when norm already fully matches the ear).
    public const int PeelTileMinLength = 2;
    public const int PeelTileMaxLength = 6;
    public const int PeelMinFamilyCount = 2;
    // Separate threshold for HAN-tile families (video Ca Lop 19/09: the danmaku wall
    // 陪一支, UI, comments — all Han, the old rule never removed them so they all survived):
    // Han tiles are only removed when repeated ≥3 times (genuinely repeated dialogue like rap/choruses the ear DOES hear
    // so spokenCount>0 already kept them; the ear missing + an exact repeat ≥3 within one cue is extremely rare).
    public const int PeelHanMinFamilyCount = 3;
    public const int PeelMaxRounds = 3;
    public const double PeelFamilySimilarity = 0.66;

    public static string? TrimRepeatTiles(
        string cueText, double cueStart, double cueEnd,
        IReadOnlyList<EditorWordTiming> words, string? nearbySpoken = null)
    {
        if (string.IsNullOrWhiteSpace(cueText) || words is null) return null;
        var heard = new StringBuilder();
        var covered = 0.0;
        var firstStart = double.PositiveInfinity;
        var lastEnd = double.NegativeInfinity;
        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
            if (!double.IsFinite(word.Probability) || word.Probability < TrimWordProbFloor) return null;
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
            heard.Append(word.Text);
            covered += Math.Max(0, Math.Min(word.End, cueEnd) - Math.Max(word.Start, cueStart));
            if (word.Start < firstStart) firstStart = word.Start;
            if (word.End > lastEnd) lastEnd = word.End;
        }
        var spoken = NormalizeSpoken(heard.ToString());
        if (spoken.Length == 0) return null;
        var duration = Math.Max(0.001, cueEnd - cueStart);
        if (covered / duration < TrimMinCoverage) return null;
        if (!(firstStart <= cueStart + TrimEdgeAnchorSeconds)) return null;
        if (!(lastEnd >= cueEnd - TrimEdgeAnchorSeconds)) return null;
        // Shared confidence floor 0.75 like TrimEdges (removes the whole non-Han part, the core still
        // must be ear-confirmed verbatim + full coverage/anchor/veto).
        var nearbyNorm = string.IsNullOrWhiteSpace(nearbySpoken)
            ? ""
            : FoldDigits(new string(nearbySpoken.Where(char.IsLetterOrDigit).ToArray()));
        // Rune map (keeping punctuation inside the core): subtitle cues are BMP-only in practice; hitting a
        // non-BMP character is fail-closed (the UTF-16 map is misaligned) — better not to remove.
        var runes = cueText.EnumerateRunes().ToArray();
        if (runes.Any(rune => !rune.IsBmp)) return null;
        var normRunes = new List<int>();
        var normBuilder = new StringBuilder();
        for (var i = 0; i < runes.Length; i++)
        {
            if (!Rune.IsLetterOrDigit(runes[i])) continue;
            normRunes.Add(i);
            normBuilder.Append(runes[i].ToString());
        }
        var original = normBuilder.ToString();
        if (original.Length == 0) return null;
        if (spoken.Contains(original, StringComparison.Ordinal)) return null;
        // Removal loop: each round removes one tile family; stop when the core matches the ear or families run out.
        // alive = positions of the norm string still alive (mapped back to normRunes -> runes).
        var alive = Enumerable.Range(0, normRunes.Count).ToList();
        string Current() => string.Concat(alive.Select(pos => runes[normRunes[pos]].ToString()));
        var peeled = false;
        var hanPeeled = false;
        for (var round = 0; round < PeelMaxRounds; round++)
        {
            var current = Current();
            if (spoken.Contains(current, StringComparison.Ordinal)) break;
            if (!TryFindPeelFamily(current, spoken, nearbyNorm,
                    out var members, out _, out var roundHan))
                break;
            var condemned = new HashSet<int>();
            foreach (var member in members.OrderByDescending(member => member.Length))
            {
                var from = 0;
                while (true)
                {
                    var at = current.IndexOf(member, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    var overlap = false;
                    for (var k = at; k < at + member.Length; k++)
                        if (condemned.Contains(k)) { overlap = true; break; }
                    if (!overlap)
                        for (var k = at; k < at + member.Length; k++) condemned.Add(k);
                    from = at + member.Length;
                }
            }
            if (condemned.Count == 0) break;
            alive = alive.Where((_, index) => !condemned.Contains(index)).ToList();
            peeled = true;
            if (roundHan) hanPeeled = true;
        }
        if (!peeled) return null;
        var finalNorm = Current();
        if (finalNorm.Length == 0) return null;
        // Build the post-removal runes: keep live character runes + punctuation/space whose LEFT-adjacent
        // character is still alive (left-attached punctuation like ，。/space after a core character is kept; punctuation inside
        // a removed tile region or at the sentence head is dropped — space between 2 cores ("hi there" when the
        // tile is in the middle) is kept via the left-adjacency rule, excess space at head/tail is cleaned by Trim()).
        var liveSet = new HashSet<int>(alive);
        var liveRunes = new HashSet<int>();
        foreach (var np in alive) liveRunes.Add(normRunes[np]);
        var normPosOfRune = new Dictionary<int, int>();
        for (var p = 0; p < normRunes.Count; p++) normPosOfRune[normRunes[p]] = p;
        var peeledRunes = new List<Rune>();
        for (var r = 0; r < runes.Length; r++)
        {
            if (normPosOfRune.TryGetValue(r, out var np))
            {
                if (liveSet.Contains(np)) peeledRunes.Add(runes[r]);
            }
            else if (r > 0 && liveRunes.Contains(r - 1))
            {
                peeledRunes.Add(runes[r]);
            }
        }
        var peeledArr = peeledRunes.ToArray();
        if (peeledArr.Count(Rune.IsLetterOrDigit) < TrimMinKept) return null;
        // A completely clean core -> return it; leftover edge fragments -> trim edges further (no half rule).
        if (spoken.Contains(finalNorm, StringComparison.Ordinal))
        {
            var direct = string.Concat(peeledArr).Trim();
            if (direct.Length == 0) return null;
            var directNorm = new string(direct.Where(char.IsLetterOrDigit).ToArray());
            if (directNorm.Length == 0) return null;
            return direct;
        }
        // If a Han tile family was removed but the remaining core is not ear-confirmed verbatim then
        // STOP (fail-closed): forbid falling back to edge trimming — picking up 1 Han character from a wall that
        // matches spoken text is silent data loss. Keep the original cue for judge/F2 to handle.
        if (hanPeeled) return null;
        return FindKeptByEdgeTrim(peeledArr, 0, spoken,
            cueStart, cueEnd, firstStart, lastEnd, nearbyNorm);
    }

    // Find a tile family to remove: a SEED (2-6 character tile) must self-repeat ≥ the threshold (independent repeat evidence — a tile appearing only inside
    // another tile's span is not mature enough to seed); family = SAME-LENGTH tiles with
    // similarity ≥0.66 (differing by ≤1 character: group misread variants; longer tiles containing extra
    // adjacent characters must NOT be grouped — removing their span would eat real characters, caught by tests 2
    // times). Han tiles only group into Han families (repeat threshold ≥3). The narrow ear window misses the whole family + veto if ANY
    // member appears widely nearby. Pick the largest family (ties:
    // longer tile first). Return false when no family qualifies.
    private static bool TryFindPeelFamily(
        string current, string spoken, string nearbyNorm,
        out List<string> members, out int familyCount, out bool seedIsHan)
    {
        members = [];
        familyCount = 0;
        seedIsHan = false;
        var bestLen = 0;
        var seenTiles = new HashSet<string>(StringComparer.Ordinal);
        for (var len = PeelTileMinLength; len <= PeelTileMaxLength; len++)
        {
            for (var i = 0; i + len <= current.Length; i++)
            {
                var tile = current.Substring(i, len);
                if (!seenTiles.Add(tile)) continue;
                // Han seed: allowed through but with a higher repeat threshold (genuinely repeated
                // dialogue the ear hears is already kept by spokenCount; see the const).
                var hanSeed = ContainsHan(tile);
                // A seed that does not self-repeat is rejected immediately (even when grouped fragments/variants
                // reach the count — that is span overlap, not repeat evidence).
                if (CountOccurrences(current, tile) < (hanSeed ? PeelHanMinFamilyCount : PeelMinFamilyCount)) continue;
                var family = new List<string> { tile };
                for (var len2 = PeelTileMinLength; len2 <= PeelTileMaxLength; len2++)
                {
                    for (var j = 0; j + len2 <= current.Length; j++)
                    {
                        var other = current.Substring(j, len2);
                        if (string.Equals(other, tile, StringComparison.Ordinal)) continue;
                        // Only same length groups together.
                        if (other.Length != tile.Length) continue;
                        // Han tiles only group into Han families (never pull Han into
                        // non-Han families or vice versa).
                        if (ContainsHan(other) != hanSeed) continue;
                        if (OcrTextCleanup.TextSimilarity(tile, other) < PeelFamilySimilarity) continue;
                        if (!family.Contains(other)) family.Add(other);
                    }
                }
                var ocrCount = 0;
                foreach (var member in family) ocrCount += CountOccurrences(current, member);
                if (ocrCount < (hanSeed ? PeelHanMinFamilyCount : PeelMinFamilyCount)) continue;
                var spokenCount = 0;
                foreach (var member in family) spokenCount += CountOccurrences(spoken, member);
                if (spokenCount > 0) continue;
                if (nearbyNorm.Length > 0
                    && family.Any(member => nearbyNorm.Contains(member, StringComparison.Ordinal))) continue;
                // On ties prefer the LONGER tile (a complete span beats a fragment).
                if (ocrCount > familyCount || (ocrCount == familyCount && len > bestLen))
                {
                    members = family;
                    familyCount = ocrCount;
                    bestLen = len;
                    seedIsHan = hanSeed;
                }
            }
        }
        return members.Count > 0;
    }

    private static int CountOccurrences(string hay, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var from = 0;
        while (true)
        {
            var at = hay.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return count;
            count++;
            from = at + needle.Length;
        }
    }

    private static bool ContainsHan(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (IsHanLetter(rune)) return true;
        return false;
    }

    private static bool IsDigitChar(int value) =>
        value is >= '0' and <= '9' or >= 0xFF10 and <= 0xFF19;

    // true if a trim touches a mixed letter+digit token (R3): the latin/digit text to trim
    // belongs to a continuous ASCII-alnum run that contains BOTH letters and digits (A2,
    // p01, 1080P) — trimming changes meaning/loses the code, keep as-is. Pure-digit runs (482,
    // 0101: decided by R1/R2) and pure-letter runs (boss, DEC: trim as before) are not affected.
    // The run only extends over latin/digits (does not cross Han characters: digits adjacent to Han like
    // "482出" still belong to the old R1/R2 territory).
    private static bool CutTouchesDigitToken(Rune[] seen, int leading, int trailing)
    {
        var cut = new bool[seen.Length];
        for (var i = 0; i < leading && i < seen.Length; i++) cut[i] = true;
        for (var i = 0; i < trailing && i < seen.Length; i++) cut[seen.Length - 1 - i] = true;
        for (var i = 0; i < seen.Length; i++)
        {
            if (!cut[i] || !IsLatinOrDigit(seen[i].Value)) continue;
            var left = i;
            while (left - 1 >= 0 && IsLatinOrDigit(seen[left - 1].Value)) left--;
            var right = i;
            while (right + 1 < seen.Length && IsLatinOrDigit(seen[right + 1].Value)) right++;
            var hasLetter = false;
            var hasDigit = false;
            for (var k = left; k <= right; k++)
            {
                if (IsDigitChar(seen[k].Value)) hasDigit = true;
                else hasLetter = true;
            }
            if (hasLetter && hasDigit) return true;
        }
        return false;
    }

    private static bool IsLatinOrDigit(int value) => IsLatinLetter(value) || IsDigitChar(value);

    // true if a trim splits a numeral cluster (a trim boundary adjacent to a kept-digit boundary at head/tail).
    private static bool SplitsDigitRun(Rune[] seen, int leading, int trailing)
    {
        if (leading >= 1 && leading <= seen.Length - 1
            && IsDigitChar(seen[leading - 1].Value) && IsDigitChar(seen[leading].Value))
            return true;
        if (trailing >= 1 && trailing <= seen.Length - 1
            && IsDigitChar(seen[seen.Length - trailing].Value)
            && IsDigitChar(seen[seen.Length - trailing - 1].Value))
            return true;
        return false;
    }

    // The ear corrects one character the eye misread (video Ca Lop 19/09: 19 cues with isolated character errors, the ear
    // heard it correctly — e.g. 5137.7s An May): align the cue with the ear sentence via a sliding window;
    // exactly 1 rune differs (both are Han — a character swap, not inserted junk),
    // traditional/simplified equivalents do not count as a difference, every ear word covering the cue has confidence
    // ≥ CorrectionMinWordProb, the cue is ≥3 long, and the REPLACEMENT must be SPOKEN BY THE EAR NEAR THE CUE
    // TIME (inside the word covering the cue ±0.3s — prevents random matches with a distant ear sentence).
    // Add a whole-sentence similarity barrier (19/09: the An May replay had 21/23 fixes matching random
    // permutations — a short 4-character cue falling into the 1-diff window of a distant ear sentence):
    // the cue must resemble the ear sentence ≥0.5 (same sentence with 1 wrong character), not a random permutation.
    // Ambiguity (0 or ≥2 candidates) returns null — keep as-is, fail-closed.
    public const double CorrectionMinWordProb = 0.85;
    public const int CorrectionMinCueRunes = 3;
    public const double CorrectionMinSimilarity = 0.5;

    public static string? TryCorrectSingleChar(
        string cueText,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? overlapping,
        IReadOnlyList<EditorWordTiming>? narrowWords)
    {
        if (string.IsNullOrWhiteSpace(cueText) || overlapping is null || narrowWords is null) return null;
        var cueRunes = cueText.EnumerateRunes().ToArray();
        var cueNorm = new List<Rune>();
        var cueMap = new List<int>();
        for (var i = 0; i < cueRunes.Length; i++)
        {
            if (!Rune.IsLetterOrDigit(cueRunes[i])) continue;
            cueNorm.Add(cueRunes[i]);
            cueMap.Add(i);
        }
        if (cueNorm.Count < CorrectionMinCueRunes) return null;
        var wordsOk = false;
        foreach (var word in narrowWords)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
            if (!double.IsFinite(word.Probability) || word.Probability < CorrectionMinWordProb) return null;
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
            wordsOk = true;
        }
        if (!wordsOk) return null;
        // Whole-sentence similarity barrier: the cue must resemble AT LEAST 1 ear sentence ≥0.5.
        var resembles = false;
        foreach (var segment in overlapping)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            if (OcrTextCleanup.TextSimilarity(cueText, segment.Text) >= CorrectionMinSimilarity)
            {
                resembles = true;
                break;
            }
        }
        if (!resembles) return null;
        var narrowText = string.Concat(narrowWords
            .Where(word => word is not null && !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => word.Text));
        var foundIndex = -1;
        Rune foundRune = default;
        var foundCount = 0;
        foreach (var segment in overlapping)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Text)) continue;
            var segNorm = segment.Text.EnumerateRunes().Where(Rune.IsLetterOrDigit).ToArray();
            if (segNorm.Length < cueNorm.Count) continue;
            for (var w = 0; w + cueNorm.Count <= segNorm.Length; w++)
            {
                var diffAt = -1;
                var diffs = 0;
                for (var k = 0; k < cueNorm.Count; k++)
                {
                    var a = cueNorm[k].Value;
                    var b = segNorm[w + k].Value;
                    if (a == b) continue;
                    // Simplified/traditional is 1 character (shared tracking map) — not an error.
                    if (SubtitleTracker.CanonicalTrackingRune(a) == SubtitleTracker.CanonicalTrackingRune(b)) continue;
                    diffs++;
                    diffAt = k;
                    if (diffs > 1) break;
                }
                if (diffs != 1) continue;
                // Han↔Han character swap (not inserted junk/punctuation).
                if (!IsHanLetter(cueNorm[diffAt]) || !IsHanLetter(segNorm[w + diffAt])) continue;
                // The replacement must be SPOKEN BY THE EAR NEAR THE CUE TIME (inside the word covering the cue):
                // matching a distant ear sentence is random — drop this window.
                if (!narrowText.Contains(segNorm[w + diffAt].ToString(), StringComparison.Ordinal)) continue;
                if (foundCount == 0 || (foundIndex == cueMap[diffAt] && foundRune.Value == segNorm[w + diffAt].Value))
                {
                    foundIndex = cueMap[diffAt];
                    foundRune = segNorm[w + diffAt];
                    foundCount++;
                }
                else
                {
                    // Two different candidates — ambiguous, keep as-is.
                    return null;
                }
            }
        }
        if (foundCount == 0 || foundIndex < 0) return null;
        var fixedRunes = cueRunes.ToArray();
        fixedRunes[foundIndex] = foundRune;
        var fixedText = string.Concat(fixedRunes.Select(rune => rune.ToString()));
        if (string.Equals(fixedText, cueText, StringComparison.Ordinal)) return null;
        return fixedText;
    }
}

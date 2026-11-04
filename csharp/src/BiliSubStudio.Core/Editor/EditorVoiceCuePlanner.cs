using System.Text;

namespace BiliSubStudio.Core.Editor;

internal static class EditorVoiceCuePlanner
{
    private const double TouchToleranceSeconds = .05;
    // A stable caption can remain on screen for several seconds before one bad
    // OCR frame flashes at its tail. Cue count and text similarity are the real
    // safety bounds; eight seconds still prevents unrelated scene-wide merges.
    private const double FlickerMaximumSeconds = 8;
    private const double FlickerCueMaximumSeconds = .25;
    private const int FlickerMaximumCues = 8;

    public static IReadOnlyList<EditorSubtitleCue> Build(IReadOnlyList<EditorSubtitleCue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return source.ToArray();
        var result = new List<EditorSubtitleCue>(source.Count);
        for (var first = 0; first < source.Count;)
        {
            var last = first;
            var maximumEnd = source[first].End;
            while (last + 1 < source.Count && last - first + 1 < FlickerMaximumCues)
            {
                var candidate = source[last + 1];
                if (candidate.Start > maximumEnd + TouchToleranceSeconds) break;
                maximumEnd = Math.Max(maximumEnd, candidate.End);
                if (maximumEnd - source[first].Start > FlickerMaximumSeconds) break;
                last++;
            }

            var collapseEnd = FindFlickerEnd(source, first, last);
            if (collapseEnd <= first)
            {
                result.Add(source[first]);
                first++;
                continue;
            }

            var cluster = source.Skip(first).Take(collapseEnd - first + 1).ToArray();
            var representative = cluster
                .OrderByDescending(cue => cue.End - cue.Start)
                .ThenBy(cue => Normalize(cue.VietnameseText).Length)
                .ThenBy(cue => cue.Start)
                .First();
            result.Add(representative with
            {
                Start = cluster.Min(cue => cue.Start),
                End = cluster.Max(cue => cue.End),
            });
            first = collapseEnd + 1;
        }
        return result;
    }

    public static IReadOnlyList<EditorSubtitleCue> RemoveUnspeakable(IReadOnlyList<EditorSubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return cues.Where(cue => VietnameseTtsTextNormalizer.HasSpeakableUnits(cue.VietnameseText)).ToArray();
    }

    // Patch the voice window before reading (23/09): a too-narrow timecode sliver (<0.15s) cannot be
    // read by any voice -> the worker keeps an unfair silence. Two patch mechanisms, both
    // NOT touching text, NOT encroaching on another line, NOT changing order; 07/10 addition:
    // do NOT move Start earlier than the subtitle time (only extend End to the right):
    // 1. Absorb an EXACTLY identical sliver: a narrow adjacent sliver (gap 0..1s, no overlap) whose text
    //    matches letter for letter is merged into the healthy line (keeping its Id + text, extending the
    //    frame to cover the sliver). Merge only when one side is narrow and the other healthy so healthy
    //    repeated dialogue is never merged by mistake.
    // 2. Extend the remaining narrow sliver into the RIGHT silence (max 2s, leaving 0.05s
    //    before the next line). A sliver touching both sides has no room to extend -> leave as is
    //    for the worker to handle (silence + review flag, rare).
    public static IReadOnlyList<EditorSubtitleCue> RepairVoiceWindows(IReadOnlyList<EditorSubtitleCue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const double MinVoiceWindowSeconds = .15;
        const double MaxAbsorbGapSeconds = 1.0;
        const double MaxExtendSeconds = 2.0;
        const double NeighborClearanceSeconds = .05;
        if (source.Count < 2) return source.ToArray();
        static bool Sliver(EditorSubtitleCue cue) => cue.End - cue.Start < MinVoiceWindowSeconds;
        static bool Healthy(EditorSubtitleCue cue) => cue.End - cue.Start >= MinVoiceWindowSeconds;
        static bool Identical(string left, string right) =>
            !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal);

        var absorbed = new List<EditorSubtitleCue>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var cue = source[index];
            if (Sliver(cue))
            {
                if (absorbed.Count > 0)
                {
                    var prev = absorbed[^1];
                    var gap = cue.Start - prev.End;
                    if (Healthy(prev) && gap >= 0 && gap <= MaxAbsorbGapSeconds
                        && Identical(cue.VietnameseText, prev.VietnameseText))
                    {
                        absorbed[^1] = prev with { End = Math.Max(prev.End, cue.End) };
                        continue;
                    }
                }
                if (index + 1 < source.Count)
                {
                    var next = source[index + 1];
                    var gap = next.Start - cue.End;
                    if (Healthy(next) && gap >= 0 && gap <= MaxAbsorbGapSeconds
                        && Identical(cue.VietnameseText, next.VietnameseText))
                    {
                        absorbed.Add(next with { Start = Math.Min(next.Start, cue.Start) });
                        index++;
                        continue;
                    }
                }
            }
            absorbed.Add(cue);
        }

        var repaired = new List<EditorSubtitleCue>(absorbed.Count);
        for (var index = 0; index < absorbed.Count; index++)
        {
            var cue = absorbed[index];
            if (!Sliver(cue) || string.IsNullOrWhiteSpace(cue.VietnameseText))
            {
                repaired.Add(cue);
                continue;
            }
            var start = cue.Start;
            var end = cue.End;
            if (index + 1 < absorbed.Count)
            {
                var next = absorbed[index + 1];
                // Extend to the right only when there is a real gap (leaving 0.05s before the next line); stop if they touch exactly.
                // Never extend Start backwards (07/10): the subtitle time is absolute, the voice must not read early.
                if (next.Start - NeighborClearanceSeconds > end)
                    end = Math.Min(next.Start - NeighborClearanceSeconds, end + MaxExtendSeconds);
            }
            repaired.Add(end == cue.End ? cue : cue with { End = end });
        }
        return repaired;
    }

    // Widen a TIGHT cue using the adjacent RIGHT silence (01/10, the L-pathological rule; 07/10 the user
    // requires that the subtitle shown is also what the voice reads, so borrowing the left gap is FORBIDDEN:
    // Start never goes earlier than the subtitle time, only End extends to the right for reading room.
    // A cue whose pace requirement exceeds natural pace (need > 1) borrows time from the RIGHT gap
    // to lower need — fixing the cause instead of forcing the voice. NO fixed ceiling: borrow exactly
    // the missing amount (required - window), taking as much as the right gap allows (leaving 0.05s
    // before the next cue), never encroaching/overrunning another cue, not touching text, not
    // changing order, Start kept absolutely fixed. VieNeu only (needs the pace
    // preset table); Piper stays as is. Runs after RepairVoiceWindows, before MapToCues
    // so manifest/validator see the widened frame as ground truth. If the right gap is not
    // enough, the worker's atempo ≤1.5x (micro 2.5x) picks up the rest (still review).
    // Not dependent on traversal order (every boundary is computed from the original cue).
    public static IReadOnlyList<EditorSubtitleCue> WidenTightWindows(
        IReadOnlyList<EditorSubtitleCue> source, string voice, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0 || !TtsVoiceCatalog.IsVieneuVoice(voice)) return source.ToArray();
        var pace = TtsVoiceCatalog.PaceWordsPerSecond(voice);
        if (pace <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return source.ToArray();
        const double NeighborClearanceSeconds = .05;
        var widened = new List<EditorSubtitleCue>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var cue = source[index];
            var window = cue.End - cue.Start;
            var words = CountSpeakableWords(cue.VietnameseText);
            if (window <= 0 || words <= 0)
            {
                widened.Add(cue);
                continue;
            }
            // GPU measurement 02-03/10 (Ngoc Huyen 4.36 chars/s): short-cue overhead scales by
            // character count, because the prosody lead-in and the trailing punctuation fall on the last char:
            // 1 char "Dung" +0.19s; 2 chars "Đừng hoảng." +0.52s, "Cẩn thận."
            // +0.66s, "Không động." +0.67s ("Vào đi." bloat +0.9s); 3 chars
            // "Đừng cựa quậy." +0.35s. Without this tier the estimator reports need 0.999
            // (falsely an exact fit) and then the worker dies at the real need of 1.7-2.1x (issues #2837,
            // #6470). If the gap is insufficient, the worker's atempo/micro-band + review cover
            // the remainder (the ceiling stays as is, no widening).
            var overhead = words <= 1 ? .27 : words == 2 ? .70 : words <= 4 ? .40 : 0;
            var required = words / pace + overhead;
            var need = required / window;
            if (need <= 1)
            {
                widened.Add(cue);
                continue;
            }
            var shortfall = required - window;
            // Borrow the RIGHT gap first (07/10): the subtitle appears when the voice
            // speaks, so the voice never reads early while any trailing silence exists.
            // rightEdge is computed from the original cue (the next cue's Start is never
            // extended earlier any more), so traversal order does not affect the result.
            var rightEdge = index == source.Count - 1
                ? durationSeconds
                : source[index + 1].Start - NeighborClearanceSeconds;
            var rightAvail = Math.Max(0, rightEdge - cue.End);
            var take = Math.Min(shortfall, rightAvail);
            var end = Math.Min(durationSeconds, cue.End + take);
            var remaining = shortfall - take;
            // F1 (17/11, user-approved): when the trailing gap runs out, a cue used to lose
            // the tail of its sentence (the heard defect in an exported video), because the
            // 1.5x pace ceiling cannot cover the shortfall either. Borrow the LEFT gap instead
            // - reading slightly EARLY is allowed, and the voice is capped at MaxLeadSeconds so
            // it never drifts away from its subtitle. Only the remainder after the right gap is
            // borrowed, and the left edge keeps the 0.05s clearance from the previous cue.
            var start = cue.Start;
            if (remaining > 1e-9)
            {
                // The left edge uses the PREVIOUS cue's already-widened End (that cue may
                // have borrowed into this very gap), so a lead never touches its neighbour.
                var leftEdge = index == 0 || widened.Count < index
                    ? 0
                    : widened[widened.Count - 1].End + NeighborClearanceSeconds;
                var leftAvail = Math.Max(0, cue.Start - leftEdge);
                var lead = Math.Min(remaining, Math.Min(leftAvail, TtsTimingLimits.MaxLeadSeconds));
                if (lead > 1e-9) start = Math.Max(0, cue.Start - lead);
            }
            widened.Add(start == cue.Start && end == cue.End ? cue : cue with { Start = start, End = end });
        }
        return widened;
    }

    private static int CountSpeakableWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var character in token)
            {
                if (char.IsLetterOrDigit(character))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static int FindFlickerEnd(IReadOnlyList<EditorSubtitleCue> cues, int first, int last)
    {
        if (last <= first) return first;
        for (var right = last; right > first; right--)
        {
            if (cues.Skip(first).Take(right - first + 1).All(cue => cue.End - cue.Start > FlickerCueMaximumSeconds))
                continue;
            for (var index = first; index <= right; index++)
            {
                for (var previous = Math.Max(first, index - 2); previous < index; previous++)
                    if (LooksLikeSameCaption(cues[previous].VietnameseText, cues[index].VietnameseText))
                        return right;
            }
        }
        return first;
    }

    private static bool LooksLikeSameCaption(string left, string right)
    {
        var first = Normalize(left);
        var second = Normalize(right);
        if (first.Length < 2 || second.Length < 2) return false;
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        if (shorter.Length >= 6 && shorter.Length * 100 >= longer.Length * 65
            && longer.Contains(shorter, StringComparison.Ordinal))
            return true;

        var firstTokens = Tokens(left);
        var secondTokens = Tokens(right);
        var minimum = Math.Min(firstTokens.Count, secondTokens.Count);
        return minimum >= 3 && firstTokens.Intersect(secondTokens).Count() * 100 >= minimum * 60;
    }

    private static HashSet<string> Tokens(string value) => value
        .Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Normalize)
        .Where(token => token.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }
}

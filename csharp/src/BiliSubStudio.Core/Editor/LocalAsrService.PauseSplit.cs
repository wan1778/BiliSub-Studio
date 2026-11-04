using System.Text;

namespace BiliSubStudio.Core.Editor;

// Split sentences on silence gaps (23/09): Whisper collapses a whole long stretch into 1 sentence, and the
// pauses fall inside it, so subtitles drag on and the voice reads in a run-on chunk. After
// finishing each stretch, look at each character's timestamp (already available, no re-listening): a silence gap >= 0.6s
// becomes its own sentence, with characters split at their own exact times. A piece below 0.3s
// attaches to a neighbouring sentence, avoiding truncated sentences. A sentence longer than 10s with no wide-enough gap
// is cut at the widest internal silence gap. Pure logic; VAD/earphones are not touched.
internal sealed partial class LocalAsrService
{
    internal const double PauseSplitGapSeconds = 0.6;
    internal const double PauseSplitMinPieceSeconds = 0.3;
    internal const double PauseSplitMaxUnsplitSeconds = 10.0;

    private static List<AsrCue> SplitOnPauses(AsrCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        var words = cue.Words.OrderBy(x => x.Start).ToList();
        if (words.Count < 2) return [cue];

        var candidates = new List<int>();
        for (var index = 0; index + 1 < words.Count; index++)
        {
            if (words[index + 1].Start - words[index].End >= PauseSplitGapSeconds)
                candidates.Add(index);
        }
        if (candidates.Count == 0 && cue.End - cue.Start > PauseSplitMaxUnsplitSeconds)
        {
            // Abnormally long sentence with no wide-enough pause: cut at the widest
            // gap so subtitles do not drag on indefinitely (the voice still reads every character).
            var best = -1;
            var widest = 0d;
            for (var index = 0; index + 1 < words.Count; index++)
            {
                var gap = words[index + 1].Start - words[index].End;
                if (gap > widest) { widest = gap; best = index; }
            }
            if (best >= 0) candidates.Add(best);
        }
        if (candidates.Count == 0) return [cue];

        var pieces = new List<AsrCue>();
        var pieceStart = 0;
        foreach (var boundary in candidates.Distinct().OrderBy(x => x))
        {
            var piece = BuildPiece(cue, words, pieceStart, boundary);
            if (piece is not null) pieces.Add(piece);
            pieceStart = boundary + 1;
        }
        var tail = BuildPiece(cue, words, pieceStart, words.Count - 1);
        if (tail is not null) pieces.Add(tail);

        // Fragments (<0.3s) attach to a neighbouring sentence: a leading fragment folds down, middle/trailing fragments fold up.
        if (pieces.Count >= 2 && IsShortPiece(pieces[0]))
        {
            var second = pieces[1];
            pieces[1] = Absorb(second, pieces[0], extendStart: true);
            pieces.RemoveAt(0);
        }
        var merged = new List<AsrCue>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (merged.Count > 0 && IsShortPiece(piece))
                merged[^1] = Absorb(merged[^1], piece, extendStart: false);
            else
                merged.Add(piece);
        }
        return merged;
    }

    private static bool IsShortPiece(AsrCue piece) =>
        piece.End - piece.Start < PauseSplitMinPieceSeconds;

    private static AsrCue Absorb(AsrCue keeper, AsrCue absorbed, bool extendStart)
    {
        var words = extendStart
            ? absorbed.Words.Concat(keeper.Words).ToList()
            : keeper.Words.Concat(absorbed.Words).ToList();
        return keeper with
        {
            Start = extendStart ? Math.Min(keeper.Start, absorbed.Start) : keeper.Start,
            End = extendStart ? keeper.End : Math.Max(keeper.End, absorbed.End),
            Words = words,
            Text = JoinPieceText(words),
        };
    }

    private static AsrCue? BuildPiece(AsrCue cue, List<AsrWord> words, int first, int last)
    {
        if (last < first) return null;
        var start = words[first].Start;
        var end = words[last].End;
        if (end <= start + .005) return null;
        var pieceWords = words.Skip(first).Take(last - first + 1)
            .Select(x => x with { Start = Math.Max(start, x.Start), End = Math.Min(end, x.End) })
            .Where(x => x.End > x.Start + .005)
            .ToList();
        if (pieceWords.Count == 0) return null;
        return cue with { Start = start, End = end, Words = pieceWords, Text = JoinPieceText(pieceWords) };
    }

    // Joined-sentence text derived from character times: Chinese characters (non-ASCII) stick together with no space,
    // Latin characters are separated by spaces. Previously the raw Whisper sentence was copied whole, so the
    // pauses never showed; now each sentence only carries the characters of its own frame.
    private static string JoinPieceText(IReadOnlyList<AsrWord> words)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            var text = word.Text.Trim();
            if (text.Length == 0) continue;
            if (builder.Length > 0 && char.IsAsciiLetterOrDigit(builder[^1]) && char.IsAsciiLetterOrDigit(text[0]))
                builder.Append(' ');
            builder.Append(text);
        }
        return builder.ToString().Trim();
    }
}

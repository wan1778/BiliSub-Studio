using System.Text;

namespace BiliSubStudio.Core.Editor;

// Cắt câu theo khe im (23/09): Whisper gom cả đoạn dài thành 1 câu, khoảng
// nghỉ nằm lọt bên trong nên sub lê thê và voice đọc dính chùm. Sau khi nghe
// xong mỗi đoạn, nhìn giờ từng chữ (có sẵn, không nghe lại): khe im >= 0.6s
// thì cắt thành câu riêng, chữ chia theo đúng giờ của nó. Mảnh dưới 0.3s thì
// dính vào câu bên, không để câu cụt. Câu dài quá 10s mà không có khe đủ rộng
// thì cắt ở khe im rộng nhất bên trong. Thuần logic, không đụng VAD/tai nghe.
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
            // Câu dài bất thường mà không có khe nghỉ đủ rộng: cắt ở khe rộng
            // nhất để sub không lê thê vô hạn (voice vẫn đọc đủ nguyên chữ).
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

        // Mảnh vụn (<0.3s) dính vào câu bên: đầu gộp xuống, giữa/đuôi gộp lên.
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

    // Chữ câu ghép từ giờ chữ: chữ Trung (ngoài ASCII) dính nhau không space,
    // chữ Latin cách nhau bằng space. Trước đây bê nguyên câu Whisper nên
    // khoảng nghỉ không hiện; giờ mỗi câu chỉ mang chữ của khung mình.
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

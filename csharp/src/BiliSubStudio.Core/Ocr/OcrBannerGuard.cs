namespace BiliSubStudio.Core.Ocr;

// Guard chữ nền tĩnh (banner/watermark/logo có chữ), chạy ở FINISH sau
// Reconcile (18/09, khóa plan): fix #1 bỏ drop-im-lặng thì chữ nền tĩnh đứng
// yên trong đoạn im sẽ lọt (drift không bắt được, tai thì im). Guard này lấp
// đúng cái lỗ đó — và CHỈ soi cue mắt-giữ-trong-im-lặng:
// - Cue nào tai từng khớp (MaxSimilarity >= 0.3 ở bất kỳ instance nào trong
//   nhóm) thì MIỄN VĨNH VIỄN — thoại dài, caption có đọc kèm, chorus... thoát
//   hết. Tương tác với mọi fix tai-mắt = 0 theo cấu trúc.
// - Gom nhóm theo FUZZY (>= 0.85, tái dùng TextSimilarity): watermark nhấp
//   nháy bị OCR đọc ra 2-3 biến thể vẫn gộp chung, không né được.
// - Ngưỡng TẦNG theo độ dài video (vá video ngắn): video >= 60s cần tỷ lệ
//   >= 50% VÀ cộng dồn >= 20s; video < 60s cần tỷ lệ >= 85%; mọi độ dài: 1
//   mạch liên tục >= 45s là banner (không thoại nào giữ nguyên 45s).
// Thuần logic, fail-open: thiếu dữ liệu là giữ hết. Drop ghi lý do có số để
// người duyệt thấy (không xóa âm thầm). Ghi vào .exclude.json như judge drop
// nên list cuối = SRT theo đúng đường cũ.
public static class OcrBannerGuard
{
    // Video dài mới xét tỷ lệ 50% (video ngắn 50% dễ dính caption thật).
    public const double LongVideoSeconds = 60;
    public const double ShareThresholdLong = 0.5;
    public const double MinCumulativeSecondsLong = 20;
    // Video ngắn: chỉ banner phủ gần hết mới dính.
    public const double ShareThresholdShort = 0.85;
    // 1 mạch liên tục dài cỡ này thì không thể là thoại.
    public const double MaxContinuousSeconds = 45;
    // Gom biến thể OCR của cùng 1 chữ nền (thiếu/thừa 1 ký tự).
    public const double GroupSimilarity = 0.85;
    // Miễn guard: tai từng nghe na ná ở bất kỳ instance nào (hướng AN TOÀN —
    // miễn chỉ nghĩa là guard này bỏ qua, cue vẫn qua mọi filter khác).
    public const double EarExemptSimilarity = 0.3;
    // Nối mạch hiển thị: watermark nhấp nháy tắt 0.2s vẫn tính 1 mạch.
    public const double RunJoinGapSeconds = 1.0;

    public sealed record BannerDrop(double Start, string Text, string Reason);

    public static IReadOnlyList<BannerDrop> Evaluate(
        IEnumerable<OcrCue> cues,
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        double durationSeconds)
    {
        var list = cues
            .Where(cue => cue is not null && cue.Text is not null
                && cue.End > cue.Start
                && double.IsFinite(cue.Start) && double.IsFinite(cue.End))
            .OrderBy(cue => cue.Start)
            .ToArray();
        if (list.Length == 0 || !(durationSeconds > 0) || !double.IsFinite(durationSeconds))
            return [];
        // Gom nhóm fuzzy theo chữ chuẩn hóa (union-find, ngưỡng 0.85).
        var parent = new int[list.Length];
        for (var index = 0; index < parent.Length; index++) parent[index] = index;
        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right) parent[left] = right;
        }
        for (var a = 0; a < list.Length; a++)
        {
            var textA = list[a].Text;
            if (string.IsNullOrWhiteSpace(textA)) continue;
            for (var b = a + 1; b < list.Length; b++)
            {
                var textB = list[b].Text;
                if (string.IsNullOrWhiteSpace(textB)) continue;
                if (Find(a) == Find(b)) continue;
                if (OcrTextCleanup.TextSimilarity(textA, textB) >= GroupSimilarity)
                    Union(a, b);
            }
        }
        var groups = new Dictionary<int, List<OcrCue>>();
        for (var index = 0; index < list.Length; index++)
        {
            var root = Find(index);
            if (!groups.TryGetValue(root, out var members))
            {
                members = new List<OcrCue>();
                groups[root] = members;
            }
            members.Add(list[index]);
        }
        var drops = new List<BannerDrop>();
        foreach (var members in groups.Values)
        {
            // Miễn vĩnh viễn: tai từng khớp na ná ở bất kỳ instance nào.
            if (EarHeard(speech, members)) continue;
            var (cumulative, maxRun) = Measure(members);
            var share = cumulative / durationSeconds;
            string? rule = null;
            if (maxRun >= MaxContinuousSeconds)
                rule = FormattableString.Invariant(
                    $"1 mạch liên tục {maxRun:0}s (≥ {MaxContinuousSeconds:0}s)");
            else if (durationSeconds >= LongVideoSeconds)
            {
                if (share >= ShareThresholdLong && cumulative >= MinCumulativeSecondsLong)
                    rule = FormattableString.Invariant(
                        $"chiếm {share:P0} video ({cumulative:0}s cộng dồn)");
            }
            else if (share >= ShareThresholdShort)
            {
                rule = FormattableString.Invariant(
                    $"chiếm {share:P0} video ngắn ({cumulative:0}s cộng dồn)");
            }
            if (rule is null) continue;
            var sample = members
                .OrderByDescending(cue => OcrTemporalFilter.Normalize(cue.Text).Length)
                .First().Text;
            foreach (var cue in members)
                drops.Add(new BannerDrop(cue.Start, cue.Text,
                    $"chữ nền tĩnh “{Truncate(sample, 20)}” ({rule}), không lần nào khớp tiếng"));
        }
        return drops;
    }

    // Tai từng nghe na ná nhóm này? (bất kỳ cue nào trong nhóm khớp >= 0.3
    // với đoạn thoại chồng lấn nó). Không speech = không miễn.
    internal static bool EarHeard(
        IReadOnlyList<BiliSubStudio.Core.Editor.EditorSpeechSegment>? speech,
        IReadOnlyList<OcrCue> members)
    {
        if (speech is null || speech.Count == 0) return false;
        foreach (var cue in members)
        {
            if (cue?.Text is null) continue;
            var overlapping = OcrSpeechMatch.OverlappingSegments(speech, cue.Start, cue.End);
            if (overlapping.Count == 0) continue;
            if (OcrSpeechMatch.MaxSimilarity(cue.Text, overlapping) >= EarExemptSimilarity)
                return true;
        }
        return false;
    }

    // Cộng dồn (hợp các khoảng chồng) + mạch dài nhất (nối gap <= 1s để bắt
    // watermark nhấp nháy).
    internal static (double Cumulative, double MaxRun) Measure(IReadOnlyList<OcrCue> members)
    {
        var spans = members
            .Where(cue => cue is not null && cue.End > cue.Start)
            .Select(cue => (cue.Start, cue.End))
            .OrderBy(span => span.Start)
            .ToArray();
        if (spans.Length == 0) return (0, 0);
        var cumulative = 0.0;
        var maxRun = 0.0;
        var runStart = spans[0].Start;
        var runEnd = spans[0].End;
        var unionStart = spans[0].Start;
        var unionEnd = spans[0].End;
        for (var index = 1; index < spans.Length; index++)
        {
            var (start, end) = spans[index];
            if (start <= unionEnd)
            {
                if (end > unionEnd) unionEnd = end;
            }
            else
            {
                cumulative += unionEnd - unionStart;
                unionStart = start;
                unionEnd = end;
            }
            if (start <= runEnd + RunJoinGapSeconds)
            {
                if (end > runEnd) runEnd = end;
            }
            else
            {
                if (runEnd - runStart > maxRun) maxRun = runEnd - runStart;
                runStart = start;
                runEnd = end;
            }
        }
        cumulative += unionEnd - unionStart;
        if (runEnd - runStart > maxRun) maxRun = runEnd - runStart;
        return (cumulative, maxRun);
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}

namespace BiliSubStudio.Core.Ocr;

// Dọn chữ sau reconcile (17/09): gộp cue kề trùng lặp + xóa mảnh vụn flicker.
// Thuần logic chữ (KHÔNG AI, KHÔNG hiểu ngữ nghĩa):
// - Gộp: 2 cue kề nhau (gap ≤ 0.3s) + chữ tương đồng ≥ 0.65 (sau chuẩn hóa) ->
//   1 cue (span cả hai, giữ chữ DÀI hơn = bản đọc đầy đủ hơn). Xử lý flicker
//   OCR đọc 1 câu chẻ nhiều mảnh (cue 34/35: cùng câu 休书, sim 0.69).
//   Ngưỡng 0.65 + gap 0.3s: 2 sub thật khác nhau kề nhau hiếm khi giống
//   nhau ≥65% trong 0.3s (đó chính là định nghĩa flicker).
// - Mảnh vụn: cue ≤ 2 rune kề (gap ≤ 0.3s) cue DÀI HƠN chứa hết chữ nó ->
//   mảnh flicker -> xóa (cue 31 "人。" kề cue 30 "人第一").
// KHÔNG sửa chữ đọc sai (联姻对象→眼惘叶象, 两不相干, 纠缠, câu cụt 38):
// sửa chữ cần hiểu ngữ nghĩa (AI) — giữ nguyên để user sửa tay.
public static class OcrTextCleanup
{
    public const double MergeGapSeconds = 0.3;
    public const double MergeSimilarity = 0.65;
    public const double FragmentGapSeconds = 0.3;
    // Hiệu ứng gõ-chữ/diễn tiến (21/09, ca p01 "xxx100%>xxx" hóng chuỗi 3):
    // cue sau MỞ ĐẦU bằng toàn bộ chữ cue trước (normalize) và dài hơn → cùng
    // 1 caption đang lớn dần, gộp giữ bản dài. Cửa sổ rộng hơn merge-thường
    // vì chuỗi gõ kéo dài. Chữ ngắn <3 rune không đủ tin để suy ra gõ-chữ.
    public const double PrefixGrowthGapSeconds = 0.5;
    public const int PrefixGrowthMinRunes = 3;
    public const int FragmentMaxRunes = 2;
    // Ngưỡng phantom (cue chớp nháy do flash/effect): NGẮN + KẸP SÁT + KHÁC
    // CHỮ cả hai bên. Điều kiện KHÁC CHỮ là bắt buộc: flicker cùng câu (cue
    // 34/35: ngắn + kẹp nhưng chữ GIỐNG nhau 0.69) phải để merge gộp, không
    // được xóa — chỉ cue lạ chen giữa (cue 55: sim ~0 cả hai bên) mới là rác.
    public const double PhantomMaxDuration = 0.6;
    public const double PhantomMaxGap = 0.15;
    public const double PhantomMaxSimilarity = 0.4;
    // Miễn phantom hẹp (18/09): cue đọc RẤT chắc thì không phải chớp-nháy —
    // bảo vệ thoại ngắn thật ("ừm", "hả", "ĐI"). Ngưỡng cũ giữ nguyên cho
    // cue mờ (rác flicker thường tin thấp).

    public sealed record CleanupResult(
        IReadOnlyList<OcrCue> Cues, int Merged, int FragmentsDropped, int PhantomsDropped);

    // Cue chớp nháy? Ngắn + kẹp sát cue kề + chữ khác cả hai bên hiện diện.
    // Không bên nào (đầu/cuối list) coi như gap vô cực -> giữ.
    public const double PhantomExemptConfidence = 0.9;

    public static bool IsPhantom(OcrCue? cue, OcrCue? prev, OcrCue? next)
    {
        if (cue is null || cue.Text is null) return false;
        if (cue.Confidence >= PhantomExemptConfidence) return false;
        if (!(cue.End - cue.Start < PhantomMaxDuration)) return false;
        var gapBefore = prev is null ? double.PositiveInfinity : cue.Start - prev.End;
        var gapAfter = next is null ? double.PositiveInfinity : next.Start - cue.End;
        // KẸP CẢ 2 BÊN mới là chớp-nháy (18/09: sửa && thành || — cũ xóa oan
        // thoại ngắn thật chỉ hẹp 1 bên, VD đáp "嗯" sau câu dài 0.1s).
        // Thoáng 1 bên là đủ chỗ cho 1 câu thoại thật.
        if (gapBefore >= PhantomMaxGap || gapAfter >= PhantomMaxGap) return false;
        if (prev?.Text is not null && TextSimilarity(prev.Text, cue.Text) >= PhantomMaxSimilarity)
            return false;
        if (next?.Text is not null && TextSimilarity(next.Text, cue.Text) >= PhantomMaxSimilarity)
            return false;
        return true;
    }

    public static CleanupResult Cleanup(IEnumerable<OcrCue> cues)
    {
        var ordered = cues.Where(cue => cue is not null && cue.Text is not null)
            .OrderBy(cue => cue.Start).ToArray();
        // Pass 0: xóa mảnh vụn flicker TRƯỚC (cue ≤2 rune kề cue dài hơn chứa
        // hết chữ nó) — để pass 1 (phantom) không ăn mất cue neo (VD "人第一"
        // kề "人。": neo ngắn+kẹp nhưng là câu neo, mảnh "人。" mới là rác).
        var fragDropped = new bool[ordered.Length];
        var fragments = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var cue = ordered[index];
            var norm = OcrTemporalFilter.Normalize(cue.Text);
            var runes = norm.EnumerateRunes().Count();
            if (runes == 0 || runes > FragmentMaxRunes) continue;
            foreach (var neighborIndex in new[] { index - 1, index + 1 })
            {
                if (neighborIndex < 0 || neighborIndex >= ordered.Length || fragDropped[neighborIndex]) continue;
                var neighbor = ordered[neighborIndex];
                var gap = neighborIndex < index
                    ? cue.Start - neighbor.End
                    : neighbor.Start - cue.End;
                if (gap < 0 || gap > FragmentGapSeconds) continue;
                var neighborNorm = OcrTemporalFilter.Normalize(neighbor.Text);
                if (neighborNorm.EnumerateRunes().Count() <= runes) continue;
                if (neighborNorm.Contains(norm, StringComparison.Ordinal))
                {
                    fragDropped[index] = true;
                    fragments++;
                    break;
                }
            }
        }
        var defragged = new List<OcrCue>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
            if (!fragDropped[index]) defragged.Add(ordered[index]);
        // Pass 1: xóa cue chớp nháy (flash/effect): ngắn + kẹp + khác chữ 2 bên.
        // Chạy SAU fragment (neo còn nguyên) và TRƯỚC merge (flicker cùng câu
        // còn nguyên để merge gộp đúng).
        var alive = new List<OcrCue>(defragged.Count);
        var phantoms = 0;
        for (var index = 0; index < defragged.Count; index++)
        {
            if (IsPhantom(defragged[index],
                    index > 0 ? defragged[index - 1] : null,
                    index < defragged.Count - 1 ? defragged[index + 1] : null))
            {
                phantoms++;
                continue;
            }
            alive.Add(defragged[index]);
        }
        // Pass 2: gộp kề trùng lặp (giữ chữ dài hơn, cộng support, OR NeedsReview).
        // NGOẠI TRỪ chữ Y HỆT nhau (sim == 1.0): pixel verdict đã xử (identical
        // + pixel Different = 2 instance thật giữ tách; identical + Same thì
        // MergeTouchingIdentical đã gộp). Chỉ gộp flicker đọc KHÁC nhau.
        var merged = new List<OcrCue>();
        var merges = 0;
        foreach (var cue in alive)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                var gap = cue.Start - prev.End;
                var similarity = TextSimilarity(prev.Text, cue.Text);
                var prefixGrowth = gap >= 0 && gap <= PrefixGrowthGapSeconds
                    && IsPrefixGrowth(prev.Text, cue.Text);
                if (gap >= 0 && ((gap <= MergeGapSeconds
                    && similarity >= MergeSimilarity && similarity < 1.0) || prefixGrowth))
                {
                    var text = NormalizedLength(cue.Text) >= NormalizedLength(prev.Text)
                        ? cue.Text : prev.Text;
                    merged[^1] = prev with
                    {
                        End = Math.Max(prev.End, cue.End),
                        Text = text,
                        Confidence = Math.Max(prev.Confidence, cue.Confidence),
                        RawSupportCount = prev.RawSupportCount + cue.RawSupportCount,
                        ConfidentSupportCount = prev.ConfidentSupportCount + cue.ConfidentSupportCount,
                        LastObservation = cue.LastObservation ?? prev.LastObservation,
                        NeedsReview = prev.NeedsReview || cue.NeedsReview,
                    };
                    merges++;
                    continue;
                }
            }
            merged.Add(cue);
        }
        if (merges == 0 && fragments == 0 && phantoms == 0)
            return new CleanupResult(merged, 0, 0, 0);
        return new CleanupResult(merged, merges, fragments, phantoms);
    }

    private static int NormalizedLength(string text) =>
        OcrTemporalFilter.Normalize(text).EnumerateRunes().Count();

    // Cue sau là bản LỚN DẦN của cue trước (hiệu ứng gõ chữ): normalize ngắn
    // là prefix nghiêm ngặt của dài và dài hơn hẳn. Không kén độ tương đồng —
    // bản tăng trưởng nhanh có thể khác xa (vd "…50%" → "…100% xong").
    private static bool IsPrefixGrowth(string previousText, string nextText)
    {
        var a = OcrTemporalFilter.Normalize(previousText);
        var b = OcrTemporalFilter.Normalize(nextText);
        if (a.Length == 0 || b.Length == 0 || a.Length >= b.Length) return false;
        var aRunes = a.EnumerateRunes().Count();
        if (aRunes < PrefixGrowthMinRunes) return false;
        return b.StartsWith(a, StringComparison.Ordinal);
    }

    // Internal để bộ lọc thời gian tái dùng khi tìm bản đọc tốt hơn (fallback).
    internal static double TextSimilarity(string left, string right)
    {
        var a = OcrTemporalFilter.Normalize(left);
        var b = OcrTemporalFilter.Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var leftRunes = a.EnumerateRunes().Select(x => x.Value).ToArray();
        var rightRunes = b.EnumerateRunes().Select(x => x.Value).ToArray();
        var previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        for (var i = 1; i <= leftRunes.Length; i++)
        {
            var current = new int[rightRunes.Length + 1];
            current[0] = i;
            for (var j = 1; j <= rightRunes.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (leftRunes[i - 1] == rightRunes[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(leftRunes.Length, rightRunes.Length);
    }
}

namespace BiliSubStudio.Core.Ocr;

// Bộ lọc thời gian (user chốt 17/09): sub cứng thật ĐỨNG YÊN qua nhiều frame
// (vị trí cố định giữa các khung), còn chữ trong video (trang sách/bối cảnh
// bị camera lia) thì DI CHUYỂN liên tục, không đứng lâu.
// Chấm từng ca nghi: chụp 3 khung trong ca (đầu/giữa/cuối + rìa ±0.3s),
// OCR lại từng khung, so khớp chữ + vị trí:
// - ≥2 khung tìm thấy nhưng vị trí trôi > 5% khung -> DI CHUYỂN -> DROP
//   (chữ bối cảnh chạy theo camera, không phải sub).
// - 3/3 khung cùng chữ + cùng vị trí -> ĐỨNG YÊN -> KEEP (sub thật).
// - 2/3 khung cùng vị trí (1 khung OCR s Méng) -> KEEP (tha toler flake).
// - <2 khung tìm thấy -> không đủ bằng chứng -> KEEP (không drop oan).
// Fail-open tuyệt đối: mọi lỗi grab/OCR -> KEEP, không bao giờ mất chữ oan.
public static class OcrTemporalFilter
{
    // Trôi vị trí tối đa để coi là "đứng yên": 4% khung. Jitter box OCR trên
    // chữ tĩnh chỉ ~0.1-0.5% khung nên 4% an toàn; camera lia qua window
    // ~2.3s (kể cả chữ lia chậm 2%/s) trôi > 4%.
    public const double MoveDriftThreshold = 0.04;
    public const int ProbeFrames = 3;
    // Rìa phủ ra NGOÀI ca (±1s) để khuếch đại độ trôi: ca ngắn 0.27s mà chỉ
    // quét trong ca thì window 0.16s — drift không bao giờ vượt ngưỡng (bug
    // đã sửa 17/09). Phủ ngoài + khớp chữ theo đúng CHỮ CA nên không dính
    // sub kế (chữ khác không match).
    public const double EdgePadSeconds = 1.0;

    public sealed record TemporalVerdict(string Decision, string Reason);

    // Chức danh credit/tít (KHÔNG phải thoại). Thoại thật có thể nhắc tới
    // ("导演说明天开拍") nhưng KHÔNG mở đầu bằng chức danh + dấu ngăn cách.
    private static readonly string[] CreditRoles =
    [
        "立书人", "出品人", "联合出品", "出品", "制片人", "总制片", "监制", "总监制",
        "导演", "总导演", "副导演", "编剧", "总编剧", "原著", "原作",
        "领衔主演", "主演", "主持人", "主持", "嘉宾",
        "发行", "摄制", "制作", "承制", "鸣谢", "赞助", "特别感谢",
        "字幕", "翻译", "校对", "压制", "片源", "修复",
    ];

    // Tít/credit (KHÔNG phải thoại) — drop ngay không cần chụp khung:
    // - dòng BẮT ĐẦU bằng chức danh credit + dấu ngăn cách ("立书人：冷暖暖")
    //   hoặc chức danh đứng một mình ("出品人");
    // - dòng chỉ là ngày tháng ("2026年6月3日");
    // - dòng bản quyền (©/copyright/版权/著作权).
    // Thoại thật ("导演说明天开拍") không có dấu ngăn cách sau chức danh nên thoát.
    public static bool IsCreditTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (IsDateOnly(trimmed)) return true;
        if (trimmed.Contains('©', StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("copyright", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("版权", StringComparison.Ordinal)
            || trimmed.Contains("著作权", StringComparison.Ordinal)) return true;
        foreach (var role in CreditRoles)
        {
            if (!trimmed.StartsWith(role, StringComparison.Ordinal)) continue;
            var rest = trimmed[role.Length..].TrimStart();
            if (rest.Length == 0) return true;
            if (rest[0] is ':' or '：' or '，' or ',' or '、' or '·' or '—' or '-' or '・')
                return true;
        }
        return false;
    }

    private static bool IsDateOnly(string text)
    {
        var digits = text.Count(char.IsDigit);
        if (digits < 4 || digits > 8) return false;
        var rest = new string(text.Where(c => !char.IsDigit(c) && !char.IsWhiteSpace(c)).ToArray());
        return rest.Length switch
        {
            0 => true,
            <= 3 => rest.All(c => c is '年' or '月' or '日' or '-' or '/' or '.' or ':' or '：'),
            _ => false,
        };
    }

    // Ngưỡng fallback đọc lại (17/09): chỉ cue đáng ngờ (⚠️/tin thấp) mới
    // đọc lại khung lân cận. Fix CHỈ khi bản đọc khác CHẮC CHẮN hơn hẳn —
    // không bao giờ "sửa" chữ đúng thành chữ sai.
    public const double FallbackTriggerConfidence = 0.85;
    public const double FixMinConfidence = 0.88;
    public const double FixMargin = 0.08;
    public const double FixMinSimilarity = 0.55;

    // Tìm bản đọc tốt nhất cho cue trong 1 frame: dòng tương đồng (cùng câu
    // đọc khác) có tin cao nhất. Trả null khi không có dòng nào plausibly cùng câu.
    public static (string Text, double Confidence)? FindBestFixMatch(
        IReadOnlyList<OcrLine> lines, string cueText)
    {
        var needle = Normalize(cueText);
        if (needle.Length == 0) return null;
        (string Text, double Confidence)? best = null;
        foreach (var line in lines)
        {
            var hay = Normalize(line.Text);
            if (hay.Length == 0) continue;
            // Cùng chữ y hệt thì không phải "bản đọc khác" — bỏ qua.
            if (string.Equals(hay, needle, StringComparison.Ordinal)) continue;
            if (OcrTextCleanup.TextSimilarity(line.Text, cueText) < FixMinSimilarity) continue;
            if (best is null || line.Confidence > best.Value.Confidence)
                best = (line.Text, line.Confidence);
        }
        return best;
    }

    // Chuẩn hóa chữ: chỉ giữ chữ/số (CJK là letter), bỏ space+punct
    // ("立书人，冷暖暖" == "立书人：冷暖暖"). Rỗng sau chuẩn hóa là không khớp.
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    // Tâm box (tọa độ pixel) chuẩn hóa theo kích thước khung.
    public static (double X, double Y)? LineCenter(OcrLine line, int frameWidth, int frameHeight)
    {
        if (line.Box is not { Length: 8 } box || frameWidth <= 0 || frameHeight <= 0) return null;
        double sumX = 0, sumY = 0;
        for (var index = 0; index < 8; index += 2)
        {
            sumX += box[index];
            sumY += box[index + 1];
        }
        return (sumX / 4 / frameWidth, sumY / 4 / frameHeight);
    }

    // Tìm dòng khớp chữ ca (chứa 1 trong 2 chiều sau chuẩn hóa). Không khớp -> null.
    // Cue NGẮN (≤2 chữ sau chuẩn hóa, vd "哼"/"滚"/"武夫"): CẤM khớp "chứa nhau" —
    // chữ nền dài chứa đúng chữ đó luôn thắng longest-match rồi đo nhầm vị trí
    // dòng nền (chạy theo camera) -> drop oan sub đứng yên (cue "哼"/"滚" thật
    // trong .exclude.json). Nhánh này chỉ chấp nhận KHỚP TUYỆT ĐỐI, hòa thì lấy
    // tin cao nhất. Đường cue dài (>2 chữ) giữ nguyên từng dòng bên dưới.
    public static OcrLine? FindMatch(IReadOnlyList<OcrLine> lines, string cueText)
    {
        var needle = Normalize(cueText);
        if (needle.Length == 0) return null;
        if (needle.EnumerateRunes().Count() <= 2)
        {
            OcrLine? exact = null;
            var exactConfidence = double.NegativeInfinity;
            foreach (var line in lines)
            {
                var hay = Normalize(line.Text);
                if (hay.Length == 0) continue;
                if (!string.Equals(hay, needle, StringComparison.Ordinal)) continue;
                if (exact is null || line.Confidence > exactConfidence)
                {
                    exact = line;
                    exactConfidence = line.Confidence;
                }
            }
            return exact;
        }
        OcrLine? best = null;
        var bestLength = 0;
        foreach (var line in lines)
        {
            var hay = Normalize(line.Text);
            if (hay.Length == 0) continue;
            if (!hay.Contains(needle, StringComparison.Ordinal)
                && !needle.Contains(hay, StringComparison.Ordinal)) continue;
            // Ưu tiên dòng khớp dài nhất (đủ câu, không phải dòng cắt ngắn).
            if (hay.Length > bestLength)
            {
                best = line;
                bestLength = hay.Length;
            }
        }
        return best;
    }

    // Quyết từ kết quả OCR của các khung probe (theo thứ tự probe).
    // frames[i] = kết quả OCR khung i; thiếu khung -> null.
    public static TemporalVerdict Decide(
        IReadOnlyList<OcrResult?> frames,
        IReadOnlyList<double> frameTimes,
        string cueText,
        int frameWidth,
        int frameHeight)
    {
        var found = new List<(double Time, double X, double Y)>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame is null || !frame.Ok || frame.Lines.Count == 0) continue;
            if (FindMatch(frame.Lines, cueText) is not { } match) continue;
            if (LineCenter(match, frameWidth, frameHeight) is not { } center) continue;
            found.Add((frameTimes[index], center.X, center.Y));
        }
        if (found.Count < 2)
            return new TemporalVerdict("keep",
                "không đủ bằng chứng di chuyển (đứng yên hoặc OCR s Méng) -> giữ");
        // Trôi tối đa giữa các cặp khung tìm thấy.
        var maxDriftX = 0.0;
        var maxDriftY = 0.0;
        for (var a = 0; a < found.Count; a++)
        {
            for (var b = a + 1; b < found.Count; b++)
            {
                maxDriftX = Math.Max(maxDriftX, Math.Abs(found[a].X - found[b].X));
                maxDriftY = Math.Max(maxDriftY, Math.Abs(found[a].Y - found[b].Y));
            }
        }
        var drift = Math.Max(maxDriftX, maxDriftY);
        if (drift > MoveDriftThreshold)
            return new TemporalVerdict("drop",
                $"chữ DI CHUYỂN giữa {found.Count} khung (trôi {drift * 100:0.0}% khung) -> chữ bối cảnh chạy theo camera, không phải sub");
        return new TemporalVerdict("keep",
            $"chữ ĐỨNG YÊN giữa {found.Count} khung (trôi {drift * 100:0.0}% khung) -> sub thật");
    }
}

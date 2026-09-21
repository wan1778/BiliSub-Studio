using System.Text;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Ocr;

// Đối chiếu tai-mắt (17/09): Whisper nghe được gì vs OCR thấy gì.
// - Timeline im-lặng từ segment thoại: lane bỏ qua OCR chỗ im (tốc độ).
// - Judge: cue không khớp đoạn thoại nào chồng lấn -> không phải thoại -> drop;
//   khớp mạnh -> keep sớm (khỏi chụp khung); lưng chừng -> để lọc vị trí quyết.
// Thuần logic, KHÔNG AI. Fail-open: thiếu dữ liệu tai là bỏ qua hết.
public static class OcrSpeechMatch
{
    // Pad speech khi dựng timeline (±0.5s: sub thường sớm/muộn hơn tiếng một chút).
    public const double TimelinePadSeconds = 0.5;
    // Pad overlap khi tìm segment chồng cue (±0.3s: timing 2 bên đều lệch ít).
    public const double OverlapPadSeconds = 0.3;
    // Ngưỡng im-lặng sâu (giữ làm utility cho test; judge KHÔNG còn drop theo
    // đường này từ 18/09 — sub-không-tiếng là thật, chữ nền tĩnh do guard
    // banner ở finish bắt).
    public const double SilenceDropGapSeconds = 1.5;
    // Ngưỡng tương đồng chữ tai-mắt (đo thật video 0916: thoại thật 0.38-0.9,
    // chữ cảnh ~0.0-0.2).
    public const double MinSimilarity = 0.3;
    public const double StrongSimilarity = 0.6;
    // Cổng warmup: 10 cue đầu chỉ thống kê (không drop theo chữ) để phát hiện
    // video lệch ngôn ngữ (tai-mắt khác nhau toàn bộ -> tắt drop-chữ, tránh
    // xóa oan cả video). Im-lặng-drop cũng chỉ bật sau warmup (Whisper điếc
    // thì im-lặng giả khắp nơi).
    public const int WarmupSamples = 10;
    public const double WarmupAgreement = 0.4;
    // Vùng quét kỹ, phương án A (18/09, user chốt an toàn nhất): khe im (không
    // thoại Whisper) dài hơn ngưỡng này thì lane OCR MỌI frame trong đó (đánh
    // Suspicious thay vì NoSub) — sub-không-tiếng/fade-in trong đoạn im dài
    // không thoát qua khe skip. Judge/guard đọc segment gốc nên không bị ảnh
    // hưởng. Khe ngắn hơn giữ nguyên (skip như cũ, nhanh).
    public const double LongSilenceFullScanSeconds = 60;

    // Tìm các khoảng im dài hơn ngưỡng (đã sort theo Start giùm gọi cũng
    // được — hàm tự sort). Kẹp [0, duration]. Rỗng/vô nghĩa -> rỗng.
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

    // Đánh vùng quét kỹ lên timeline: cờ NoSub trong vùng -> Suspicious (đọc
    // đầy đủ). Giữ nguyên HasSub (lõi thoại + pad) để công tắc council toàn
    // cục còn dấu hiệu tai-nghe.
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

    // Dựng timeline 0.2s từ segment thoại: có tiếng -> HasSub (quét như cũ;
    // HasSub sinh ra đúng cho Whisper (E.5b) — mọi chỗ đọc vote chỉ tách NoSub
    // nên HasSub hành xử y hệt Suspicious, cộng thêm làm dấu hiệu "timeline từ
    // tai" để mở council skip mà không sửa signature), im lặng -> NoSub
    // (council được skip khi các cổng khác đồng ý + canary soi).
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

    // Pad cứu tai-trễ cho đường drop-mismatch (±1.5s = chân trời veto): sub có
    // thể hiện TRỄ sau tiếng (tracker mở cue muộn) hoặc tracker tách cue khiến
    // tai nói cùng câu nằm ngoài ±0.3s (field 2408.1: tai nói xong trước 0.75s!).
    // Chỉ dùng để CỨU (miễn drop), không dùng để buộc tội/giữ sớm.
    public const double MismatchOverlapPadSeconds = 1.5;

    // Segment chồng lấn [start-pad, end+pad] (đã sort theo Start giùm gọi).
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

    // Tai phủ kín span (18/09, luật chung user chốt): chữ TỰ TIN (prob ≥ 0.8)
    // phủ ≥ 75% thời lượng cue mới là bằng chứng dương tính để buộc tội (kết
    // hợp với sim thấp + không chứa ở ngoài mới thành mâu thuẫn). Tai thưa,
    // tin thấp, rỗng -> false (không đủ bằng chứng -> giữ, fail-closed).
    // Hàm này KHÔNG xem nội dung chữ (giống/khác do sim/containment quyết).
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

    // Miễn containment (18/09): cue ngắn nằm NGUYÊN VĂN trong câu tai nghe
    // dài thì tai CÓ nói — sim Levenshtein thấp chỉ vì chênh độ dài
    // (VD cue 好的 trong segment 好的我们开始吧 chỉ được 0.25 < ngưỡng drop
    // 0.3 dù tai nghe đúng chữ đó). Gộp số-chữ 2 vế (2 vs 两) để containment
    // không hụt vì khác dạng số. Dùng chặn drop oan, KHÔNG dùng keep sớm.
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

    // Tương đồng cao nhất giữa chữ cue và các segment chồng lấn.
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

    // Khoảng cách từ cue tới đoạn thoại gần nhất (0 nếu đang chồng lấn).
    // Dùng cho drop-im-lặng: cue nằm sâu trong im lặng mới drop.
    // Ngưỡng gắn cờ chữ-cảnh-nghi-vấn (video Cả Lớp 19/09: sổ/biển/tường tai
    // không hề nói): cue cách mọi đoạn thoại > 1.5s thì gắn review (giữ cue,
    // không xóa — quyết xóa thuộc về banner/user).
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

    // Cắt đầu/đuôi thừa theo tai (17/09, siết 18/09): chữ OCR dính thêm chữ cảnh
    // ở rìa. Thử mọi cách cắt rìa (tổng chữ-cái/số cắt ≤ 4, ưu tiên cắt ít nhất),
    // chọn cách đầu tiên mà phần còn lại là SUBSTRING của tai nghe.
    // LUẬT THÉP 18/09 (harness kiểm 8778 cue thật chứng minh guards cũ không
    // cứu được: 后世之人→世之, 龙王逃课→王逃, 没有之一→之一...): CẤM CẮT CHỮ HÁN.
    // Chữ Hán chỉ được giữ nguyên hoặc xóa cả cue (judge drop có lý do ghi sổ),
    // không bao giờ xén lén bên trong (đó là mất mát dữ liệu câm). Nhát cắt chỉ
    // được chứa số/dấu/latin — đúng lớp rác user báo (số đuôi, (), ♡, E, boss).
    // Rác Hán dính rìa (hiếm — 人第一) thì sửa tay, không đoán.
    // Guards còn lại (an toàn là trên hết):
    // - mọi word tai tin ≥ 0.75 (tai đang nghe rõ; vụ 那 2 tai 76% vẫn cho cắt
    //   nhát không-Hán);
    // - tai phủ ≥ 75% thời lượng cue (tai nghe thưa = tai sót chữ);
    // - NEO RÌA (18/09): chỉ cắt đầu khi word tai ĐẦU bắt đầu sát đầu cue,
    //   chỉ cắt đuôi khi word CUỐI kết thúc sát cuối cue;
    // - VETO LÂN CẬN (18/09): chữ định cắt mà tai CÓ nói ở lân cận rộng thì cấm;
    // - GỘP SỐ-CHỮ (18/09): fold 2 vế về số khi so khớp tuyệt đối;
    // - cue đã khớp hết tai nghe thì không cắt; phần giữ ≥ 1 chữ (nhát cắt
    //   không-Hán, cue ≤5 rune do cap tổng cắt).
    // KHÔNG đụng sai chữ GIỮA câu — giữ nguyên. Trả null khi không cắt được.
    public const int TrimMaxRun = 4;
    public const int TrimMinKept = 2;
    public const double TrimMinWordProb = 0.8;
    public const double TrimWordProbFloor = 0.75;
    public const double TrimMinCoverage = 0.75;
    public const double TrimEdgeAnchorSeconds = 0.35;
    public const double TrimNearbyVetoSeconds = 1.5;

    // Gộp chữ-số Hán về số Ả Rập cho so khớp tuyệt đối 2 vế (tai 九 vs mắt 9,
    // 两 vs 2). Chỉ số đếm đơn (十百千万亿 là toán số học — không đụng tới,
    // thiếu thì veto/containment hụt về hướng an toàn: giữ, không cắt).
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

    // Dup 1:1 từ ChineseSubtitleNormalizer.IsHan (giữ private để khỏi mở API):
    // chữ Hán khối chính + mở rộng. Kana/hangul/chữ khác KHÔNG tính là Hán ở
    // đây — nhát cắt chỉ dính nhóm đó thì đối xử như cắt số/dấu (ít nguy hiểm
    // hơn cắt chữ Hán).
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
        // Cue đã khớp hết tai nghe thì KHÔNG có gì để cắt (không được xén bớt
        // chữ đúng — bug đã bắt 17/09: "冷氏集团" bị cắt thành "冷氏集").
        var cueNorm = FoldDigits(new string(cueText.Where(char.IsLetterOrDigit).ToArray()));
        if (spoken.Contains(cueNorm, StringComparison.Ordinal)) return null;
        return FindKeptByEdgeTrim(seen, seenLetters, spoken,
            cueStart, cueEnd, firstStart, lastEnd, nearbyNorm);
    }

    // Tìm cách cắt rìa (tổng chữ cắt ≤ TrimMaxRun, ưu tiên cắt ít nhất) mà phần
    // còn lại là SUBSTRING của tai nghe. Dùng chung cho TrimEdges (cắt rác rìa)
    // và TrimRepeatTiles (cắt mảnh sót sau gỡ tường lặp). floorLetters > 0 thì
    // giữ luật "phần giữ ≥ nửa cue" (TrimEdges); = 0 thì lõi ngắn vẫn đúng nếu
    // tai xác nhận nguyên văn. Phần giữ 1 chữ được chấp nhận (nhát cắt luôn
    // không-Hán nên cap tổng cắt giới hạn cue ≤5 rune; lõi tai-xác-nhận).
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
                // Neo rìa: không bằng chứng ở rìa thì không cắt rìa đó.
                if (leading > 0 && !(firstStart <= cueStart + TrimEdgeAnchorSeconds)) continue;
                if (trailing > 0 && !(lastEnd >= cueEnd - TrimEdgeAnchorSeconds)) continue;
                var cutRunes = seen.Take(leading)
                    .Concat(seen.Skip(seen.Length - trailing).Take(trailing))
                    .ToArray();
                // LUẬT THÉP: cấm cắt chữ Hán (mọi ca xén oan quan sát được đều
                // cắt chữ Hán; rác cần cắt của user toàn số/dấu/latin).
                if (cutRunes.Any(IsHanLetter)) continue;
                // BIÊN SỐ (18/09): cấm tách/nuốt cụm số đã tai-xác-nhận.
                // R1: biên cắt-số kề biên giữ-số (18岁->8岁 đổi giá trị!) -> từ
                // chối. R2: chữ số trong phần cắt mà tai có nói (folded) ->
                // từ chối (số đó có thể thật; giữ nguyên an toàn hơn, dù veto
                // cả cụm có thể lọt). Chữ số gồm ASCII + fullwidth.
                if (SplitsDigitRun(seen, leading, trailing)) continue;
                if (cutRunes.Any(rune => IsDigitChar(rune.Value)
                    && spoken.Contains(rune.ToString(), StringComparison.Ordinal))) continue;
                // R3 (video Cả Lớp 19/09: cue "A…2…35?" bị cắt đầu "A…2"): cấm
                // cắt khi chữ định cắt thuộc token latin+số (A2, p01, 1080P):
                // token lai thường là tên/hạng/mã — cắt là đổi nghĩa, giữ.
                if (CutTouchesDigitToken(seen, leading, trailing)) continue;
                // Veto lân cận: chữ định cắt mà tai có nói gần đó thì giữ.
                if (nearbyNorm.Length > 0)
                {
                    var cutNorm = FoldDigits(string.Concat(cutRunes.Where(Rune.IsLetterOrDigit)));
                    if (cutNorm.Length > 0
                        && nearbyNorm.Contains(cutNorm, StringComparison.Ordinal)) continue;
                }
                var keptRunes = seen.Skip(leading).Take(seen.Length - leading - trailing).ToArray();
                var keptLetters = keptRunes.Count(Rune.IsLetterOrDigit);
                // Nhát cắt đã cấm chữ Hán ở trên nên giữ 1 chữ tai-xác-nhận
                // cũng được (那 2 -> 那); cap tổng cắt giới hạn cue ≤5 rune.
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

    // Cổng áp dụng fix-cắt (18/09): strong/review/tin-thấp như cũ, HOẶC nhát
    // cắt chỉ gỡ chữ không-Hán CÓ số/latin (rác danmaku/counter: 482, 0101,
    // 那 2, boss, DEC — không thể xén nghĩa Hán). Dấu câu thuần (。、？) vẫn
    // cần cổng cũ (giữ giọng câu). Dùng chung runtime + offline-harness
    // (1 nguồn duy nhất, không drift).
    public static bool ShouldApplyTrimFix(
        bool strongMatch, bool needsReview, double confidence,
        string oldText, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText)
            || string.Equals(newText, oldText, StringComparison.Ordinal)) return false;
        if (strongMatch || needsReview || confidence < OcrTemporalFilter.FallbackTriggerConfidence)
            return true;
        // Cue tin cao tai-vừa: chỉ cho qua nhát cắt số/dấu/latin có số/latin.
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

    // Gỡ tường chữ lặp nền (18/09, siết: CHỈ gỡ tile KHÔNG-HÁN — số/dấu/latin
    // lặp như 482/0101/()/♡; tile chữ Hán KHÔNG gỡ (luật thép chống xén oan:
    // tai sót tên riêng là chuyện thường, gỡ chữ Hán là mất mát câm)):
    // tile 2-6 chữ lặp (gộp họ BIẾN THỂ CÙNG ĐỘ DÀI không-Hán, tương đồng
    // ≥0.66) mà tai KHÔNG hề nói (cả cửa sổ hẹp lẫn lân cận) thì gỡ, giữ lõi
    // tai có nói. 3 lớp bảo vệ (an toàn là trên hết):
    // (1) chỉ gỡ họ tile tai vắng mặt — số tai có nói (2023年) thì giữ nguyên;
    //     ngưỡng chỉ cần ≥2 vì tai-vắng-mặt đã là bằng chứng chính;
    // (2) lõi còn lại phải nằm NGUYÊN VĂN trong tai + ≥1 chữ — tuyệt đối không
    //     xén chữ tai có nói (mảnh rìa sót sau gỡ thì cắt tiếp bằng
    //     FindKeptByEdgeTrim, cũng cấm chữ Hán);
    // (3) tai phủ kín (tin ≥ floor) + neo 2 đầu như TrimEdges.
    // KHÔNG áp luật "giữ ≥ nửa cue": tường lặp có thể chiếm đa số cue (bằng
    // chứng gỡ là bản thân sự lặp + tai vắng mặt, không phải tỉ lệ).
    // Trả null khi không gỡ được gì (kể cả norm đã khớp hết tai).
    public const int PeelTileMinLength = 2;
    public const int PeelTileMaxLength = 6;
    public const int PeelMinFamilyCount = 2;
    // Ngưỡng riêng cho họ tile CHỮ HÁN (video Cả Lớp 19/09: tường danmaku
    // 陪一支, UI, bình luận — toàn chữ Hán, luật cũ không gỡ nên sống hết):
    // tile Hán chỉ gỡ khi lặp ≥3 (thoại lặp thật như rap/điệp khúc tai CÓ nghe
    // nên spokenCount>0 đã giữ; tai sót + lặp y hệt ≥3 trong 1 cue cực hiếm).
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
        // Floor tin chung 0.75 như TrimEdges (gỡ toàn phần không-Hán, lõi vẫn
        // phải tai-xác-nhận nguyên văn + coverage/anchor/veto đầy đủ).
        var nearbyNorm = string.IsNullOrWhiteSpace(nearbySpoken)
            ? ""
            : FoldDigits(new string(nearbySpoken.Where(char.IsLetterOrDigit).ToArray()));
        // Bản đồ rune (giữ dấu câu trong lõi): cue sub BMP-only thực tế; gặp
        // non-BMP thì fail-closed (bản đồ UTF-16 lệch) — thà không gỡ.
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
        // Vòng gỡ: mỗi vòng gỡ 1 họ tile; dừng khi lõi khớp tai hoặc hết họ.
        // alive = vị trí chuỗi-norm còn sống (ánh xạ về normRunes -> runes).
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
        // Dựng runes đã gỡ: giữ rune chữ sống + dấu câu/space mà ký tự KỀ TRÁI
        // còn sống (dấu bám trái như ，。/space sau chữ lõi thì giữ; dấu trong
        // vùng tile gỡ hoặc đầu câu thì bỏ — space giữa 2 lõi ("hi there" khi
        // tile ở giữa) được giữ nhờ kề-trái, space thừa đầu/cuối Trim() dọn).
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
        // Lõi sạch hẳn -> trả; còn mảnh rìa -> cắt rìa tiếp (không luật nửa).
        if (spoken.Contains(finalNorm, StringComparison.Ordinal))
        {
            var direct = string.Concat(peeledArr).Trim();
            if (direct.Length == 0) return null;
            var directNorm = new string(direct.Where(char.IsLetterOrDigit).ToArray());
            if (directNorm.Length == 0) return null;
            return direct;
        }
        // Đã gỡ tile Hán mà lõi còn lại tai không xác nhận nguyên văn thì
        // DỪNG (fail-closed): cấm rớt xuống cắt rìa — nhặt 1 chữ Hán tường
        // trùng chữ tai nói là mất mát câm. Giữ cue gốc cho judge/F2 xử.
        if (hanPeeled) return null;
        return FindKeptByEdgeTrim(peeledArr, 0, spoken,
            cueStart, cueEnd, firstStart, lastEnd, nearbyNorm);
    }

    // Tìm họ tile để gỡ: SEED (tile 2-6 chữ) phải TỰ lặp ≥ ngưỡng (bằng chứng lặp độc lập — tile chỉ xuất hiện trong
    // span tile khác thì không đủ tuổi seed); họ = các tile CÙNG ĐỘ DÀI tương
    // đồng ≥0.66 (sai khác ≤1 chữ: gộp biến thể đọc sai; tile dài hơn chứa thêm
    // chữ kề KHÔNG được gộp — gỡ span của nó sẽ ăn chữ thật, test bắt được 2
    // lần). Tile Hán chỉ gộp vào họ Hán (ngưỡng lặp ≥3). Tai hẹp vắng mặt toàn họ + veto nếu BẤT
    // KỲ member nào xuất hiện ở lân cận rộng. Chọn họ đông nhất (đồng hạng:
    // tile dài trước). Trả false khi không có họ đạt.
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
                // Seed chữ Hán: cho qua nhưng ngưỡng lặp cao hơn (thoại lặp
                // thật tai có nghe nên đã bị spokenCount giữ; xem const).
                var hanSeed = ContainsHan(tile);
                // Seed không tự lặp thì loại ngay (kể cả khi gộp mảnh/variant
                // đủ số — số đó là trùng span, không phải bằng chứng lặp).
                if (CountOccurrences(current, tile) < (hanSeed ? PeelHanMinFamilyCount : PeelMinFamilyCount)) continue;
                var family = new List<string> { tile };
                for (var len2 = PeelTileMinLength; len2 <= PeelTileMaxLength; len2++)
                {
                    for (var j = 0; j + len2 <= current.Length; j++)
                    {
                        var other = current.Substring(j, len2);
                        if (string.Equals(other, tile, StringComparison.Ordinal)) continue;
                        // Cùng độ dài mới gộp.
                        if (other.Length != tile.Length) continue;
                        // Tile Hán chỉ gộp vào họ Hán (không lôi chữ Hán vào
                        // họ non-Hán và ngược lại).
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
                // Đồng hạng thì ưu tiên tile DÀI (span đầy đủ hơn mảnh).
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

    // true nếu nhát cắt chạm token lẫn chữ+số (R3): chữ latin/số định cắt
    // thuộc cụm ASCII-alnum liên tục mà cụm đó chứa CẢ chữ CÁI lẫn số (A2,
    // p01, 1080P) — cắt là đổi nghĩa/mất mã, giữ nguyên. Cụm thuần số (482,
    // 0101: R1/R2 quyết) và thuần chữ (boss, DEC: cắt như cũ) không dính.
    // Cụm chỉ chạy trên latin/số (không lan qua chữ Hán: số kề Hán như
    // "482出" vẫn thuộc địa hạt R1/R2 cũ).
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

    // true nếu nhát cắt tách cụm số (biên cắt-số kề biên giữ-số ở đầu/đuôi).
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

    // Tai sửa 1 chữ mắt đọc sai (video Cả Lớp 19/09: 19 cue sai lẻ chữ, tai
    // nghe đúng — VD 5137.7s Ăn Mày): căn cue với câu tai bằng cửa sổ trượt;
    // đúng 1 rune khác (cả 2 đều chữ Hán — hoán chữ, không phải rác chèn),
    // giản/phồn tương đương không tính là khác, mọi word tai phủ cue tin
    // ≥ CorrectionMinWordProb, cue dài ≥3, và CHỮ THAY PHẢI DO TAI NÓI SÁT GIỜ
    // CUE (nằm trong word phủ cue ±0.3s — chống khớp ngẫu nhiên với câu tai xa).
    // Thêm rào tương đồng toàn câu (19/09: replay Ăn Mày 21/23 fix khớp đảo
    // ngẫu nhiên — cue ngắn 4 chữ rớt vào cửa sổ 1-diff của câu tai xa lạ):
    // cue phải giống câu tai ≥0.5 (cùng câu sai 1 chữ), không phải đảo lạ.
    // Mơ hồ (0 hoặc ≥2 ứng viên) thì null — giữ nguyên, fail-closed.
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
        // Rào tương đồng toàn câu: cue phải giống ÍT NHẤT 1 câu tai ≥0.5.
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
                    // Giản/phồn là 1 chữ (bản đồ tracking chung) — không lỗi.
                    if (SubtitleTracker.CanonicalTrackingRune(a) == SubtitleTracker.CanonicalTrackingRune(b)) continue;
                    diffs++;
                    diffAt = k;
                    if (diffs > 1) break;
                }
                if (diffs != 1) continue;
                // Hoán chữ Hán↔Hán (không phải rác/dấu chèn).
                if (!IsHanLetter(cueNorm[diffAt]) || !IsHanLetter(segNorm[w + diffAt])) continue;
                // Chữ thay phải do tai NÓI SÁT GIỜ cue (trong word phủ cue):
                // khớp với câu tai xa giờ là ngẫu nhiên — bỏ cửa sổ này.
                if (!narrowText.Contains(segNorm[w + diffAt].ToString(), StringComparison.Ordinal)) continue;
                if (foundCount == 0 || (foundIndex == cueMap[diffAt] && foundRune.Value == segNorm[w + diffAt].Value))
                {
                    foundIndex = cueMap[diffAt];
                    foundRune = segNorm[w + diffAt];
                    foundCount++;
                }
                else
                {
                    // Hai ứng viên khác nhau — mơ hồ, giữ nguyên.
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

using System.Text.Json;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Đối chiếu tai-mắt (17/09): Whisper nghe gì vs OCR thấy gì. Test bằng số liệu
// thật đo từ video 0916 (faster-whisper-small, CPU int8).
internal static class OcrSpeechMatchContract
{
    public static Task RunAsync()
    {
        TimelineMarksSpeechAndSilence();
        CorroboratedDialogueScoresHigh();
        SceneTextScoresLow();
        SilenceGapMeasuresDistance();
        TrimRefusesHanTrailing();
        TrimRefusesHanLeading();
        TrimKeepsCleanCue();
        TrimRefusesUnsafe();
        LongSilence90sMarkedFullScan();
        ShortSilence30sStaysSkippable();
        FullyMuteVideoMarkedFullScan();
        NoSkipInsideFullScanZone();
        TrimRefusesWhenCutHeardNearby();
        TrimRefusesWhenEarStartsLate();
        TrimRefusesSparseCoverage();
        VerbatimContainedExemptsShortCue();
        VerbatimRejectsUnheard();
        EarCoversDenseSpan();
        EarSparseIsNotEvidence();
        EarLowConfIsNotEvidence();
        EarEmptyIsNotEvidence();
        PeelRefusesHanFlood();
        PeelRefusesGenuineTriple();
        PeelRefusesFullEcho();
        PeelRefusesHanVariant();
        PeelStripsHanWallMixedCore();
        PeelPureHanWallStaysForJudge();
        TrimProtectsAlnumToken();
        TrimStillCutsPureDigitAffix();
        CorrectSingleCharPositive();
        CorrectSingleCharRefusals();
        PeelRefusesDeafEar();
        PeelKeepsInternalPunct();
        PeelLongTileKeepsSpacedCore();
        DigitFoldVetoProtectsRealNumber();
        WeakEarTrimsNonHanCut();
        WeakEarRefusesHanCut();
        DigitBoundaryRefusesValueChange();
        DigitAffixWithoutDigitKeptTrims();
        ApplyGateLocksHanCuts();
        ApplyGateOpensDigitCuts();
        return Task.CompletedTask;
    }

    private static EditorSpeechSegment Seg(double start, double end, string text) =>
        new(start, end, text, -0.2, 0.0, [], "uncertain", 0, 0);

    private static EditorWordTiming W(string text, double start, double end, double prob = 0.99) =>
        new(text, start, end, prob);

    private static void TimelineMarksSpeechAndSilence()
    {
        // 2 đoạn thoại 56.5-58.6 và 59.0-61.5, pad ±0.5s.
        var timeline = OcrSpeechMatch.BuildTimeline(
            [Seg(56.54, 58.66, "a"), Seg(59.08, 61.56, "b")], 97.0);
        Check(timeline.At(57.0) == VadFlag.HasSub, "giữa thoại phải HasSub");
        Check(timeline.At(10.0) == VadFlag.NoSub, "im lặng phải NoSub");
        // Pad: 56.54-0.5=56.04 -> 56.0 vẫn HasSub; 55.0 ngoài pad -> NoSub.
        Check(timeline.At(56.2) == VadFlag.HasSub, "pad trước phải phủ");
        Check(timeline.At(55.0) == VadFlag.NoSub, "ngoài pad phải NoSub");
    }

    private static void CorroboratedDialogueScoresHigh()
    {
        // Cue 33 vs tai nghe: khác 1 chữ (cô/anh) -> giống ~0.9 -> giữ.
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(56.54, 58.66, "对他的未来没有帮助")], 56.566, 58.133);
        Check(overlapping.Count == 1, "phải chồng lấn");
        var similarity = OcrSpeechMatch.MaxSimilarity("对她的未来没有帮助", overlapping);
        Check(similarity >= OcrSpeechMatch.StrongSimilarity,
            $"thoại thật phải khớp mạnh (được {similarity:0.00})");
        // Cue 32: tai nghe thiếu đầu câu nhưng lõi giống -> trên ngưỡng drop.
        var overlapping2 = OcrSpeechMatch.OverlappingSegments(
            [Seg(55.0, 56.54, "作为做饭做家务")], 53.666, 56.400);
        var similarity2 = OcrSpeechMatch.MaxSimilarity("龙王在她眼里只会做饭做家务", overlapping2);
        Check(similarity2 >= OcrSpeechMatch.MinSimilarity,
            $"thoại lệch đầu phải qua ngưỡng drop (được {similarity2:0.00})");
    }

    private static void SceneTextScoresLow()
    {
        // Cue 34-36 (chữ trên sách 休书): tai nghe "第二"/"冷氏集团..." ở cùng
        // thời gian nhưng chữ khác hẳn -> giống thấp -> drop.
        var segs = new[]
        {
            Seg(56.54, 58.66, "对他的未来没有帮助"),
            Seg(58.66, 59.08, "第二"),
            Seg(59.08, 61.56, "冷氏集团这三年越做越强"),
        };
        var overlapping = OcrSpeechMatch.OverlappingSegments(segs, 58.133, 59.567);
        Check(overlapping.Count == 3, "phải thấy 3 segment chồng");
        var similarity = OcrSpeechMatch.MaxSimilarity(
            "我们早已不是一个世界的人，我己经确定新的眼惘叶象。同洗四堂了人", overlapping);
        Check(similarity < OcrSpeechMatch.MinSimilarity,
            $"chữ cảnh phải giống thấp (được {similarity:0.00})");
    }

    private static void SilenceGapMeasuresDistance()
    {
        var segs = new[] { Seg(10.0, 12.0, "a"), Seg(20.0, 22.0, "b") };
        // Cue trong im lặng sâu (15s, cách thoại 3s) -> gap lớn -> drop.
        Check(OcrSpeechMatch.SilenceGap(15.0, 15.5, segs) >= 3.0, "gap im lặng sai");
        // Cue chồng thoại -> gap 0.
        Check(OcrSpeechMatch.SilenceGap(10.5, 11.5, segs) == 0, "chồng thoại phải gap 0");
        // Cue kề thoại (khe 0.2s) -> gap nhỏ -> không drop.
        Check(OcrSpeechMatch.SilenceGap(12.2, 12.5, segs) < OcrSpeechMatch.SilenceDropGapSeconds,
            "khe thoại không được drop");
        // Không segment nào -> vô cực (fail-open ở caller).
        Check(double.IsPositiveInfinity(OcrSpeechMatch.SilenceGap(5.0, 6.0, [])),
            "rỗng phải vô cực");
    }

    private static void TrimRefusesHanTrailing()
    {
        // ĐỔI SPEC 18/09 (luật thép chống xén oan, harness 8778 cue thật chứng
        // minh guards cũ không cứu được: 后世之人→世之, 龙王逃课→王逃...): nhát
        // cắt dính chữ Hán thì TỪ CHỐI, kể cả ca rác Hán thật. Rác Hán dính rìa
        // (hiếm) sửa tay; không đoán.
        // Cue 31 cũ ("…龙王而你，"): cắt "而你，" là cắt Hán -> từ chối.
        var words = new[]
        {
            W("她", 61.53, 62.01, 0.87), W("觉得", 62.22, 62.4), W("自己", 62.4, 62.66),
            W("和", 62.66, 62.86), W("龙", 62.86, 63.0), W("王", 63.0, 63.2, 1.0),
            W("已经", 63.2, 63.42),
        };
        var trimmed = OcrSpeechMatch.TrimEdges("她觉得自己和龙王而你，", 62.1, 63.3, words);
        Check(trimmed is null, $"đuôi Hán không được cắt (được '{trimmed}')");
        // Cue 32 cũ ("…的人而作"): cắt "而作" là cắt Hán -> từ chối.
        var words2 = new[]
        {
            W("已经", 63.2, 63.42), W("不是", 63.42, 63.72), W("一个", 63.72, 63.98),
            W("世界", 63.98, 64.26), W("的人", 64.26, 64.54),
        };
        var trimmed2 = OcrSpeechMatch.TrimEdges("已经不是一个世界的人而作", 63.3, 64.8, words2);
        Check(trimmed2 is null, $"đuôi Hán không được cắt (được '{trimmed2}')");
    }

    private static void TrimRefusesHanLeading()
    {
        // "人第一" + tai nghe "第一": cắt "人" là cắt Hán -> TỪ CHỐI (spec mới).
        var words = new[] { W("第", 52.8, 52.95, 0.95), W("一", 52.95, 53.1, 0.95) };
        var trimmed = OcrSpeechMatch.TrimEdges("人第一", 52.8, 53.1, words);
        Check(trimmed is null, $"đầu Hán không được cắt (được '{trimmed}')");
    }

    private static void TrimKeepsCleanCue()
    {
        // Cue sạch khớp hết tai nghe -> null (không đụng).
        var words = new[]
        {
            W("冷", 59.37, 59.81), W("氏", 59.81, 59.99), W("集", 59.99, 60.15),
            W("团", 60.15, 60.29),
        };
        var trimmed = OcrSpeechMatch.TrimEdges("冷氏集团", 59.5, 60.2, words);
        Check(trimmed is null, "cue sạch không được cắt");
    }

    private static void TrimRefusesUnsafe()
    {
        var words = new[] { W("你好", 10.0, 10.5) };
        // Tai điếc (tin thấp) -> từ chối.
        var deaf = new[] { W("你好", 10.0, 10.5, 0.5) };
        Check(OcrSpeechMatch.TrimEdges("你好啊啊", 10.0, 10.5, deaf) is null, "tai điếc không được cắt");
        // Không word nào -> null.
        Check(OcrSpeechMatch.TrimEdges("你好啊啊", 10.0, 10.5, []) is null, "rỗng phải null");
        // Rác quá nửa cue -> không cắt (để drop-logic xử, tránh xén oan).
        var trimmed = OcrSpeechMatch.TrimEdges("甲乙丙丁戊己", 10.0, 11.0, words);
        Check(trimmed is null, "rác đa số không được cắt");
    }

    // Bug cue 36 (18/09): OCR đúng 没有之一, tai cửa sổ hẹp chỉ nghe 之一
    // nhưng tai lân cận RỘNG có nói 没有 (lệch timing) -> cấm cắt.
    private static void TrimRefusesWhenCutHeardNearby()
    {
        var words = new[]
        {
            W("之", 135.6, 135.85), W("一", 135.85, 136.1),
            W("我", 136.1, 136.3), W("身", 136.3, 136.45),
        };
        const string nearby = "刚才没有之一我身后这座墓";
        var trimmed = OcrSpeechMatch.TrimEdges("没有之一", 135.533, 136.5, words, nearby);
        Check(trimmed is null, $"tai lân cận có 没有 thì không được cắt (được '{trimmed}')");
    }

    // Tai bắt đầu muộn hẳn so với đầu cue -> không có bằng chứng rìa đầu là
    // rác (từ thật của tai có thể nằm ngoài cửa sổ) -> cấm cắt đầu.
    private static void TrimRefusesWhenEarStartsLate()
    {
        var words = new[]
        {
            W("某", 10.8, 11.2), W("不", 11.2, 11.6), W("搭", 11.6, 12.0),
            W("后", 12.0, 12.4), W("语", 12.4, 12.8), W("啊", 12.8, 13.2),
            W("哈", 13.2, 13.6), W("哈", 13.6, 14.0),
        };
        const string nearby = "某不搭后语啊哈哈";
        var trimmed = OcrSpeechMatch.TrimEdges("前言不搭后语啊", 10.0, 14.0, words, nearby);
        Check(trimmed is null, $"tai bắt đầu muộn thì không được cắt đầu (được '{trimmed}')");
    }

    // Tai chỉ phủ thưa (~0.62 < 0.75) thời lượng cue -> tai sót chữ, không đủ
    // chắc để cắt.
    private static void TrimRefusesSparseCoverage()
    {
        var words = new[] { W("之", 135.7, 136.0), W("一", 136.0, 136.3) };
        const string nearby = "之一我身后";
        var trimmed = OcrSpeechMatch.TrimEdges("没有之一", 135.533, 136.5, words, nearby);
        Check(trimmed is null, $"phủ thưa thì không được cắt (được '{trimmed}')");
    }

    // Bẫy độ dài (18/09): cue ngắn 好的 nằm nguyên văn trong segment dài
    // 好的我们开始吧 nhưng sim Levenshtein chỉ 0.25 < ngưỡng drop 0.3 — tai
    // CÓ nói đúng chữ đó nên cấm drop theo sim.
    private static void VerbatimContainedExemptsShortCue()
    {
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(10.0, 20.0, "好的我们开始吧")], 12.0, 12.5);
        Check(overlapping.Count == 1, "phải chồng lấn");
        var sim = OcrSpeechMatch.MaxSimilarity("好的", overlapping);
        Check(sim < OcrSpeechMatch.MinSimilarity, $"bẫy độ dài phải lộ (sim={sim:0.00})");
        Check(OcrSpeechMatch.IsVerbatimContained("好的", overlapping), "nguyên văn phải miễn drop");
    }

    // Chữ tai không hề nói thì không miễn.
    private static void VerbatimRejectsUnheard()
    {
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(10.0, 20.0, "好的我们开始吧")], 12.0, 12.5);
        Check(!OcrSpeechMatch.IsVerbatimContained("龙王庙", overlapping), "chữ lạ không được miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("", overlapping), "rỗng không miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("好的", []), "không segment không miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("好的", null!), "null không miễn");
    }

    // Luật chung 18/09: tai PHỦ KÍN cue bằng chữ TỰ TIN mới là bằng chứng để
    // buộc tội (mâu thuẫn dương tính). Hàm này chỉ đo độ phủ — chữ giống hay
    // khác do sim/containment quyết ở ngoài. Phủ kín tin cao -> true.
    private static void EarCoversDenseSpan()
    {
        var words = new[] { W("啊", 10.0, 10.5), W("啊", 10.5, 11.0) };
        Check(OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "phủ kín tin cao phải true");
        var edge = new[] { W("啊", 10.0, 10.75) };
        Check(OcrSpeechMatch.EarCoversSpan(10.0, 11.0, edge), "đúng ngưỡng 0.75 phải true");
    }

    // Tai thưa (20%) thì không phải bằng chứng — dù chữ có khác hẳn.
    private static void EarSparseIsNotEvidence()
    {
        var words = new[] { W("啊", 10.0, 10.2) };
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "phủ thưa không được buộc tội");
    }

    // Phủ kín nhưng tin thấp (tai nghe bừa) thì không phải bằng chứng.
    private static void EarLowConfIsNotEvidence()
    {
        var words = new[] { W("啊", 10.0, 10.5, 0.5), W("啊", 10.5, 11.0, 0.5) };
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "tin thấp không được buộc tội");
    }

    // Không words thì không có gì để buộc tội (fail-closed về giữ).
    private static void EarEmptyIsNotEvidence()
    {
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, []), "rỗng phải false");
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, null!), "null phải false");
    }

    // ĐỔI SPEC 18/09 (luật thép chống xén oan): tường chữ HÁN thì KHÔNG gỡ,
    // kể cả tai vắng mặt (tai sót tên riêng là chuyện thường; harness 8778 cue
    // thật: 陆鸣同学→同学, 纯阳之体→之体, 别绷太紧→太紧...). Tường Hán cần quét
    // lại có cổng màu hoặc sửa tay.
    private static void PeelRefusesHanFlood()
    {
        var words = new[]
        {
            W("我", 12407.80, 12407.92), W("心", 12407.92, 12408.04),
            W("里", 12408.04, 12408.16), W("还", 12408.16, 12408.28),
            W("是", 12408.28, 12408.40), W("空", 12408.40, 12408.52),
            W("落", 12408.52, 12408.64), W("落", 12408.64, 12408.76),
            W("的", 12408.76, 12408.88),
        };
        const string nearby = "可为什么我心里还是空落落的明天再说";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支我心里还是空落落的陪一支陪", 12407.73, 12409.00, words, nearby);
        Check(peeled is null, $"tường Hán không được gỡ (được '{peeled}')");
    }

    // Thoại lặp thật (完了×3, tai nghe đủ 3) -> cấm gỡ (spokenFamCount > 0).
    private static void PeelRefusesGenuineTriple()
    {
        var words = new[]
        {
            W("完了", 10.0, 10.3), W("完了", 10.3, 10.6), W("完了", 10.6, 10.9),
        };
        const string nearby = "完了完了完了";
        var peeled = OcrSpeechMatch.TrimRepeatTiles("完了完了完了", 10.0, 11.0, words, nearby);
        Check(peeled is null, $"lặp thật tai nghe đủ thì giữ (được '{peeled}')");
    }

    // Tai nghe nguyên văn cả cue (kể cả tile) -> không có gì để gỡ.
    private static void PeelRefusesFullEcho()
    {
        var words = new[]
        {
            W("我只是", 20.0, 20.5), W("只是我", 20.5, 21.0),
            W("只是刚", 21.0, 21.5), W("好路过", 21.5, 22.0),
        };
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "我只是只是我只是刚好路过", 20.0, 22.0, words, "我只是只是我只是刚好路过");
        Check(peeled is null, $"tai nghe hết thì giữ (được '{peeled}')");
    }

    // Biến thể Hán (陪/赔) cũng KHÔNG gỡ (luật thép: không gỡ chữ Hán).
    private static void PeelRefusesHanVariant()
    {
        var words = new[]
        {
            W("昨", 30.0, 30.15), W("晚", 30.15, 30.3), W("云", 30.3, 30.6),
            W("烟", 30.6, 30.9), W("很", 30.9, 31.1), W("大", 31.1, 31.25),
        };
        const string nearby = "昨晚云烟很大";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支云烟赔一支陪一支", 30.0, 31.3, words, nearby);
        Check(peeled is null, $"họ biến thể Hán không được gỡ (được '{peeled}')");
    }

    // SPEC MỚI 19/09 (user duyệt gỡ tile Hán khi tai im): tường danmaku
    // 陪一支×3 + lõi thoại tai có nghe (云烟) -> gỡ tường giữ lõi.
    private static void PeelStripsHanWallMixedCore()
    {
        var words = new[] { W("云", 10.05, 10.25), W("烟", 10.25, 10.45) };
        const string nearby = "云烟";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支陪一支云烟陪一支", 10.0, 10.5, words, nearby);
        Check(peeled == "云烟", $"tường Hán + lõi tai-nghe phải gỡ giữ lõi (được '{peeled}')");
    }

    // Tường Hán THUẦN (không lõi) -> null fail-closed (judge mâu thuẫn drop
    // khi có tiếng / F2 gắn cờ khi im — peel không bịa lõi).
    private static void PeelPureHanWallStaysForJudge()
    {
        var words = new[] { W("他", 10.05, 10.45) };
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支陪一支陪一支", 10.0, 10.5, words, "他");
        Check(peeled is null, $"tường thuần phải null cho judge xử (được '{peeled}')");
    }

    // R3 (video Cả Lớp 19/09 cue 3617 "A…2…35?"): token lẫn chữ+số ở rìa
    // (A2, p01) cấm cắt — cắt là đổi nghĩa/mất mã.
    private static void TrimProtectsAlnumToken()
    {
        var words = new[]
        {
            W("出", 10.05, 10.25), W("场", 10.25, 10.45), W("了", 10.45, 10.70),
        };
        const string nearby = "出场了";
        var trimmed = OcrSpeechMatch.TrimEdges("A2出场了", 10.0, 10.8, words, nearby);
        Check(trimmed is null, $"token lai A2 cấm cắt (được '{trimmed}')");
    }

    // Đối chứng R3: cụm THUẦN số (482) vẫn cắt như cũ (R1/R2 quyết).
    private static void TrimStillCutsPureDigitAffix()
    {
        var words = new[]
        {
            W("出", 10.05, 10.25), W("场", 10.25, 10.45), W("了", 10.45, 10.70),
        };
        const string nearby = "出场了";
        var trimmed = OcrSpeechMatch.TrimEdges("482出场了", 10.0, 10.8, words, nearby);
        Check(trimmed == "出场了", $"số thuần vẫn cắt (được '{trimmed}')");
    }

    private static EditorSpeechSegment SegW(
        double start, double end, string text, IReadOnlyList<EditorWordTiming> words) =>
        new(start, end, text, -0.2, 0.0, words, "uncertain", 0, 0);

    // Tai sửa 1 chữ (VD 5137.7s): cue 云烟很天 vs tai 昨晚云烟很大, word tin
    // cao -> 云烟很大.
    private static void CorrectSingleCharPositive()
    {
        var words = new[]
        {
            W("昨", 9.6, 9.8), W("晚", 9.8, 10.0), W("云", 10.0, 10.2),
            W("烟", 10.2, 10.4), W("很", 10.4, 10.6), W("大", 10.6, 10.8),
        };
        var overlapping = new[] { SegW(9.5, 11.5, "昨晚云烟很大", words) };
        var fixedText = OcrSpeechMatch.TryCorrectSingleChar("云烟很天", overlapping, words);
        Check(fixedText == "云烟很大", $"phải sửa 1 chữ (được '{fixedText}')");
    }

    // Từ chối sửa: 2 chữ khác / tai điếc / giản-phồn / cue ngắn.
    private static void CorrectSingleCharRefusals()
    {
        var words = new[]
        {
            W("昨", 9.6, 9.8), W("晚", 9.8, 10.0), W("云", 10.0, 10.2),
            W("烟", 10.2, 10.4), W("很", 10.4, 10.6), W("大", 10.6, 10.8),
        };
        var overlapping = new[] { SegW(9.5, 11.5, "昨晚云烟很大", words) };
        Check(OcrSpeechMatch.TryCorrectSingleChar("云火很小", overlapping, words) is null,
            "2 chữ khác không được sửa");
        var deaf = words.Select(word => new EditorWordTiming(word.Text, word.Start, word.End, 0.5)).ToArray();
        Check(OcrSpeechMatch.TryCorrectSingleChar("云烟很天", overlapping, deaf) is null,
            "tai điếc không được sửa");
        Check(OcrSpeechMatch.TryCorrectSingleChar("强身健体",
            new[] { SegW(9.5, 11.5, "強身健體", words) }, words) is null,
            "giản-phồn không phải lỗi");
        Check(OcrSpeechMatch.TryCorrectSingleChar("好天", overlapping, words) is null,
            "cue ngắn không sửa");
        Check(OcrSpeechMatch.TryCorrectSingleChar("云烟很天", overlapping, []) is null,
            "rỗng words không sửa");
    }

    // Tai điếc / không words -> từ chối (fail-closed).
    private static void PeelRefusesDeafEar()
    {
        var deaf = new[] { W("陪一支", 12407.8, 12408.0, 0.5), W("我心里", 12408.0, 12408.5, 0.5) };
        Check(OcrSpeechMatch.TrimRepeatTiles("陪一支我心里", 12407.73, 12409.0, deaf, "x") is null,
            "tai điếc không được gỡ");
        Check(OcrSpeechMatch.TrimRepeatTiles("陪一支我心里", 12407.73, 12409.0, [], "x") is null,
            "rỗng words không gỡ");
    }

    // Dấu câu kẹp tile không-Hán: gỡ tile, giữ dấu + lõi (bản đồ rune).
    // Tile "ab"/"cd" lặp + lõi "hi" tai-xác-nhận + dấu kẹp.
    private static void PeelKeepsInternalPunct()
    {
        var words = new[]
        {
            W("x", 10.0, 10.2), W("hi", 10.2, 10.8), W("y", 10.8, 11.0),
        };
        const string nearby = "xhiy";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "ab，cd hi ab，cd", 10.0, 11.0, words, nearby);
        Check(peeled == "hi", $"tile không-Hán phải gỡ (được '{peeled}')");
    }

    // Tile dài 5 chữ (团支书画画×2 ở cue 397 thật): max cũ 4 mù tile này,
    // rớt xuống edge-trim cho ra "hither" sai; max 6 gỡ đúng giữ space lõi.
    private static void PeelLongTileKeepsSpacedCore()
    {
        var words = new[]
        {
            W("say", 10.0, 10.8), W("hi", 10.8, 11.2),
            W("there", 11.2, 11.8), W("ok", 11.8, 12.0),
        };
        const string nearby = "sayhithereok";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "abcde hi abcde there", 10.0, 12.0, words, nearby);
        Check(peeled == "hi there", $"tile dài phải gỡ sạch giữ space (được '{peeled}')");
    }

    // Hóa thạch 18/09 (start=368.63 file user): OCR 有9成概率, tai 有九成概率
    // (chữ số vs chữ Hán). Code cũ cắt thành 成概率; fold số-chữ khiến veto
    // bắt được (有9 ⊆ 有9成) nên giữ nguyên.
    private static void DigitFoldVetoProtectsRealNumber()
    {
        var words = new[]
        {
            W("有", 10.0, 10.2), W("九", 10.2, 10.4), W("成", 10.4, 10.6),
            W("概", 10.6, 10.8), W("率", 10.8, 11.0),
        };
        const string nearby = "有九成概率";
        var trimmed = OcrSpeechMatch.TrimEdges("有9成概率", 10.0, 11.0, words, nearby);
        Check(trimmed is null, $"số thật phải giữ (được '{trimmed}')");
        // Đầu Hán + số (人…): nhát cắt dính Hán thì cấm luôn (luật thép) —
        // sửa tay, không đoán.
        var trimmed2 = OcrSpeechMatch.TrimEdges("人有9成概率", 10.0, 11.0, words, nearby);
        Check(trimmed2 is null, $"đầu Hán không được cắt (được '{trimmed2}')");
    }

    // Vụ 那 2 (18/09, số liệu tai thật): tai nghe 那 nhưng chỉ tin 76% (<0.8).
    // Nhát cắt không-chữ-Hán (số/dấu) được nới xuống 0.75; cắt chữ Hán thì vẫn
    // đòi 0.8 (chống cue-36).
    private static void WeakEarTrimsNonHanCut()
    {
        var words = new[]
        {
            W("那", 17464.04, 17464.14, 0.76), W("好吧", 17464.14, 17465.02),
        };
        const string nearby = "保证很快回来那好吧";
        var trimmed = OcrSpeechMatch.TrimEdges("那 2", 17463.867, 17464.633, words, nearby);
        Check(trimmed == "那", $"số rác phải cắt dù tai 76% (được '{trimmed}')");
    }

    // Đối chứng: cùng tai yếu, nhát cắt chữ Hán thì cấm (luật thép — giờ cấm
    // tuyệt đối chứ không chỉ khi tai yếu).
    private static void WeakEarRefusesHanCut()
    {
        var words = new[] { W("好", 10.0, 10.8, 0.76) };
        const string nearby = "好明天再说";
        var trimmed = OcrSpeechMatch.TrimEdges("人好", 10.0, 10.8, words, nearby);
        Check(trimmed is null, $"tai yếu không được cắt chữ Hán (được '{trimmed}')");
    }

    // Biên số (18/09, hóa thạch user: OCR 18岁那年, tai 八岁那年 — thật/giả
    // không phân biệt được): tách cụm số (cắt "1" chừa "8") và nuốt số tai
    // xác nhận (cắt "18" trong khi tai nói 8) đều TỪ CHỐI — giữ nguyên an toàn
    // hơn (18→8 hay 18→∅ đều đổi/mất sự thật).
    private static void DigitBoundaryRefusesValueChange()
    {
        var words = new[]
        {
            W("八", 10.0, 10.25), W("岁", 10.25, 10.5),
            W("那", 10.5, 10.75), W("年", 10.75, 11.0),
        };
        const string nearby = "八岁那年";
        var trimmed = OcrSpeechMatch.TrimEdges("18岁那年", 10.0, 11.0, words, nearby);
        Check(trimmed is null, $"không được đổi giá trị số (được '{trimmed}')");
    }

    // Đối chứng: số rác không dính số thật (kept không số) thì vẫn cắt.
    private static void DigitAffixWithoutDigitKeptTrims()
    {
        var words = new[]
        {
            W("狗", 10.0, 10.2), W("主", 10.2, 10.4), W("播", 10.4, 10.6),
        };
        const string nearby = "狗主播来了";
        var trimmed = OcrSpeechMatch.TrimEdges("狗主播 482", 10.0, 10.6, words, nearby);
        Check(trimmed == "狗主播", $"số rác phải cắt (được '{trimmed}')");
    }

    // Cổng áp dụng fix-cắt (18/09): strong/review/tin-thấp cho qua mọi nhát;
    // cue tin cao tai-vừa chỉ cho qua nhát gỡ số/dấu/latin (không xén Hán).
    private static void ApplyGateLocksHanCuts()
    {
        // Nhát cắt Hán trên cue tin cao -> cấm (dù lõi khớp).
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "没有之一", "之一"),
            "cắt Hán cue tin cao phải cấm");
        // Dấu câu thuần trên cue tin cao -> cấm (giữ giọng câu).
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "你好，", "你好"),
            "cắt dấu cue tin cao phải cấm");
        // Bằng nhau/rỗng -> cấm.
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB", "AB"), "bằng nhau phải cấm");
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB", ""), "rỗng phải cấm");
    }

    private static void ApplyGateOpensDigitCuts()
    {
        // Nhát cắt số/dấu/latin trên cue tin cao -> cho qua.
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB 12", "AB"),
            "cắt số cue tin cao phải cho qua");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "那 2", "那"),
            "cắt số cue tin cao phải cho qua");
        // Cổng cũ vẫn mở: strong/review/tin-thấp.
        Check(OcrSpeechMatch.ShouldApplyTrimFix(true, false, 0.99, "ABx", "AB"), "strong phải mở");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, true, 0.99, "ABx", "AB"), "review phải mở");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.7, "ABx", "AB"), "tin thấp phải mở");
    }

    // Phương án A (18/09): khe im 90s (> 60s) -> vùng quét kỹ (Suspicious),
    // nhưng lõi thoại + pad giữ HasSub.
    private static void LongSilence90sMarkedFullScan()
    {
        var segs = new[] { Seg(0.0, 10.0, "a"), Seg(100.0, 110.0, "b") };
        var zones = OcrSpeechMatch.FindLongSilences(segs, 200.0);
        Check(zones.Count == 2, $"khe 90s đầu+cuối phải bắt 2 vùng (được {zones.Count})");
        var timeline = OcrSpeechMatch.BuildTimeline(segs, 200.0);
        OcrSpeechMatch.MarkLongSilences(timeline, zones);
        Check(timeline.At(50.0) == VadFlag.Suspicious, "giữa khe 90s phải quét kỹ");
        Check(timeline.At(150.0) == VadFlag.Suspicious, "khe cuối 90s phải quét kỹ");
        Check(timeline.At(5.0) == VadFlag.HasSub, "lõi thoại phải giữ HasSub");
        Check(timeline.At(105.0) == VadFlag.HasSub, "lõi thoại sau phải giữ HasSub");
    }

    // Khe im 30s (< 60s) -> vẫn NoSub (skip như cũ, nhanh).
    private static void ShortSilence30sStaysSkippable()
    {
        var segs = new[] { Seg(0.0, 10.0, "a"), Seg(40.0, 50.0, "b") };
        var zones = OcrSpeechMatch.FindLongSilences(segs, 200.0);
        // Khe giữa 30s không bắt; khe cuối 150s bắt.
        Check(zones.Count == 1, $"chỉ khe cuối dài được bắt (được {zones.Count})");
        var timeline = OcrSpeechMatch.BuildTimeline(segs, 200.0);
        OcrSpeechMatch.MarkLongSilences(timeline, zones);
        Check(timeline.At(25.0) == VadFlag.NoSub, "khe 30s vẫn skip được");
    }

    // Video câm hoàn toàn 120s -> 1 vùng quét kỹ cả video.
    private static void FullyMuteVideoMarkedFullScan()
    {
        var zones = OcrSpeechMatch.FindLongSilences([], 120.0);
        Check(zones.Count == 1, "câm full phải 1 vùng");
        var timeline = OcrSpeechMatch.BuildTimeline([], 120.0);
        OcrSpeechMatch.MarkLongSilences(timeline, zones);
        Check(timeline.At(0.0) == VadFlag.Suspicious, "đầu video câm phải quét kỹ");
        Check(timeline.At(60.0) == VadFlag.Suspicious, "giữa video câm phải quét kỹ");
        Check(timeline.At(119.9) == VadFlag.Suspicious, "cuối video câm phải quét kỹ");
        // Rác đầu vào -> rỗng, không nổ.
        Check(OcrSpeechMatch.FindLongSilences(null, 120.0).Count == 1, "null speech như câm");
        Check(OcrSpeechMatch.FindLongSilences([], 0).Count == 0, "duration 0 phải rỗng");
        Check(OcrSpeechMatch.FindLongSilences([], 30.0).Count == 0, "video ngắn hơn ngưỡng phải rỗng");
    }

    // Mẫu trong vùng quét kỹ (đứng yên + neo trắng) -> council vẫn KHÔNG skip
    // (vote Suspicious, không phải NoSub).
    private static void NoSkipInsideFullScanZone()
    {
        var segs = new[] { Seg(0.0, 10.0, "a"), Seg(100.0, 110.0, "b") };
        var timeline = OcrSpeechMatch.BuildTimeline(segs, 200.0);
        OcrSpeechMatch.MarkLongSilences(timeline,
            OcrSpeechMatch.FindLongSilences(segs, 200.0));
        var vote = timeline.At(50.0);
        Check(vote != VadFlag.NoSub, "vùng quét kỹ không được vote NoSub");
        Check(!OcrGateShadow.ShouldCouncilSkip(
            vadSilent: vote == VadFlag.NoSub,
            bandScore: 0.0, prevBlankAnchored: true, consecutiveSkips: 0),
            "council phải từ chối skip trong vùng quét kỹ");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

using System.Text.Json;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Ear-eye corroboration (17/09): what Whisper hears vs what OCR sees. Tested with real
// measurements taken from video 0916 (faster-whisper-small, CPU int8).
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
        // 2 speech segments 56.5-58.6 and 59.0-61.5, pad ±0.5s.
        var timeline = OcrSpeechMatch.BuildTimeline(
            [Seg(56.54, 58.66, "a"), Seg(59.08, 61.56, "b")], 97.0);
        Check(timeline.At(57.0) == VadFlag.HasSub, "giữa thoại phải HasSub");
        Check(timeline.At(10.0) == VadFlag.NoSub, "im lặng phải NoSub");
        // Pad: 56.54-0.5=56.04 -> 56.0 still HasSub; 55.0 outside pad -> NoSub.
        Check(timeline.At(56.2) == VadFlag.HasSub, "pad trước phải phủ");
        Check(timeline.At(55.0) == VadFlag.NoSub, "ngoài pad phải NoSub");
    }

    private static void CorroboratedDialogueScoresHigh()
    {
        // Cue 33 vs the ear: 1 character differs (cô/anh) -> similarity ~0.9 -> keep.
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(56.54, 58.66, "对他的未来没有帮助")], 56.566, 58.133);
        Check(overlapping.Count == 1, "phải chồng lấn");
        var similarity = OcrSpeechMatch.MaxSimilarity("对她的未来没有帮助", overlapping);
        Check(similarity >= OcrSpeechMatch.StrongSimilarity,
            $"thoại thật phải khớp mạnh (được {similarity:0.00})");
        // Cue 32: the ear misses the sentence head but the core is similar -> above the drop threshold.
        var overlapping2 = OcrSpeechMatch.OverlappingSegments(
            [Seg(55.0, 56.54, "作为做饭做家务")], 53.666, 56.400);
        var similarity2 = OcrSpeechMatch.MaxSimilarity("龙王在她眼里只会做饭做家务", overlapping2);
        Check(similarity2 >= OcrSpeechMatch.MinSimilarity,
            $"thoại lệch đầu phải qua ngưỡng drop (được {similarity2:0.00})");
    }

    private static void SceneTextScoresLow()
    {
        // Cue 34-36 (text on the book 休书): the ear hears "第二"/"冷氏集团..." at the
        // same time but the characters are entirely different -> low similarity -> drop.
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
        // Cue in deep silence (15s, 3s from speech) -> large gap -> drop.
        Check(OcrSpeechMatch.SilenceGap(15.0, 15.5, segs) >= 3.0, "gap im lặng sai");
        // Cue overlapping speech -> gap 0.
        Check(OcrSpeechMatch.SilenceGap(10.5, 11.5, segs) == 0, "chồng thoại phải gap 0");
        // Cue adjacent to speech (0.2s gap) -> small gap -> no drop.
        Check(OcrSpeechMatch.SilenceGap(12.2, 12.5, segs) < OcrSpeechMatch.SilenceDropGapSeconds,
            "khe thoại không được drop");
        // No segments -> infinity (fail-open at the caller).
        Check(double.IsPositiveInfinity(OcrSpeechMatch.SilenceGap(5.0, 6.0, [])),
            "rỗng phải vô cực");
    }

    private static void TrimRefusesHanTrailing()
    {
        // SPEC CHANGE 18/09 (iron rule against wrongful trimming; the 8778-real-cue harness
        // proves the old guards could not save these: 后世之人→世之, 龙王逃课→王逃...): a cut
        // edge touching Han characters is REFUSED, even for genuinely Han garbage. Han garbage touching an
        // edge (rare) is fixed by hand; no guessing.
        // Old cue 31 ("…龙王而你，"): cutting "而你，" cuts Han -> refuse.
        var words = new[]
        {
            W("她", 61.53, 62.01, 0.87), W("觉得", 62.22, 62.4), W("自己", 62.4, 62.66),
            W("和", 62.66, 62.86), W("龙", 62.86, 63.0), W("王", 63.0, 63.2, 1.0),
            W("已经", 63.2, 63.42),
        };
        var trimmed = OcrSpeechMatch.TrimEdges("她觉得自己和龙王而你，", 62.1, 63.3, words);
        Check(trimmed is null, $"đuôi Hán không được cắt (được '{trimmed}')");
        // Old cue 32 ("…的人而作"): cutting "而作" cuts Han text -> reject.
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
        // "人第一" + the ear hears "第一": cutting "人" cuts Han -> REFUSE (new spec).
        var words = new[] { W("第", 52.8, 52.95, 0.95), W("一", 52.95, 53.1, 0.95) };
        var trimmed = OcrSpeechMatch.TrimEdges("人第一", 52.8, 53.1, words);
        Check(trimmed is null, $"đầu Hán không được cắt (được '{trimmed}')");
    }

    private static void TrimKeepsCleanCue()
    {
        // Clean cue fully matching the ear -> null (untouched).
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
        // Deaf ear (low confidence) -> refuse.
        var deaf = new[] { W("你好", 10.0, 10.5, 0.5) };
        Check(OcrSpeechMatch.TrimEdges("你好啊啊", 10.0, 10.5, deaf) is null, "tai điếc không được cắt");
        // No words -> null.
        Check(OcrSpeechMatch.TrimEdges("你好啊啊", 10.0, 10.5, []) is null, "rỗng phải null");
        // Garbage over half the cue -> no cut (leave it to the drop logic, avoid wrongful trimming).
        var trimmed = OcrSpeechMatch.TrimEdges("甲乙丙丁戊己", 10.0, 11.0, words);
        Check(trimmed is null, "rác đa số không được cắt");
    }

    // Cue 36 bug (18/09): OCR reads 没有之一 correctly; a narrow-window ear hears only 之一
    // but the nearby WIDE ear does say 没有 (timing offset) -> forbid cutting.
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

    // Ear starts much later than the cue start -> no evidence that the leading edge is
    // garbage (the ear's real words may lie outside the window) -> forbid trimming the head.
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

    // Ear covers the cue duration only sparsely (~0.62 < 0.75) -> the ear misses words, not
    // confident enough to cut.
    private static void TrimRefusesSparseCoverage()
    {
        var words = new[] { W("之", 135.7, 136.0), W("一", 136.0, 136.3) };
        const string nearby = "之一我身后";
        var trimmed = OcrSpeechMatch.TrimEdges("没有之一", 135.533, 136.5, words, nearby);
        Check(trimmed is null, $"phủ thưa thì không được cắt (được '{trimmed}')");
    }

    // Length trap (18/09): the short cue 好的 sits verbatim inside the long segment
    // 好的我们开始吧 but Levenshtein similarity is only 0.25 < drop threshold 0.3 — the
    // ear DID say exactly that text, so dropping by similarity is forbidden.
    private static void VerbatimContainedExemptsShortCue()
    {
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(10.0, 20.0, "好的我们开始吧")], 12.0, 12.5);
        Check(overlapping.Count == 1, "phải chồng lấn");
        var sim = OcrSpeechMatch.MaxSimilarity("好的", overlapping);
        Check(sim < OcrSpeechMatch.MinSimilarity, $"bẫy độ dài phải lộ (sim={sim:0.00})");
        Check(OcrSpeechMatch.IsVerbatimContained("好的", overlapping), "nguyên văn phải miễn drop");
    }

    // Text the ear never said gets no exemption.
    private static void VerbatimRejectsUnheard()
    {
        var overlapping = OcrSpeechMatch.OverlappingSegments(
            [Seg(10.0, 20.0, "好的我们开始吧")], 12.0, 12.5);
        Check(!OcrSpeechMatch.IsVerbatimContained("龙王庙", overlapping), "chữ lạ không được miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("", overlapping), "rỗng không miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("好的", []), "không segment không miễn");
        Check(!OcrSpeechMatch.IsVerbatimContained("好的", null!), "null không miễn");
    }

    // General rule 18/09: only an ear that COVERS the cue fully with CONFIDENT words is
    // evidence to convict (positive contradiction). This function only measures coverage —
    // whether the characters match or differ is decided outside by sim/containment.
    // Dense high-confidence coverage -> true.
    private static void EarCoversDenseSpan()
    {
        var words = new[] { W("啊", 10.0, 10.5), W("啊", 10.5, 11.0) };
        Check(OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "phủ kín tin cao phải true");
        var edge = new[] { W("啊", 10.0, 10.75) };
        Check(OcrSpeechMatch.EarCoversSpan(10.0, 11.0, edge), "đúng ngưỡng 0.75 phải true");
    }

    // A sparse ear (20%) is not evidence — even if the characters are entirely different.
    private static void EarSparseIsNotEvidence()
    {
        var words = new[] { W("啊", 10.0, 10.2) };
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "phủ thưa không được buộc tội");
    }

    // Dense but low confidence (an ear hearing at random) is not evidence either.
    private static void EarLowConfIsNotEvidence()
    {
        var words = new[] { W("啊", 10.0, 10.5, 0.5), W("啊", 10.5, 11.0, 0.5) };
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, words), "tin thấp không được buộc tội");
    }

    // With no words there is nothing to convict with (fail-closed toward keep).
    private static void EarEmptyIsNotEvidence()
    {
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, []), "rỗng phải false");
        Check(!OcrSpeechMatch.EarCoversSpan(10.0, 11.0, null!), "null phải false");
    }

    // SPEC CHANGE 18/09 (iron rule against wrongful trimming): a wall of HAN characters is
    // NOT stripped, even when the ear is absent (the ear missing proper names is common;
    // the 8778-real-cue harness: 陆鸣同学→同学, 纯阳之体→之体, 别绷太紧→太紧...). A Han wall
    // needs a rescan with the color gate or a manual fix.
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

    // Genuine repeated speech (完了×3, the ear hears all 3) -> stripping forbidden (spokenFamCount > 0).
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

    // The ear hears the entire cue verbatim (including the tiles) -> nothing to strip.
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

    // Han variants (陪/赔) are also NOT stripped (iron rule: never strip Han characters).
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

    // NEW SPEC 19/09 (user approved stripping Han tiles when the ear is silent): a danmaku
    // wall 陪一支×3 + a speech core the ear did hear (云烟) -> strip the wall, keep the core.
    private static void PeelStripsHanWallMixedCore()
    {
        var words = new[] { W("云", 10.05, 10.25), W("烟", 10.25, 10.45) };
        const string nearby = "云烟";
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支陪一支云烟陪一支", 10.0, 10.5, words, nearby);
        Check(peeled == "云烟", $"tường Hán + lõi tai-nghe phải gỡ giữ lõi (được '{peeled}')");
    }

    // A PURE Han wall (no core) -> null fail-closed (the contradiction judge drops it
    // when there is speech / F2 flags it when silent — peel never fabricates a core).
    private static void PeelPureHanWallStaysForJudge()
    {
        var words = new[] { W("他", 10.05, 10.45) };
        var peeled = OcrSpeechMatch.TrimRepeatTiles(
            "陪一支陪一支陪一支", 10.0, 10.5, words, "他");
        Check(peeled is null, $"tường thuần phải null cho judge xử (được '{peeled}')");
    }

    // R3 (Cả Lớp video 19/09 cue 3617 "A…2…35?"): a letter+digit token at the edge
    // (A2, p01) forbids trimming — cutting changes the meaning / loses the code.
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

    // R3 counter-check: a PURE digit group (482) is still trimmed as before (R1/R2 decide).
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

    // Ear corrects 1 character (e.g. 5137.7s): cue 云烟很天 vs ear 昨晚云烟很大, high-confidence
    // words -> 云烟很大.
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

    // Refuses correction: 2 characters differ / deaf ear / traditional-simplified / short cue.
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

    // Deaf ear / no words -> refuse (fail-closed).
    private static void PeelRefusesDeafEar()
    {
        var deaf = new[] { W("陪一支", 12407.8, 12408.0, 0.5), W("我心里", 12408.0, 12408.5, 0.5) };
        Check(OcrSpeechMatch.TrimRepeatTiles("陪一支我心里", 12407.73, 12409.0, deaf, "x") is null,
            "tai điếc không được gỡ");
        Check(OcrSpeechMatch.TrimRepeatTiles("陪一支我心里", 12407.73, 12409.0, [], "x") is null,
            "rỗng words không gỡ");
    }

    // Punctuation surrounded by non-Han tiles: strip the tiles, keep the punctuation + core (rune map).
    // Repeated tiles "ab"/"cd" + an ear-confirmed core "hi" + the enclosing punctuation.
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

    // Long 5-character tile (团支书画画×2 in real cue 397): the old max of 4 misses this tile and
    // falls through to edge-trim yielding the wrong "hither"; max 6 strips it correctly, keeping the core space.
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

    // Fossil 18/09 (start=368.63, user file): OCR 有9成概率, ear 有九成概率
    // (digit vs Han character). The old code cut it to 成概率; digit-character folding makes the
    // veto catch it (有9 ⊆ 有9成) so it is left intact.
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
        // Han head + digit (人…): any cut edge touching Han is forbidden outright (iron rule) —
        // fix by hand, do not guess.
        var trimmed2 = OcrSpeechMatch.TrimEdges("人有9成概率", 10.0, 11.0, words, nearby);
        Check(trimmed2 is null, $"đầu Hán không được cắt (được '{trimmed2}')");
    }

    // Case 那 2 (18/09, real ear numbers): the ear hears 那 but only at 76% confidence (<0.8).
    // A non-Han cut edge (digits/punctuation) is relaxed to 0.75; cutting Han characters still
    // demands 0.8 (guards against cue 36).
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

    // Counter-check: same weak ear, but a cut edge on Han characters is forbidden (iron rule — now
    // forbidden outright, not only when the ear is weak).
    private static void WeakEarRefusesHanCut()
    {
        var words = new[] { W("好", 10.0, 10.8, 0.76) };
        const string nearby = "好明天再说";
        var trimmed = OcrSpeechMatch.TrimEdges("人好", 10.0, 10.8, words, nearby);
        Check(trimmed is null, $"tai yếu không được cắt chữ Hán (được '{trimmed}')");
    }

    // Digit boundary (18/09, user fossil: OCR 18岁那年, ear 八岁那年 — real/fake cannot be
    // distinguished): splitting the digit group (cut "1", keep "8") and swallowing an
    // ear-confirmed digit (cut "18" while the ear says 8) are both REFUSED — keeping it intact is
    // safer (18→8 or 18→∅ both change or lose the truth).
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

    // Counter-check: garbage digits not touching a real digit (kept has no digits) are still trimmed.
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

    // Gate applying the trim fix (18/09): strong/review/low-confidence lets every cut through;
    // a high-confidence cue with a mid ear only lets digit/punctuation/Latin cuts through (no Han trimming).
    private static void ApplyGateLocksHanCuts()
    {
        // A Han cut edge on a high-confidence cue -> forbidden (even if the core matches).
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "没有之一", "之一"),
            "cắt Hán cue tin cao phải cấm");
        // Pure punctuation on a high-confidence cue -> forbidden (preserve sentence intonation).
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "你好，", "你好"),
            "cắt dấu cue tin cao phải cấm");
        // Equal/empty -> forbidden.
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB", "AB"), "bằng nhau phải cấm");
        Check(!OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB", ""), "rỗng phải cấm");
    }

    private static void ApplyGateOpensDigitCuts()
    {
        // A digit/punctuation/Latin cut edge on a high-confidence cue -> allowed.
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "AB 12", "AB"),
            "cắt số cue tin cao phải cho qua");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.99, "那 2", "那"),
            "cắt số cue tin cao phải cho qua");
        // The old gate still opens: strong/review/low-confidence.
        Check(OcrSpeechMatch.ShouldApplyTrimFix(true, false, 0.99, "ABx", "AB"), "strong phải mở");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, true, 0.99, "ABx", "AB"), "review phải mở");
        Check(OcrSpeechMatch.ShouldApplyTrimFix(false, false, 0.7, "ABx", "AB"), "tin thấp phải mở");
    }

    // Option A (18/09): a 90s silence gap (> 60s) -> a full-scan zone (Suspicious),
    // but the speech core + pad keep HasSub.
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

    // A 30s silence gap (< 60s) -> still NoSub (skip as before, fast).
    private static void ShortSilence30sStaysSkippable()
    {
        var segs = new[] { Seg(0.0, 10.0, "a"), Seg(40.0, 50.0, "b") };
        var zones = OcrSpeechMatch.FindLongSilences(segs, 200.0);
        // The middle 30s gap is not caught; the trailing 150s gap is.
        Check(zones.Count == 1, $"chỉ khe cuối dài được bắt (được {zones.Count})");
        var timeline = OcrSpeechMatch.BuildTimeline(segs, 200.0);
        OcrSpeechMatch.MarkLongSilences(timeline, zones);
        Check(timeline.At(25.0) == VadFlag.NoSub, "khe 30s vẫn skip được");
    }

    // A fully mute 120s video -> 1 full-scan zone for the whole video.
    private static void FullyMuteVideoMarkedFullScan()
    {
        var zones = OcrSpeechMatch.FindLongSilences([], 120.0);
        Check(zones.Count == 1, "câm full phải 1 vùng");
        var timeline = OcrSpeechMatch.BuildTimeline([], 120.0);
        OcrSpeechMatch.MarkLongSilences(timeline, zones);
        Check(timeline.At(0.0) == VadFlag.Suspicious, "đầu video câm phải quét kỹ");
        Check(timeline.At(60.0) == VadFlag.Suspicious, "giữa video câm phải quét kỹ");
        Check(timeline.At(119.9) == VadFlag.Suspicious, "cuối video câm phải quét kỹ");
        // Garbage input -> empty, no crash.
        Check(OcrSpeechMatch.FindLongSilences(null, 120.0).Count == 1, "null speech như câm");
        Check(OcrSpeechMatch.FindLongSilences([], 0).Count == 0, "duration 0 phải rỗng");
        Check(OcrSpeechMatch.FindLongSilences([], 30.0).Count == 0, "video ngắn hơn ngưỡng phải rỗng");
    }

    // A still frame inside a full-scan zone (motionless + white anchor) -> the council still does NOT skip
    // (votes Suspicious, not NoSub).
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

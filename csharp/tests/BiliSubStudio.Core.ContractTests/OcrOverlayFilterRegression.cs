using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrOverlayFilterRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var scanner = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanner")
            ?? throw new InvalidOperationException("missing OcrScanner");
        var filter = scanner.GetMethod("FilterOffBaselineOverlayLines", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR overlay filter");

        var sceneTitle = new OcrResult(true, true, "宗忌森林", .68,
            [new OcrLine("宗忌森林", .68, [49, 30, 497, 131])]);
        var defaultLowerRegion = new OcrRegion(.05, .65, .90, .29);
        var filtered = (OcrResult)(filter.Invoke(null, [sceneTitle, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null"));
        if (filtered.Detected || filtered.Lines.Count != 0)
            throw new InvalidOperationException("left-aligned upper scene title leaked into default lower subtitle OCR");

        var dialogue = new OcrResult(true, true, "所以？", .84,
            [new OcrLine("所以？", .84, [560, 221, 698, 278])]);
        var retainedDialogue = (OcrResult)(filter.Invoke(null, [dialogue, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null for dialogue"));
        if (!retainedDialogue.Detected || retainedDialogue.Text != "所以？")
            throw new InvalidOperationException("bottom dialogue was discarded as an overlay");

        var upperRegion = new OcrRegion(.05, .20, .90, .29);
        var retainedUpper = (OcrResult)(filter.Invoke(null, [sceneTitle, upperRegion])
            ?? throw new InvalidOperationException("OCR overlay filter returned null for upper ROI"));
        if (!retainedUpper.Detected || retainedUpper.Text != "宗忌森林")
            throw new InvalidOperationException("user-selected upper ROI lost valid OCR text");

        var sameBaselineBranding = new OcrResult(true, true, "丟不丢人\n呼啦呼啦2212", .80,
        [
            new OcrLine("丟不丢人", .802, [537, 230, 741, 279]),
            new OcrLine("呼啦呼啦2212", .805, [1033, 263, 1207, 277]),
        ]);
        var filteredBranding = (OcrResult)(filter.Invoke(null, [sameBaselineBranding, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR branding filter returned null"));
        if (filteredBranding.Text != "丟不丢人" || filteredBranding.Lines.Count != 1)
            throw new InvalidOperationException("changing lower-right branding contaminated a subtitle cue");

        var brandingVariant = new OcrResult(true, true, "丢不丢人\n呼啦画啦2213", .79,
        [
            new OcrLine("丢不丢人", .98, [537, 230, 738, 279]),
            new OcrLine("呼啦画啦2213", .52, [1029, 260, 1212, 279]),
        ]);
        var filteredBrandingVariant = (OcrResult)(filter.Invoke(null, [brandingVariant, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR branding-variant filter returned null"));
        if (filteredBrandingVariant.Text != "丢不丢人" || filteredBrandingVariant.Lines.Count != 1)
            throw new InvalidOperationException("a changed lower-right branding reading survived filtering");

        var overlappingBranding = new OcrResult(true, true, "分明是此人破空杀来的声势\n超京速4米版", .95,
        [
            new OcrLine("分明是此人破空杀来的声势", .999, [237, 175, 1040, 255]),
            new OcrLine("超京速4米版", .91, [995, 257, 1145, 277]),
        ]);
        var filteredOverlap = (OcrResult)(filter.Invoke(null, [overlappingBranding, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR overlapping-branding filter returned null"));
        if (filteredOverlap.Text != "分明是此人破空杀来的声势" || filteredOverlap.Lines.Count != 1)
            throw new InvalidOperationException("lower-right branding survived because its box overlapped the subtitle horizontally");

        var closeBranding = new OcrResult(true, true, "只要能有一缕雾气逃脱\n4400", .90,
        [
            new OcrLine("只要能有一缕雾气逃脱", .999, [303, 175, 975, 254]),
            new OcrLine("4400", .812, [990, 246, 1280, 282]),
        ]);
        var filteredCloseBranding = (OcrResult)(filter.Invoke(null, [closeBranding, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR close-branding filter returned null"));
        if (filteredCloseBranding.Text != "只要能有一缕雾气逃脱" || filteredCloseBranding.Lines.Count != 1)
            throw new InvalidOperationException("lower-right branding survived a narrow detector-box gap");

        var legitimateInlineDigit = new OcrResult(true, true, "订单已经送到\n第8集", .99,
        [
            new OcrLine("订单已经送到", .99, [400, 220, 760, 270]),
            new OcrLine("第8集", .99, [980, 220, 1160, 270]),
        ]);
        var retainedInlineDigit = (OcrResult)(filter.Invoke(null, [legitimateInlineDigit, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR inline-digit filter returned null"));
        if (retainedInlineDigit.Lines.Count != 2 || retainedInlineDigit.Text != legitimateInlineDigit.Text)
            throw new InvalidOperationException("same-height legitimate text containing a digit was treated as branding");

        var standaloneBranding = new OcrResult(true, true, "超京清4K版2400", .89,
            [new OcrLine("超京清4K版2400", .89, [990, 246, 1280, 282])]);
        var filteredStandalone = (OcrResult)(filter.Invoke(null, [standaloneBranding, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR standalone-branding filter returned null"));
        if (filteredStandalone.Detected || filteredStandalone.Lines.Count != 0)
            throw new InvalidOperationException("standalone lower-right branding became an OCR cue without dialogue");

        var standaloneSong = new OcrResult(true, true, "呼啦呼啦2212", .93,
            [new OcrLine("呼啦呼啦2212", .93, [1030, 255, 1209, 279])]);
        var filteredSong = (OcrResult)(filter.Invoke(null, [standaloneSong, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR standalone-song filter returned null"));
        if (filteredSong.Detected || filteredSong.Lines.Count != 0)
            throw new InvalidOperationException("standalone lower-right song watermark became an OCR cue without dialogue");

        var retainedUpperDialogue = new OcrResult(true, true, "第8集", .99,
            [new OcrLine("第8集", .99, [560, 120, 700, 180])]);
        var retainedUpperResult = (OcrResult)(filter.Invoke(null, [retainedUpperDialogue, new OcrRegion(.05, .10, .90, .29)])
            ?? throw new InvalidOperationException("OCR upper-ROI standalone filter returned null"));
        if (!retainedUpperResult.Detected || retainedUpperResult.Text != "第8集")
            throw new InvalidOperationException("user-selected upper ROI lost a legitimate standalone caption");

        var retainedLowerEpisode = new OcrResult(true, true, "第8集", .99,
            [new OcrLine("第8集", .99, [1000, 240, 1200, 290])]);
        var retainedLowerEpisodeResult = (OcrResult)(filter.Invoke(null, [retainedLowerEpisode, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR lower episode filter returned null"));
        if (!retainedLowerEpisodeResult.Detected || retainedLowerEpisodeResult.Text != "第8集")
            throw new InvalidOperationException("legitimate lower-right episode caption was treated as branding");

        foreach (var text in new[] { "第108集", "VIP会员" })
        {
            var legitimate = new OcrResult(true, true, text, .99,
                [new OcrLine(text, .99, [1000, 240, 1200, 290])]);
            var retained = (OcrResult)(filter.Invoke(null, [legitimate, defaultLowerRegion])
                ?? throw new InvalidOperationException("OCR mixed-caption filter returned null"));
            if (!retained.Detected || retained.Text != text)
                throw new InvalidOperationException("legitimate mixed lower-right caption was treated as branding");
        }

        var largeBranding = new OcrResult(true, true, "字幕\n呼啦呼啦2212", .95,
        [
            new OcrLine("字幕", .99, [530, 225, 750, 280]),
            new OcrLine("呼啦呼啦2212", .92, [965, 250, 1280, 310]),
        ]);
        var filteredLargeBranding = (OcrResult)(filter.Invoke(null, [largeBranding, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR large-branding filter returned null"));
        if (filteredLargeBranding.Lines.Count != 1 || filteredLargeBranding.Text != "字幕")
            throw new InvalidOperationException("large lower-right branding hijacked the subtitle reference line");

        var lowConfidenceCaption = largeBranding with
        {
            Confidence = .75,
            Lines =
            [
                new OcrLine("字幕", .75, [530, 225, 750, 280]),
                new OcrLine("呼啦呼啦2212", .92, [965, 250, 1280, 310]),
            ],
        };
        var filteredLowConfidence = (OcrResult)(filter.Invoke(null, [lowConfidenceCaption, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR low-confidence branding filter returned null"));
        if (filteredLowConfidence.Lines.Count != 1 || filteredLowConfidence.Text != "字幕")
            throw new InvalidOperationException("large branding hijacked a low-confidence subtitle reference line");

        // Field fixture V01@761.7s: the branding reads with high confidence and
        // sits lower than the caption, so it must not become the baseline that
        // keeps itself alive.
        var baselineHijack = new OcrResult(true, true, "获得奖励\n呼啦呼啦2212", .96,
        [
            new OcrLine("获得奖励", .9999, [534, 251, 744, 320]),
            new OcrLine("呼啦呼啦2212", .92, [1031, 302, 1209, 319]),
        ]);
        var filteredHijack = (OcrResult)(filter.Invoke(null, [baselineHijack, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR baseline-hijack filter returned null"));
        if (filteredHijack.Text != "获得奖励" || filteredHijack.Lines.Count != 1)
            throw new InvalidOperationException("high-confidence lower-right branding hijacked the subtitle baseline");

        // Raw Paddle detections, kept separate by the worker's inline-row guard.
        // The scene glyphs must not contaminate either text or tracker union boxes.
        var sceneCases = new[]
        {
            new OcrResult(true, true, "书\n明\n老头也是眉开眼笑", .99,
            [
                new OcrLine("书", .99996, [96, 39, 174, 175]),
                new OcrLine("明", .99999, [857, 197, 930, 283]),
                new OcrLine("老头也是眉开眼笑", .99997, [449, 212, 830, 267]),
            ]),
            new OcrResult(true, true, "有行否可发同容放\n是否取消本次任务\n雪\n司", .81,
            [
                new OcrLine("有行否可发同容放", .817, [279, 45, 690, 69]),
                new OcrLine("是否取消本次任务", .99973, [464, 186, 814, 245]),
                new OcrLine("雪", .7075, [347, 196, 432, 281]),
                new OcrLine("司", .9325, [1257, 226, 1279, 254]),
            ]),
        };
        foreach (var scene in sceneCases)
        {
            var expected = scene.Lines.Single(line => line.Box[0] < 640 && line.Box[2] > 640 && line.Confidence > .99);
            var clean = (OcrResult)filter.Invoke(null, [scene, defaultLowerRegion])!;
            if (clean.Lines.Count != 1 || clean.Text != expected.Text
                || !clean.Lines[0].Box.SequenceEqual(expected.Box) || clean.Confidence != expected.Confidence)
                throw new InvalidOperationException("scene text changed subtitle text, confidence or observation bounds");
        }

        var multiline = new OcrResult(true, true, "第一行字幕\n第二行字幕\n第8集", .99,
        [
            new OcrLine("第一行字幕", .99, [400, 150, 760, 200]),
            new OcrLine("第二行字幕", .99, [400, 205, 760, 255]),
            new OcrLine("第8集", .99, [790, 205, 940, 255]),
        ]);
        foreach (var region in new[] { defaultLowerRegion, upperRegion })
        {
            var clean = (OcrResult)filter.Invoke(null, [multiline, region])!;
            if (clean.Lines.Count != 3 || clean.Text != multiline.Text)
                throw new InvalidOperationException("scene filtering removed multiline/inline subtitle content");
        }

        // The "休书" letter case 21/09: a column of ≥4 scene-text rows above the dialogue
        // baseline, center-aligned -> drop the whole column. The 5th row hugging the baseline (not farAbove)
        // is evidence the column rule is what fires, not the old cleanup rule.
        var letterScene = new OcrResult(true, true,
            "我们当初结婚从头到尾只是一场交易\n为应付家里催婚我找你当挡箭牌\n婚前协议写得很清楚婚后同住不同楼\n你打地铺不发生夫妻关系\n三年期满后我会按协议足额支付你五百万报酬\n冷氏集团这三年越做越强", .97,
        [
            new OcrLine("我们当初结婚从头到尾只是一场交易", .97, [150, 18, 700, 46]),
            new OcrLine("为应付家里催婚我找你当挡箭牌", .97, [150, 60, 690, 88]),
            new OcrLine("婚前协议写得很清楚婚后同住不同楼", .96, [150, 102, 720, 130]),
            new OcrLine("你打地铺不发生夫妻关系", .95, [150, 144, 560, 172]),
            new OcrLine("三年期满后我会按协议足额支付你五百万报酬", .96, [150, 196, 780, 246]),
            new OcrLine("冷氏集团这三年越做越强", .999, [446, 254, 834, 292]),
        ]);
        var letterClean = (OcrResult)(filter.Invoke(null, [letterScene, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR scene-column filter returned null"));
        if (letterClean.Lines.Count != 1 || letterClean.Text != "冷氏集团这三年越做越强")
            throw new InvalidOperationException("stacked scene-text column leaked into subtitle cues");

        // 2 secondary rows above the baseline (a two-row dialogue line) are NOT a scene column -> keep.
        var twoRowCaption = new OcrResult(true, true, "你说你家里穷得揭不开锅\n我冷氏集团凭什么看上你", .98,
        [
            new OcrLine("你说你家里穷得揭不开锅", .98, [350, 208, 780, 246]),
            new OcrLine("我冷氏集团凭什么看上你", .98, [360, 254, 790, 292]),
        ]);
        var retainedRows = (OcrResult)(filter.Invoke(null, [twoRowCaption, defaultLowerRegion])
            ?? throw new InvalidOperationException("OCR scene-column filter returned null for two-row caption"));
        if (retainedRows.Lines.Count != 2 || retainedRows.Text != twoRowCaption.Text)
            throw new InvalidOperationException("two-row subtitle was dropped as a scene column");

        // A hand-picked upper ROI (region.Y < .5): the column rule does not apply — rows hugging
        // the baseline survive (the old farAbove cleanup removes distant ones, the column rule must not touch them).
        var upperClean = (OcrResult)(filter.Invoke(null, [letterScene, upperRegion])
            ?? throw new InvalidOperationException("OCR scene-column filter returned null for upper ROI"));
        if (!upperClean.Lines.Any(line => line.Text == "三年期满后我会按协议足额支付你五百万报酬")
            || !upperClean.Lines.Any(line => line.Text == "冷氏集团这三年越做越强"))
            throw new InvalidOperationException("scene-column rule must not apply to a user-selected upper ROI");
    }
}

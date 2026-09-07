using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G23 text ASS builder contract (user-approved Concat parity, 2026-09-08).
// Locks: deterministic ASS time formatting, inverted ASS color order,
// numpad alignment mapping from anchor fractions, fade tag generation per
// animation kind, content escaping, and style line composition.
public static class TimelineTextAssContract
{
    public static void Run()
    {
        var style = new TimelineTextStyle(
            "Arial", 0.08, Bold: true, Italic: false,
            FillHex: "#FFFFFF", OutlineHex: "#000000",
            OutlineWidthFraction: 0.004, ShadowHex: "#202020", ShadowOpacity: 0.5);
        var alignment = new TimelineTextAlignment(AnchorX: 0.5, AnchorY: 0.9, 0.5, 0.5);
        var item = new TimelineResolvedTextItem(
            TimelineTextItem.NewId(), "text-track", 1_000_000, 4_000_000,
            "Xin chào {G23}\ndòng hai", style, alignment,
            TimelineTextAnimation.FadeIn, 500_000);

        var dialogue = TimelineTextAssBuilder.BuildDialogue(item);
        Check(dialogue.StartsWith("Dialogue: 0,0:00:01.00,0:00:04.00,T2,,0,0,0,,", StringComparison.Ordinal),
            "dialogue header must use exact ASS timecodes and layer/format");
        Check(dialogue.Contains("\\an2", StringComparison.Ordinal),
            "anchor 0.5/0.9 must map to bottom-center numpad 2");
        Check(dialogue.Contains("\\b1", StringComparison.Ordinal),
            "bold style must emit \\b1");
        Check(dialogue.Contains("\\fad(500,0)", StringComparison.Ordinal),
            "fade-in animation must emit fad with duration ms");
        Check(dialogue.Contains("Xin chào \\{G23\\}\\Ndòng hai", StringComparison.Ordinal),
            "content must escape braces and convert newlines to \\N");

        var centerItem = item with
        {
            Alignment = TimelineTextAlignment.Center,
            Animation = TimelineTextAnimation.FadeInOut,
            AnimationDurationUs = 1_000_000,
        };
        var centerDialogue = TimelineTextAssBuilder.BuildDialogue(centerItem);
        Check(centerDialogue.Contains("\\an5", StringComparison.Ordinal),
            "center anchor must map to numpad 5");
        Check(centerDialogue.Contains("\\fad(500,500)", StringComparison.Ordinal),
            "fade-in-out must split duration evenly");

        var styleLine = TimelineTextAssBuilder.BuildStyleDefinition("T2-Default", style, alignment);
        Check(styleLine.StartsWith("Style: T2-Default,Arial,", StringComparison.Ordinal),
            "style line must start with name and font");
        Check(styleLine.Contains(",&H00202020,", StringComparison.Ordinal),
            "shadow color must convert to ASS inverted alpha-BGR order");
        Check(styleLine.Contains(",-1,0,0,0,100,100,", StringComparison.Ordinal),
            "bold -1 and italic 0 flags must appear in canonical order");

        // Deterministic: same input twice produces identical output.
        Check(dialogue == TimelineTextAssBuilder.BuildDialogue(item),
            "builder must be deterministic for identical input");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

namespace BiliSubStudio.Core.Ocr;

// E.5 — ROI STATIC ZONES (OCR Gates v1.3), THIS PHASE IS OBSERVE-ONLY (shadow).
//
// Idea: watermarks/logos show the SAME text at the SAME spot across dozens of consecutive samples
// (bilibili + original uploader name on the right, measured in B.4). Learn static regions by counting:
// a text line appearing at a nearly identical box for >= RepeatThreshold consecutive samples
// is a static region (SUSPICIOUS) — proposed for removal. Dialogue subtitles keep changing so
// they never reach the threshold (low false-positive safety).
// This phase: record the learned regions + count the odd lines that would be filtered (would-filter). No real
// filtering yet, no behavior change. Boxes are compared in OCR crop space (like the comparer).
public sealed class OcrRoiStaticZones
{
    public const int RepeatThreshold = 5;
    public const double BoxTolerancePx = 4;

    // Zone-learning gate (field 15/9: 396 zones over a bottom subtitle-strip ROI — all repeated
    // dialogue got learned as a wrong zone): only learn when the scan ROI covers the UPPER half of
    // the frame (where watermarks/logos live). For a bottom subtitle-strip ROI (Y>=0.5, e.g. the
    // current video Y=0.82) any repeated text is repeated dialogue, so learning is
    // turned off entirely: zones empty, would-filter 0, read behavior unchanged. Measured bboxes.json:
    // dialogue y<=652/720, watermark x>=980 — the Y rule is a necessary condition to avoid false learning.
    public static bool LearningEnabledFor(OcrRegion scanRegion) => scanRegion.Y < 0.5;

    public sealed record StaticZone(string Text, OcrBox Box, int Repeats);

    private readonly Dictionary<string, (OcrBox Box, int Streak)> _streaks = new(StringComparer.Ordinal);
    private readonly List<StaticZone> _zones = [];
    private readonly HashSet<string> _zoned = new(StringComparer.Ordinal);

    public IReadOnlyList<StaticZone> Zones => _zones;

    private static string Key(string text, OcrBox box)
    {
        var qx = (int)Math.Round(box.Left / BoxTolerancePx);
        var qy = (int)Math.Round(box.Top / BoxTolerancePx);
        var qw = (int)Math.Round((box.Right - box.Left) / BoxTolerancePx);
        var qh = (int)Math.Round((box.Bottom - box.Top) / BoxTolerancePx);
        return text + "|" + qx + "," + qy + "," + qw + "," + qh;
    }

    // Each sample: feed the filtered lines. Returns the number of odd lines that would be filtered (would-filter).
    public int Observe(IReadOnlyList<OcrLine> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Box is not { Length: 4 }) continue;
            var box = new OcrBox(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            if (box.Right <= box.Left || box.Bottom <= box.Top) continue;
            seen.Add(ObserveLine(line.Text ?? string.Empty, box));
        }
        // A streak only grows when the sample appears CONSECUTIVELY: a missing key resets it.
        var gone = new List<string>();
        foreach (var key in _streaks.Keys)
            if (!seen.Contains(key)) gone.Add(key);
        foreach (var key in gone) _streaks.Remove(key);
        // Count would-filter: lines falling inside an already-learned zone.
        var wouldFilter = 0;
        foreach (var line in lines)
        {
            if (line.Box is not { Length: 4 }) continue;
            var box = new OcrBox(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            foreach (var zone in _zones)
            {
                if (BoxInside(box, zone.Box)) { wouldFilter++; break; }
            }
        }
        return wouldFilter;
    }

    private string ObserveLine(string text, OcrBox box)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var key = Key(text, box);
        if (_zoned.Contains(key)) return key;
        if (_streaks.TryGetValue(key, out var entry))
            entry = (entry.Box, entry.Streak + 1);
        else
            entry = (box, 1);
        _streaks[key] = entry;
        if (entry.Streak >= RepeatThreshold)
        {
            _streaks.Remove(key);
            _zoned.Add(key);
            _zones.Add(new StaticZone(text, entry.Box, entry.Streak));
        }
        return key;
    }

    internal static bool BoxInside(OcrBox inner, OcrBox outer)
        => inner.Left >= outer.Left - BoxTolerancePx && inner.Top >= outer.Top - BoxTolerancePx
        && inner.Right <= outer.Right + BoxTolerancePx && inner.Bottom <= outer.Bottom + BoxTolerancePx;
}

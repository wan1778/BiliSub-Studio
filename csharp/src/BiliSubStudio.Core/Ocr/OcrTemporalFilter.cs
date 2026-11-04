namespace BiliSubStudio.Core.Ocr;

// Time filter (locked by user 17/09): a real hardcoded sub STAYS STILL across many frames
// (fixed position between frames), while text inside the video (book pages/background
// panned by the camera) MOVES continuously, never staying put.
// Judge each suspicious cue: capture 3 frames within the cue (start/middle/end + ±0.3s margins),
// re-OCR each frame, compare text + position:
// - ≥2 frames found but position drifts > 5% of frame -> MOVING -> DROP
//   (background text panning with the camera, not a sub).
// - 3/3 frames same text + same position -> STILL -> KEEP (real sub).
// - 2/3 frames same position (1 frame OCR garbled) -> KEEP (tolerates a flake).
// - <2 frames found -> insufficient evidence -> KEEP (no wrongful drop).
// Absolutely fail-open: any grab/OCR error -> KEEP, never lose real text by mistake.
public static class OcrTemporalFilter
{
    // Maximum position drift to consider "still": 4% of frame. OCR box jitter on
    // static text is only ~0.1-0.5% of frame so 4% is safe; camera panning through the window
    // ~2.3s (even slow text panning at 2%/s) drifts > 4%.
    public const double MoveDriftThreshold = 0.04;
    public const int ProbeFrames = 3;
    // Margin extending OUTSIDE the cue (±1s) to amplify drift: a 0.27s cue scanned only
    // inside itself gives a 0.16s window — drift never crosses the threshold (bug
    // fixed 17/09). Covering outside + matching text by exact CUE TEXT means no bleed into the
    // next sub (different text does not match).
    public const double EdgePadSeconds = 1.0;

    public sealed record TemporalVerdict(string Decision, string Reason);

    // Credit/title role (NOT dialogue). Real dialogue may mention it
    // ("导演说明天开拍") but never starts with a role name + separator.
    private static readonly string[] CreditRoles =
    [
        "立书人", "出品人", "联合出品", "出品", "制片人", "总制片", "监制", "总监制",
        "导演", "总导演", "副导演", "编剧", "总编剧", "原著", "原作",
        "领衔主演", "主演", "主持人", "主持", "嘉宾",
        "发行", "摄制", "制作", "承制", "鸣谢", "赞助", "特别感谢",
        "字幕", "翻译", "校对", "压制", "片源", "修复",
    ];

    // Title/credit (NOT dialogue) — drop immediately, no frame capture needed:
    // - a line STARTING with a credit role + separator ("立书人：冷暖暖")
    //   or a role name standing alone ("出品人");
    // - a line that is only a date ("2026年6月3日");
    // - a copyright line (©/copyright/版权/著作权).
    // Real dialogue ("导演说明天开拍") has no separator after the role name so it escapes.
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

    // Re-read fallback threshold (17/09): only suspicious cues (⚠️/low confidence) get a
    // re-read of neighbouring frames. Fix ONLY when the different read is CLEARLY better —
    // never "correct" correct text into wrong text.
    public const double FallbackTriggerConfidence = 0.85;
    public const double FixMinConfidence = 0.88;
    public const double FixMargin = 0.08;
    public const double FixMinSimilarity = 0.55;

    // Find the best read of a cue within one frame: the matching line (same sentence
    // read differently) with the highest confidence. Returns null when no line plausibly matches the sentence.
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
            // Identical text is not a "different read" — skip it.
            if (string.Equals(hay, needle, StringComparison.Ordinal)) continue;
            if (OcrTextCleanup.TextSimilarity(line.Text, cueText) < FixMinSimilarity) continue;
            if (best is null || line.Confidence > best.Value.Confidence)
                best = (line.Text, line.Confidence);
        }
        return best;
    }

    // Normalize text: keep only letters/digits (CJK counts as a letter), drop spaces+punct
    // ("立书人，冷暖暖" == "立书人：冷暖暖"). Empty after normalization means no match.
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    // Box centre (pixel coords) normalized by frame size.
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

    // Find the line matching the cue text (contains either direction after normalization). No match -> null.
    // SHORT cue (≤2 chars after normalization, e.g. "哼"/"滚"/"武夫"): FORBID "contains" matching —
    // long background text that happens to contain those chars always wins longest-match, then the position
    // of a background line (panning with the camera) is mis-measured -> wrongful drop of a still sub (the real "哼"/"滚" cues
    // in .exclude.json). This branch only accepts EXACT match, ties take the
    // highest confidence. The long-cue path (>2 chars) keeps the per-line logic below.
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
            // Prefer the longest matching line (full sentence, not a truncated line).
            if (hay.Length > bestLength)
            {
                best = line;
                bestLength = hay.Length;
            }
        }
        return best;
    }

    // Decide from the OCR results of the probe frames (in probe order).
    // frames[i] = OCR result of frame i; a missing frame -> null.
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
        // Maximum drift among the found frame pairs.
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

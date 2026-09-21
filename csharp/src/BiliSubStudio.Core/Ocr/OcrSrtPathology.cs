namespace BiliSubStudio.Core.Ocr;

// E.6 — SRT PATHOLOGY GATE (OCR Gates v1.3), sua 2026-09-14: REPAIR-FIRST.
//
// Bai hoc vu cue 2703 (overlap 0.1s chan ca file 2700 cau): chan file vi loi
// nho duoc sua la CUOP file cua user (checkpoint da xoa, khong con duong lui).
// Quy tac moi: sua duoc thi SUA + bao to, chi dung khi khong con gi cuu duoc.
// - TRIM + warn: overlap -> prev.End = next.Start (chuan subtitle editor).
//   Neu trim lam prev chet (End<=Start) thi drop prev + liet ke.
// - DROP + warn (liet ke tung cue): text rong, End<=Start, gio am vo ly,
//   manh vun <0.1s, surrogate le -> thay bang U+FFFD (giu cue).
// - SORT + warn: Start khong don dieu thi sap lai (player thich sort san).
// - WARN: list rong, duration ngoai bien, sa mac, dup/gan-dup cham.
// - FAIL (dung xuat, nem): chi khi sua xong KHONG CON cue nao ma dau vao
//   khong rong (toan rac) — truong hop nay ghi file trang la noi doi.
// Thuan (pure), test truc tiep. Nhan text DA normalize (giong export).
public sealed record RepairedCues(
    bool Passed,
    IReadOnlyList<(double Start, double End, string Text)> Cues,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings);

public static class OcrSrtPathology
{
    private const double Epsilon = 0.001;
    private const double MinDuration = 0.1;
    private const double WarnShortDuration = 0.2;
    private const double WarnLongDuration = 30;
    private const double DesertGapSeconds = 300;
    private const int MaxListed = 5;
    private const double NearDupSimilarity = 0.82;
    private const double NearDupGapSeconds = 0.25;

    public static RepairedCues Repair(IReadOnlyList<(double Start, double End, string Text)> cues)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        if (cues.Count == 0)
        {
            warnings.Add("empty: khong co cue nao (video khong sub la hop le, van ghi file).");
            return new RepairedCues(true, [], failures, warnings);
        }
        // 1. Loc rac + sua surrogate (giu thu tu goc de bao so cue dung).
        var kept = new List<(double Start, double End, string Text, int Index)>();
        var dropped = 0;
        string? firstDrop = null;
        for (var i = 0; i < cues.Count; i++)
        {
            var (start, end, text) = cues[i];
            text ??= string.Empty;
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0)
            {
                dropped++;
                firstDrop ??= $"cue {i + 1} [{start:0.000}-{end:0.000}]: gio vo ly, bo qua.";
                continue;
            }
            if (end <= start)
            {
                dropped++;
                firstDrop ??= $"cue {i + 1} [{start:0.000}-{end:0.000}]: End<=Start, bo qua.";
                continue;
            }
            if (end - start < MinDuration)
            {
                dropped++;
                firstDrop ??= $"cue {i + 1} [{start:0.000}-{end:0.000}]: manh vun {end - start:0.000}s, bo qua.";
                continue;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                dropped++;
                firstDrop ??= $"cue {i + 1} [{start:0.000}-{end:0.000}]: text rong, bo qua.";
                continue;
            }
            text = ReplaceLoneSurrogates(text, out var replacedSurrogate);
            if (replacedSurrogate)
                warnings.Add($"cue {i + 1} [{start:0.000}-{end:0.000}]: surrogate le da thay bang U+FFFD (van ghi).");
            kept.Add((start, end, text, i));
        }
        if (dropped > 0)
            warnings.Add($"{dropped} cue rac da bo (vd: {firstDrop}) van ghi tiep.");
        if (kept.Count == 0)
        {
            failures.Add($"toan bo {cues.Count} cue deu rac ({dropped} da bo), khong con gi de ghi.");
            return new RepairedCues(false, [], failures, warnings);
        }
        // 2. Sort theo Start (neu dau vao lon xon thi sap + bao).
        var reordered = false;
        for (var i = 1; i < kept.Count; i++)
        {
            if (kept[i].Start < kept[i - 1].Start - Epsilon) { reordered = true; break; }
        }
        var ordered = reordered
            ? kept.OrderBy(c => c.Start).ThenBy(c => c.End).ToList()
            : kept;
        if (reordered) warnings.Add("cue khong don dieu: da sap lai theo Start (van ghi).");
        // 3. Trim overlap + canh bao gan-dup/sa mac/duration.
        var output = new List<(double Start, double End, string Text)>();
        var trimmed = 0;
        foreach (var cue in ordered)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                if (cue.Start < prev.End - Epsilon)
                {
                    trimmed++;
                    var fixedEnd = cue.Start;
                    if (fixedEnd <= prev.Start)
                    {
                        output.RemoveAt(output.Count - 1);
                        warnings.Add($"cue [{prev.Start:0.000}-{prev.End:0.000}] '{Truncate(prev.Text)}' chong han: bo (giu cue sau).");
                    }
                    else
                    {
                        output[^1] = (prev.Start, fixedEnd, prev.Text);
                    }
                }
                else if (cue.Start - prev.End > DesertGapSeconds)
                {
                    warnings.Add($"sa mac {cue.Start - prev.End:0}s khong cue truoc cue [{cue.Start:0.000}] (kiem tra doan chet).");
                }
                else if (cue.Start - prev.End <= NearDupGapSeconds
                    && !string.Equals(prev.Text.Trim(), cue.Text.Trim(), StringComparison.Ordinal)
                    && Similarity(prev.Text.Trim(), cue.Text.Trim()) >= NearDupSimilarity)
                {
                    warnings.Add($"cue [{cue.Start:0.000}-{cue.End:0.000}] gan-dup ke ({Similarity(prev.Text.Trim(), cue.Text.Trim()):0.00}): flicker-split sot? kiem tra needreview.");
                }
                else if (cue.Start <= prev.End + Epsilon
                    && string.Equals(prev.Text.Trim(), cue.Text.Trim(), StringComparison.Ordinal))
                {
                    warnings.Add($"cue [{cue.Start:0.000}-{cue.End:0.000}] dup ke giong het: flicker-split sot? kiem tra needreview.");
                }
            }
            var duration = cue.End - cue.Start;
            if (duration > WarnLongDuration)
                warnings.Add($"cue [{cue.Start:0.000}-{cue.End:0.000}] dai {duration:0.0}s ngoai bien thuong (van ghi).");
            else if (duration < WarnShortDuration)
                warnings.Add($"cue [{cue.Start:0.000}-{cue.End:0.000}] ngan {duration:0.000}s (van ghi).");
            output.Add((cue.Start, cue.End, cue.Text));
        }
        if (trimmed > 0)
            warnings.Add($"{trimmed} cho overlap da cat gon (prev.End = next.Start, chuan subtitle editor).");
        return new RepairedCues(true, output, failures, warnings);
    }

// Backward-compat cho test/cho goi cu: tra ve dang cu (Passed/Failures/Warnings).
// Du lieu da repair (trim/drop/sort) KHONG tra qua day — dung Repair() lay Cues.
public sealed record PathologyReport(bool Passed, IReadOnlyList<string> Failures, IReadOnlyList<string> Warnings);
    public static PathologyReport Validate(IReadOnlyList<(double Start, double End, string Text)> cues)
    {
        var repaired = Repair(cues);
        return new PathologyReport(repaired.Passed, repaired.Failures, repaired.Warnings);
    }

    private static string Truncate(string text) => text.Length <= 24 ? text : text[..24] + "…";

    private static string ReplaceLoneSurrogates(string text, out bool replaced)
    {
        replaced = false;
        var output = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) { output.Append(text[i]); continue; }
            var high = char.IsHighSurrogate(text[i]);
            if (high && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                output.Append(text[i]);
                output.Append(text[i + 1]);
                i++;
            }
            else
            {
                output.Append('�');
                replaced = true;
            }
        }
        return output.ToString();
    }

    // Levenshtein chuan hoa theo do dai (giong ham Similarity cua scanner,
    // de private rieng de khong mo rong API): 1.0 giong het, 0 khac han.
    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        var leftRunes = left.EnumerateRunes().Select(x => x.Value).ToArray();
        var rightRunes = right.EnumerateRunes().Select(x => x.Value).ToArray();
        if (leftRunes.Length == 0 || rightRunes.Length == 0) return 0;
        var previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        for (var i = 1; i <= leftRunes.Length; i++)
        {
            var current = new int[rightRunes.Length + 1];
            current[0] = i;
            for (var j = 1; j <= rightRunes.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (leftRunes[i - 1] == rightRunes[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(leftRunes.Length, rightRunes.Length);
    }
}

// =====================================================================
// PIPELINE XUẤT/HIỂN THỊ DUY NHẤT (18/09): live lúc quét xong và file SRT
// phải giống y đúc — cùng 1 hàm Build cho cả 2 đường (export + live).
// Thứ tự bước GIỮ Y HỆT export cũ: loại trừ -> đắp fix -> normalize ->
// pathology. Khác duy nhất: cue rớt normalize CÓ ĐẾM + mẫu (trước đây export
// vứt câm còn live vẫn hiện). Cờ NeedsReview được mang qua trim/sort để live
// hiện đúng badge như SRT.
// (Nằm chung file vì là chặng cuối đường xuất SRT; tách file riêng khi được
// phép tạo file mới.)
public sealed record OcrExportRow(double Start, double End, string Text, bool NeedsReview);

public sealed record OcrExportCueList(
    IReadOnlyList<OcrExportRow> Rows,
    int Excluded, IReadOnlyList<string> ExcludedSamples,
    int Fixed, IReadOnlyList<string> FixedSamples,
    int NormalizeDropped, IReadOnlyList<string> NormalizeDroppedSamples,
    bool Passed, IReadOnlyList<string> Failures, IReadOnlyList<string> Warnings);

public static class OcrExportPipeline
{
    public static OcrExportCueList Build(
        IEnumerable<OcrCue> cues,
        IReadOnlyList<OcrCueExclusion>? exclusions,
        IReadOnlyList<OcrCueFix>? fixes)
    {
        var filtered = OcrExportExclusions.Apply(cues, exclusions);
        var patched = OcrExportFixes.Apply(filtered.Kept, fixes);
        var normalized = new List<(double Start, double End, string Text, bool NeedsReview)>();
        var normDropped = 0;
        var normSamples = new List<string>();
        foreach (var cue in patched.Cues)
        {
            if (cue is null) continue;
            if (ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text))
                normalized.Add((cue.Start, cue.End, text, cue.NeedsReview));
            else
            {
                normDropped++;
                if (normSamples.Count < 5)
                    normSamples.Add($"{cue.Start:0.000}s “{Truncate(cue.Text, 24)}”");
            }
        }
        // Hậu-fix gộp trùng (21/09, báo cáo lặp của user): fix của judge được áp
        // SAU reconcile — một nhát fix có thể làm 2 câu kề đồng nhất, hoặc
        // chuỗi đắp về cùng một chữ. Gộp cặp kề chữ y hệt khe ≤0.5s ngay đây để
        // dạng lặp này không bao giờ rớt vào SRT. Chạy TRƯỚC Repair (trim
        // overlap vẫn chuẩn với phần còn lại).
        var collapsedProbe = CollapseIdenticalAdjacent(normalized);
        var repaired = OcrSrtPathology.Repair(
            collapsedProbe.Select(item => (item.Start, item.End, item.Text)).ToList());
        // Mang cờ review qua trim/sort: Start+Text không đổi qua repair (trim
        // chỉ sửa End, sort ổn định, OrderBy ổn định) nên hàng đợi theo key là
        // khớp chính xác từng dòng. Ngoại lệ duy nhất: cue dính surrogate bị
        // thay U+FFFD đổi Text -> lookup hụt -> mất badge (cosmetic, hiếm).
        var reviewByKey = new Dictionary<(double Start, string Text), Queue<bool>>();
        foreach (var item in collapsedProbe)
        {
            var key = (item.Start, item.Text);
            if (!reviewByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<bool>();
                reviewByKey[key] = queue;
            }
            queue.Enqueue(item.NeedsReview);
        }
        var rows = new List<OcrExportRow>(repaired.Cues.Count);
        foreach (var (start, end, text) in repaired.Cues)
        {
            var review = false;
            if (reviewByKey.TryGetValue((start, text), out var queue) && queue.Count > 0)
                review = queue.Dequeue();
            rows.Add(new OcrExportRow(start, end, text, review));
        }
        return new OcrExportCueList(rows,
            filtered.Dropped, filtered.DroppedSamples,
            patched.Fixed, patched.FixedSamples,
            normDropped, normSamples,
            repaired.Passed, repaired.Failures, repaired.Warnings);
    }

    // Gộp cặp KỀ chữ y hệt (post-normalize, ordinal) khe ≤0,5s. Chỉ đụng khi
    // hai hàng cạnh nhau sau loại-trừ/fix/normalize có cùng Text — bất biến với
    // mọi câu khác chữ. NeedsReview gộp theo AND (cả hai cùng nghi vấn mới giữ
    // cờ cho câu đã gộp).
    internal static IReadOnlyList<(double Start, double End, string Text, bool NeedsReview)>
        CollapseIdenticalAdjacent(IReadOnlyList<(double Start, double End, string Text, bool NeedsReview)> normalized)
    {
        if (normalized.Count < 2) return normalized;
        var output = new List<(double Start, double End, string Text, bool NeedsReview)>(normalized.Count);
        foreach (var item in normalized)
        {
            if (output.Count > 0)
            {
                var head = output[^1];
                if (string.Equals(head.Text, item.Text, StringComparison.Ordinal)
                    && item.Start >= head.Start
                    && item.Start - head.End <= 0.5)
                {
                    output[^1] = (head.Start, Math.Max(head.End, item.End), head.Text,
                        head.NeedsReview && item.NeedsReview);
                    continue;
                }
            }
            output.Add(item);
        }
        return output;
    }

    // Format giờ SRT duy nhất cho cả export lẫn live (làm tròn ms — TimeSpan
    // .ToString("fff") cắt ngọn nên live cũ lệch 1ms ở biên).
    public static string FormatSrtTime(double seconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static string Truncate(string? text, int maxRunes)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var runes = text.EnumerateRunes().Take(maxRunes).ToArray();
        var kept = string.Concat(runes);
        return kept.Length < text.Length ? kept + "…" : kept;
    }
}

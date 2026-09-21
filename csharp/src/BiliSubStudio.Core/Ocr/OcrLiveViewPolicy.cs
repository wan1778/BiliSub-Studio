namespace BiliSubStudio.Core.Ocr;

// Pure policy cho live cue list UI (không dependency WinUI — testable):
// - auto-follow tail khi viewport đang ở cuối,
// - incremental render: chỉ update rows từ first-changed index (tail merge/append),
//   prefix giữ nguyên nên ListView virtualization không realize lại hàng nghìn containers.
public static class OcrLiveViewPolicy
{
    // Ngưỡng "gần cuối" theo pixel (~2 rows 32px). Viewport ngắn hơn ngưỡng vẫn follow.
    public const double FollowThresholdPx = 64.0;

    public static bool IsNearEnd(double verticalOffset, double viewportHeight, double scrollableHeight, double thresholdPx = FollowThresholdPx)
    {
        if (viewportHeight <= 0) return true;
        if (scrollableHeight <= 0) return true;
        return verticalOffset + viewportHeight >= scrollableHeight - thresholdPx;
    }

    // Số cue đầu IDENTICAL (Start/End/Text ordinal) giữa 2 lần render.
    // Cố ý KHÔNG dùng record equality: confidence/support/review thay đổi không được
    // coi là row đổi (tránh rebuild vô ích). Text khác HOẶC Start/End khác => đổi.
    public static int CommonCuePrefixLength(IReadOnlyList<OcrCue> oldCues, IReadOnlyList<OcrCue> newCues)
    {
        var count = Math.Min(oldCues.Count, newCues.Count);
        var index = 0;
        while (index < count)
        {
            var oldCue = oldCues[index];
            var newCue = newCues[index];
            if (oldCue.Start != newCue.Start || oldCue.End != newCue.End
                || !string.Equals(oldCue.Text, newCue.Text, StringComparison.Ordinal))
                break;
            index++;
        }
        return index;
    }

    // Overload cho dòng đã qua pipeline chung (OcrExportRow: live-quét-xong =
    // SRT): cùng semantics Start/End/Text, tên khác để không ambiguous với
    // overload OcrCue ở call-site collection-expression.
    public static int CommonRowPrefixLength(IReadOnlyList<OcrExportRow> oldRows, IReadOnlyList<OcrExportRow> newRows)
    {
        var count = Math.Min(oldRows.Count, newRows.Count);
        var index = 0;
        while (index < count)
        {
            var oldRow = oldRows[index];
            var newRow = newRows[index];
            if (oldRow.Start != newRow.Start || oldRow.End != newRow.End
                || !string.Equals(oldRow.Text, newRow.Text, StringComparison.Ordinal))
                break;
            index++;
        }
        return index;
    }
}

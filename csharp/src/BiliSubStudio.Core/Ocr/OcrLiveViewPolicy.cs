namespace BiliSubStudio.Core.Ocr;

// Pure policy for the live cue list UI (no WinUI dependency — testable):
// - auto-follow the tail while the viewport sits at the end,
// - incremental render: only update rows from the first-changed index (tail merge/append),
//   the prefix is unchanged so ListView virtualization does not re-realize thousands of containers.
public static class OcrLiveViewPolicy
{
    // "Near the end" threshold in pixels (~2 rows of 32px). A viewport shorter than the threshold still follows.
    public const double FollowThresholdPx = 64.0;

    public static bool IsNearEnd(double verticalOffset, double viewportHeight, double scrollableHeight, double thresholdPx = FollowThresholdPx)
    {
        if (viewportHeight <= 0) return true;
        if (scrollableHeight <= 0) return true;
        return verticalOffset + viewportHeight >= scrollableHeight - thresholdPx;
    }

    // Number of leading cues IDENTICAL (Start/End/Text ordinal) between two renders.
    // Deliberately NOT record equality: changes to confidence/support/review must not
    // count as a changed row (avoids useless rebuilds). Different Text OR different Start/End => changed.
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

    // Overload for rows already through the shared pipeline (OcrExportRow: live-scan-done =
    // SRT): same Start/End/Text semantics, different name so it is not ambiguous with
    // the OcrCue overload at the collection-expression call-site.
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

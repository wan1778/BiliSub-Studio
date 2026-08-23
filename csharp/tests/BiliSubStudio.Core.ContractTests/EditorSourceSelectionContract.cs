using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorSourceSelectionContract
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-source-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Path.Combine(root, "video.mp4");
            File.WriteAllBytes(video, [0, 1, 2, 3]);
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            var alias = Path.Combine(nested, "..", "video.mp4");

            var normalized = EditorSourceSelection.NormalizeCandidatePath(alias);
            if (!string.Equals(normalized, Path.GetFullPath(video), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("source normalization changed the selected file");
            if (!EditorSourceSelection.IsSameSource(video, alias))
                throw new InvalidOperationException("same video was not recognized as the same source");
            if (EditorSourceSelection.IsSameSource(null, video))
                throw new InvalidOperationException("missing current source cannot equal a candidate");

            var missingRejected = false;
            try { EditorSourceSelection.NormalizeCandidatePath(Path.Combine(root, "missing.mp4")); }
            catch (FileNotFoundException) { missingRejected = true; }
            if (!missingRejected) throw new InvalidOperationException("missing video path was accepted");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

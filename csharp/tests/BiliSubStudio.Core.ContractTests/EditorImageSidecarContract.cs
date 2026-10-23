using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Field bug 23/10: editor image/logo sidecar never saved. SaveImageSidecarAsync
// moved the temp file while its own FileStream (FileShare.None) was still open,
// so every write died with ERROR_SHARING_VIOLATION and the page crashed on the
// first drag release. The store must close the handle before the move, leave no
// temp files behind, and self-heal leftovers from older crashed writes.
internal static class EditorImageSidecarContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-image-sidecar-" + Guid.NewGuid().ToString("N"));
        var projects = Path.Combine(root, "Projects");
        Directory.CreateDirectory(projects);
        try
        {
            var png = Path.Combine(root, "aaaa.png");
            await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var path = EditorImageSidecarStore.PathFor(projects, "project-1");

            var first = new EditorImageSidecarEntry(png, .025, .025, .18, .18, 1, 1920, 1080);
            await EditorImageSidecarStore.WriteAsync(path, [first]);
            Require(File.Exists(path), "sidecar must exist after the first save");
            Require(Directory.GetFiles(projects, "*.tmp-*").Length == 0, "save must not leave temp files");
            var read = await EditorImageSidecarStore.ReadAsync(path);
            Require(read.Count == 1 && read[0] == first, "sidecar must round-trip every field exactly");

            var second = first with { X = .5, Opacity = .4 };
            await EditorImageSidecarStore.WriteAsync(path, [first, second]);
            read = await EditorImageSidecarStore.ReadAsync(path);
            Require(read.Count == 2 && read[1].X == .5 && Math.Abs(read[1].Opacity - .4) < 1e-9,
                "second save must replace the whole sidecar");
            Require(Directory.GetFiles(projects, "*.tmp-*").Length == 0, "overwrite must not leave temp files");

            await EditorImageSidecarStore.WriteAsync(path, []);
            Require(!File.Exists(path), "empty list must delete the sidecar");

            var orphan = path + ".tmp-114b2c5b55db4fa3808b7d11adff7da8";
            await File.WriteAllTextAsync(orphan, "stale");
            Require(EditorImageSidecarStore.CleanupOrphanTemps(path) == 1, "orphan temp must be removed");
            Require(!File.Exists(orphan), "orphan temp must not survive cleanup");
            Require(!File.Exists(path), "cleanup must not create a sidecar");

            var missing = await EditorImageSidecarStore.ReadAsync(path);
            Require(missing.Count == 0, "missing sidecar must read as an empty list");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

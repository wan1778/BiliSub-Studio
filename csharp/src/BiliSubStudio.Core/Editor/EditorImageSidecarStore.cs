using System.Text.Json;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorImageSidecarEntry(
    string Path,
    double X,
    double Y,
    double Width,
    double Height,
    double Opacity,
    uint PixelWidth,
    uint PixelHeight);

// Ảnh/logo của project editor nằm trong sidecar riêng cạnh project JSON.
// Ghi phải nguyên tử: tạo file tạm, đóng handle rồi mới Move (chia sẻ handle
// đang mở làm MoveFileEx trả ERROR_SHARING_VIOLATION 0x20 — đúng lỗi 23/10).
public static class EditorImageSidecarStore
{
    private const int MoveAttempts = 5;
    private const int MoveRetryDelayMilliseconds = 60;

    public static string PathFor(string projectsDirectory, string projectId) =>
        System.IO.Path.Combine(projectsDirectory, projectId + ".images.json");

    public static async Task WriteAsync(string path, IReadOnlyList<EditorImageSidecarEntry> images)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            Delete(path);
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, images, cancellationToken: CancellationToken.None);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetry(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static async Task<IReadOnlyList<EditorImageSidecarEntry>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return [];

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<List<EditorImageSidecarEntry>>(stream, cancellationToken: cancellationToken);
        return loaded ?? [];
    }

    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        TryDelete(path);
        CleanupOrphanTemps(path);
    }

    // Dọn file tạm còn sót từ lần ghi bị hủy/treo trước đó.
    public static int CleanupOrphanTemps(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

        var removed = 0;
        foreach (var orphan in Directory.EnumerateFiles(directory, System.IO.Path.GetFileName(path) + ".tmp-*"))
        {
            try
            {
                File.Delete(orphan);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                // Defender/indexer có thể giữ handle trong vài chục ms.
                Thread.Sleep(MoveRetryDelayMilliseconds * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

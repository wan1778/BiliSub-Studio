using System.Security.Cryptography;
using System.Text;

namespace BiliSubStudio.Core.Ocr;

// IO for the sidecar: 28/09 (user forbade files beside the video) ALWAYS write to app Data;
// AlongsidePath is only left for READING legacy files (old leftover files still give cache hits).
// Every error (permissions, disk, corrupt) is swallowed silently + continue WITHOUT the sidecar — a scan never
// fails because of a cache file. Cancellation still throws to respect the caller.
public sealed class OcrSidecarStore
{
    private readonly string _appDataDir;
    private int _fallbackMode = 1;

    public OcrSidecarStore(string appDataDir) => _appDataDir = appDataDir;

    public string AlongsidePath(string videoPath)
    {
        var full = Path.GetFullPath(videoPath.Trim());
        return Path.Combine(Path.GetDirectoryName(full) ?? full, OcrSidecar.FileNameFor(Path.GetFileName(full)));
    }

    public string FallbackPath(OcrSidecar.FileIdentity identity)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity.Path + "|" + identity.Size + "|" + identity.ModUnixNano)));
        return Path.Combine(_appDataDir, "Sidecars", digest[..16] + ".ocr-sidecar.json");
    }

    public string ResolvePath(string videoPath)
        => Volatile.Read(ref _fallbackMode) == 0 ? AlongsidePath(videoPath) : FallbackForUnknown(videoPath);

    private string FallbackForUnknown(string videoPath)
    {
        try
        {
            var identity = OcrSidecar.FileIdentity.FromFile(videoPath);
            if (identity is not null) return FallbackPath(identity);
        }
        catch { }
        return Path.Combine(_appDataDir, "Sidecars", "unknown.ocr-sidecar.json");
    }

    // Read: beside the video first, fallback after. Wrong stamp/version/corrupt => null (recompute via the old path).
    public async Task<OcrSidecar.SidecarDocument?> LoadAsync(string videoPath, OcrSidecar.FileIdentity current, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { AlongsidePath(videoPath), FallbackPath(current) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrSidecar.SidecarDocument? document = null;
            try
            {
                if (!File.Exists(path)) continue;
                document = OcrSidecar.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (document is null || !document.Identity.Matches(current)) continue;
            return document;
        }
        return null;
    }

    // Atomic write (tmp + move). If writing beside the video fails (permissions/disk) => enable fallback + retry once.
    // If all fail => swallow silently. Never throws except on cancel.
    public async Task SaveAsync(string videoPath, OcrSidecar.SidecarDocument document, CancellationToken cancellationToken)
    {
        var payload = OcrSidecar.Serialize(document);
        var primary = Volatile.Read(ref _fallbackMode) == 0 ? TryPath(() => AlongsidePath(videoPath)) : null;
        if (primary is not null && await TryWriteAsync(primary, payload, cancellationToken)) return;
        if (Volatile.Read(ref _fallbackMode) == 0) Interlocked.Exchange(ref _fallbackMode, 1);
        var fallback = TryPath(() => FallbackPath(document.Identity));
        if (fallback is not null) await TryWriteAsync(fallback, payload, cancellationToken);
    }

    private static string? TryPath(Func<string> resolve)
    {
        try { return resolve(); }
        catch { return null; }
    }

    private static async Task<bool> TryWriteAsync(string path, string payload, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, payload, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}

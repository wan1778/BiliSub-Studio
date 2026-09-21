using System.Diagnostics;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Ocr;

// Reaps OCR/FFmpeg child processes orphaned by a sudden close or crash of a
// previous app instance. Without this, stale Python Paddle workers keep
// holding GPU VRAM and stale FFmpeg lanes keep decoding, so a resume after
// restart cannot acquire its worker pool. Runs only at app startup, when the
// current instance owns no workers yet, so every match is an orphan by
// construction. Never throws.
internal sealed record OrphanProcessInfo(int Id, string ExePath, DateTime StartTimeUtc);

internal static class OcrOrphanReaper
{
    internal static string BootstrapRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? string.Empty : Path.Combine(local, "BiliSub Studio", "OCRBootstrap");
    }

    internal static bool IsOwnedOcrExecutable(string? exePath, string toolsDir, string bootstrapDir)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath.Trim());
        if (!name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string fullExe;
        try { fullExe = Path.GetFullPath(exePath); }
        catch { return false; }
        return IsUnder(fullExe, toolsDir) || IsUnder(fullExe, bootstrapDir);
    }

    private static bool IsUnder(string file, string? dir)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(dir)) return false;
        string fullDir;
        try { fullDir = Path.GetFullPath(dir); }
        catch { return false; }
        fullDir = fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return file.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }

    internal static int Reap(AppPaths paths, Func<IReadOnlyList<OrphanProcessInfo>>? lister = null)
    {
        try
        {
            var tools = Path.GetFullPath(paths.Tools);
            var boot = BootstrapRoot();
            DateTime appStart;
            try { appStart = Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch { appStart = DateTime.UtcNow; }
            var mine = Environment.ProcessId;
            var candidates = lister is not null ? lister() : ListSystem();
            var killed = 0;
            foreach (var candidate in candidates)
            {
                if (candidate is null || candidate.Id == mine) continue;
                // Anything spawned at/after our own start belongs to this
                // session, never to a dead one.
                if (candidate.StartTimeUtc >= appStart) continue;
                if (!IsOwnedOcrExecutable(candidate.ExePath, tools, boot)) continue;
                try
                {
                    using var process = Process.GetProcessById(candidate.Id);
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { }
            }
            return killed;
        }
        catch { return 0; }
    }

    private static IReadOnlyList<OrphanProcessInfo> ListSystem()
    {
        var output = new List<OrphanProcessInfo>();
        foreach (var name in new[] { "python", "pythonw", "ffmpeg" })
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in found)
            {
                using (process)
                {
                    try
                    {
                        var exe = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(exe)) continue;
                        output.Add(new OrphanProcessInfo(process.Id, exe, process.StartTime.ToUniversalTime()));
                    }
                    catch { }
                }
            }
        }
        return output;
    }
}

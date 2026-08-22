using System.IO.Compression;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Tools;

public sealed record ToolStatus(bool YtDlpReady, bool FfmpegReady, bool FfprobeReady);

public sealed class ToolManager
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolManager(AppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    public string YtDlpPath => Path.Combine(_paths.Tools, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(_paths.Tools, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(_paths.Tools, "ffprobe.exe");

    public ToolStatus Status => new(IsOwnedTool(YtDlpPath), IsOwnedTool(FfmpegPath), IsOwnedTool(FfprobePath));

    public async Task<string> EnsureYtDlpAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsOwnedTool(YtDlpPath))
            {
                Directory.CreateDirectory(_paths.Tools);
                await DownloadAtomicAsync(YtDlpUrl, YtDlpPath, cancellationToken);
            }
            return YtDlpPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string> EnsureFfmpegAsync(CancellationToken cancellationToken) => EnsureFfmpegBundleAsync(FfmpegPath, cancellationToken);

    public Task<string> EnsureFfprobeAsync(CancellationToken cancellationToken) => EnsureFfmpegBundleAsync(FfprobePath, cancellationToken);

    private async Task<string> EnsureFfmpegBundleAsync(string requested, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsOwnedTool(requested))
            {
                return requested;
            }
            Directory.CreateDirectory(_paths.Tools);
            var archive = Path.Combine(_paths.Tools, "ffmpeg.zip");
            await DownloadAtomicAsync(FfmpegUrl, archive, cancellationToken);
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var wanted in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.FirstOrDefault(x =>
                        string.Equals(Path.GetFileName(x.FullName), wanted, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        throw new InvalidDataException($"Gói FFmpeg thiếu {wanted}.");
                    }
                    var destination = Path.Combine(_paths.Tools, wanted);
                    var temporary = destination + ".tmp";
                    try
                    {
                        await using (var source = entry.Open())
                        await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await source.CopyToAsync(target, cancellationToken);
                            await target.FlushAsync(cancellationToken);
                        }
                        File.Move(temporary, destination, overwrite: true);
                    }
                    finally
                    {
                        TryDelete(temporary);
                    }
                }
            }
            finally
            {
                TryDelete(archive);
                TryDelete(archive + ".tmp");
            }
            if (!IsOwnedTool(requested))
            {
                throw new FileNotFoundException("Không tìm thấy công cụ sau giải nén.", requested);
            }
            return requested;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAtomicAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp";
        TryDelete(temporary);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("BiliSubStudio/4-CSharp");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private bool IsOwnedTool(string candidate)
    {
        try
        {
            if (!File.Exists(candidate) || new FileInfo(candidate).Length <= 0)
            {
                return false;
            }
            var root = Path.GetFullPath(_paths.Tools).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(candidate);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(new FileInfo(path).LinkTarget, null, StringComparison.Ordinal)) return false;
            using var executable = File.OpenRead(path);
            return executable.ReadByte() == 'M' && executable.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

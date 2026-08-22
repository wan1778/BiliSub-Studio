using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Video;

public sealed class VideoDownloadService
{
    private readonly AppPaths _paths;
    private readonly YtDlpResolver _resolver;
    private readonly RangeDownloader _downloader;
    private readonly ToolManager _tools;
    private readonly ProcessRunner _processes;

    public VideoDownloadService(
        AppPaths paths,
        YtDlpResolver resolver,
        RangeDownloader downloader,
        ToolManager tools,
        ProcessRunner processes)
    {
        _paths = paths;
        _resolver = resolver;
        _downloader = downloader;
        _tools = tools;
        _processes = processes;
    }

    // 1/4/8 is the last field-proven Bilibili budget from the legacy 3.9.2 line.
    // 1/8/16 is still useful in synthetic tests, but real Bilibili CDNs have shown
    // early short bodies under the larger worker counts.
    public static int SpeedConnections(string speed) => (speed ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "stable" => 1,
        "turbo" => 8,
        _ => 4,
    };

    public async Task<VideoDownloadResult> RunAsync(AppJob job, VideoDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(job);
        var cancellationToken = job.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("URL rỗng.", nameof(request));
        var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? _paths.DefaultDownloads : Path.GetFullPath(request.OutputDirectory.Trim());
        Directory.CreateDirectory(outputDirectory);
        await _tools.EnsureYtDlpAsync(cancellationToken);
        var ffmpeg = await _tools.EnsureFfmpegAsync(cancellationToken);

        job.Set("resolving", 0, "Đang lấy stream Bilibili...");
        var resolveRequest = new VideoResolveRequest(request.Url, request.Quality, request.Mode, request.Container, request.CookieFile);
        var selection = await _resolver.ResolveAsync(resolveRequest, cancellationToken);
        job.Log($"Đã resolve: {selection.Title}");
        if (selection.Video is not null) job.Log($"Video format {selection.Video.FormatId} · {selection.Video.Height}p · {selection.Video.Size} bytes");
        if (selection.Audio is not null) job.Log($"Audio format {selection.Audio.FormatId} · {selection.Audio.Size} bytes");

        var estimatedStreams = (selection.Video?.Size ?? 0L) + (selection.Audio?.Size ?? 0L);
        if (estimatedStreams > 0)
        {
            const long reserve = 512L * 1024 * 1024;
            var required = estimatedStreams <= (long.MaxValue - reserve) / 2
                ? estimatedStreams * 2 + reserve
                : long.MaxValue;
            var root = Path.GetPathRoot(outputDirectory);
            if (!string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < required)
                    {
                        throw new IOException($"Ổ lưu không đủ dung lượng cho video dài. Cần khoảng {required / (1024d * 1024 * 1024):0.0} GiB, còn {drive.AvailableFreeSpace / (1024d * 1024 * 1024):0.0} GiB.");
                    }
                }
                catch (ArgumentException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var bundleCacheRoot = Path.Combine(outputDirectory, ".BiliSubStudio");
        var workRoot = Path.Combine(bundleCacheRoot, "Cache", "video");
        Directory.CreateDirectory(workRoot);
        try
        {
            File.SetAttributes(bundleCacheRoot, File.GetAttributes(bundleCacheRoot) | FileAttributes.Hidden);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var work = Path.Combine(workRoot, ResumeKey(request, selection));
        Directory.CreateDirectory(work);
        var budget = SpeedConnections(request.Speed);
        var usedRange = true;
        var peakConnections = 0;
        var transportGate = new object();
        var transports = new Dictionary<StreamKind, RangeDownloadStatus>();
        using var selectionGate = new SemaphoreSlim(1, 1);
        var current = selection;

        async Task<ResolvedStream> RefreshAsync(StreamKind kind, long seen, CancellationToken token)
        {
            await selectionGate.WaitAsync(token);
            try
            {
                var existing = kind == StreamKind.Video ? current.Video : current.Audio;
                if (existing is not null && existing.Generation != seen) return existing;
                current = await _resolver.ResolveAsync(resolveRequest, token);
                return (kind == StreamKind.Video ? current.Video : current.Audio)
                    ?? throw new InvalidOperationException("URL mới thiếu stream cần làm mới.");
            }
            finally { selectionGate.Release(); }
        }

        void Report(StreamKind kind, RangeDownloadStatus status)
        {
            long completed;
            long total;
            double bytesPerSecond;
            int activeConnections;
            int configuredConnections;
            bool rangeSupported;
            lock (transportGate)
            {
                transports[kind] = status;
                var aggregate = AggregateTransport(transports.Values);
                completed = aggregate.BytesCompleted;
                total = aggregate.TotalBytes;
                bytesPerSecond = aggregate.BytesPerSecond;
                activeConnections = aggregate.ActiveConnections;
                configuredConnections = aggregate.ConfiguredConnections;
                rangeSupported = aggregate.RangeSupported;
                peakConnections = Math.Max(peakConnections, activeConnections);
            }
            var percent = total > 0 ? completed * 90d / total : -1;
            job.SetTransport(bytesPerSecond, activeConnections, rangeSupported);
            var transport = rangeSupported ? "HTTP Range" : "yt-dlp fallback";
            job.Set("downloading", percent, $"{transport} · {activeConnections}/{configuredConnections} kết nối · {FormatSpeed(bytesPerSecond)}");
        }

        async Task<(string Path, bool Range)> DownloadOneAsync(ResolvedStream stream, string name, int connections)
        {
            try
            {
                var path = await _downloader.DownloadAsync(
                    stream, work, name, connections,
                    (seen, token) => RefreshAsync(stream.Kind, seen, token),
                    status => Report(stream.Kind, status), cancellationToken);
                return (path, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                job.Log($"Range {stream.Kind} thất bại; chuyển yt-dlp fallback: {error.Message}");
                RangeDownloadStatus fallbackStatus;
                lock (transportGate)
                {
                    if (transports.TryGetValue(stream.Kind, out var previous))
                        fallbackStatus = previous with { RangeSupported = false, ActiveConnections = 1, ConfiguredConnections = 1, BytesPerSecond = 0 };
                    else
                        fallbackStatus = new RangeDownloadStatus(false, 1, 1, 0, 0, 0);
                }
                Report(stream.Kind, fallbackStatus);
                try
                {
                    var path = await FallbackAsync(job, request, stream, work, cancellationToken);
                    return (path, false);
                }
                finally
                {
                    Report(stream.Kind, fallbackStatus with { ActiveConnections = 0, BytesPerSecond = 0 });
                }
            }
        }

        (string Path, bool Range) video = default;
        (string Path, bool Range) audio = default;
        if (selection.Video is not null && selection.Audio is not null)
        {
            if (budget == 1)
            {
                video = await DownloadOneAsync(selection.Video, "video", 1);
                audio = await DownloadOneAsync(selection.Audio, "audio", 1);
            }
            else
            {
                var videoTask = DownloadOneAsync(selection.Video, "video", budget - 1);
                var audioTask = DownloadOneAsync(selection.Audio, "audio", 1);
                await Task.WhenAll(videoTask, audioTask);
                video = await videoTask;
                audio = await audioTask;
            }
        }
        else if (selection.Video is not null)
        {
            video = await DownloadOneAsync(selection.Video, "video", budget);
        }
        else if (selection.Audio is not null)
        {
            audio = await DownloadOneAsync(selection.Audio, "audio", budget);
        }
        else
        {
            throw new InvalidOperationException("Không có stream để tải.");
        }
        usedRange = video.Path is null || video.Range;
        usedRange &= audio.Path is null || audio.Range;

        cancellationToken.ThrowIfCancellationRequested();
        var extension = string.Equals(request.Container, "mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4";
        var baseName = FileNamePolicy.Sanitize(selection.Title, FileNamePolicy.Sanitize(selection.Id, "BiliSub_Video"));
        var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, baseName + extension));
        var temporary = output + ".muxing" + extension;
        TryDelete(temporary);
        job.Set("merging", 95, "Đang ghép bằng FFmpeg...");
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            if (!string.IsNullOrWhiteSpace(video.Path))
            {
                args.AddRange(["-i", video.Path]);
            }
            if (!string.IsNullOrWhiteSpace(audio.Path))
            {
                args.AddRange(["-i", audio.Path]);
            }
            if (!string.IsNullOrWhiteSpace(video.Path) && !string.IsNullOrWhiteSpace(audio.Path))
            {
                args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
            }
            args.AddRange(["-c", "copy"]);
            if (extension == ".mp4") args.AddRange(["-movflags", "+faststart"]);
            args.Add(temporary);
            var merge = await _processes.RunAsync(ffmpeg, args, cancellationToken);
            if (merge.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg ghép stream: {merge.StandardError.Trim()}");
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0)
            {
                throw new InvalidDataException("File đầu ra rỗng.");
            }
            File.Move(temporary, output);
            Directory.Delete(work, recursive: true);
            var size = new FileInfo(output).Length;
            job.Log($"Hoàn tất: {output}");
            job.Set("done", 100, output);
            return new VideoDownloadResult(output, size, usedRange, peakConnections);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<string> FallbackAsync(
        AppJob job,
        VideoDownloadRequest request,
        ResolvedStream stream,
        string work,
        CancellationToken cancellationToken)
    {
        var ytDlp = await _tools.EnsureYtDlpAsync(cancellationToken);
        var prefix = Path.Combine(work, $"{stream.Kind.ToString().ToLowerInvariant()}_fallback");
        CleanupFallbackTemporary(prefix);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.CookieFile)) args.AddRange(["--cookies", request.CookieFile]);
        args.AddRange([
            "--ignore-config", "--no-playlist", "--continue", "--no-overwrites",
            "--retries", "20", "--fragment-retries", "20", "--file-access-retries", "5",
            "--socket-timeout", "30", "--concurrent-fragments", "1", "--http-chunk-size", "4M",
            "--no-warnings", "--newline",
            "-f", stream.FormatId, "-o", prefix + ".%(ext)s", "--print", "after_move:filepath", request.Url,
        ]);
        try
        {
            job.Set("fallback", -1, $"yt-dlp fallback {stream.Kind}...");
            var result = await _processes.RunAsync(ytDlp, args, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp fallback {stream.Kind}: {result.StandardError.Trim()}");
            }
            var candidates = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Concat(Directory.EnumerateFiles(work, Path.GetFileName(prefix) + ".*"))
                .Where(x => !x.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !x.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var output = candidates.LastOrDefault(x => new FileInfo(x).Length > 0)
                ?? throw new InvalidDataException($"yt-dlp fallback {stream.Kind} không tạo file.");
            job.Log($"yt-dlp fallback {stream.Kind} hoàn tất: {new FileInfo(output).Length} bytes");
            return output;
        }
        finally
        {
            CleanupFallbackTemporary(prefix);
        }
    }

    private static string ResumeKey(VideoDownloadRequest request, StreamSelection selection)
    {
        var identity = string.Join('\0', request.Url.Trim(), request.Quality.Trim(), request.Mode.Trim(), selection.Video?.FormatId ?? string.Empty, selection.Audio?.FormatId ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    internal static RangeDownloadStatus AggregateTransport(IEnumerable<RangeDownloadStatus> statuses)
    {
        var values = statuses.ToArray();
        return new RangeDownloadStatus(
            values.Length > 0 && values.All(x => x.RangeSupported),
            values.Sum(x => x.ActiveConnections),
            values.Sum(x => x.ConfiguredConnections),
            values.Sum(x => x.BytesCompleted),
            values.Sum(x => x.TotalBytes),
            values.Sum(x => x.BytesPerSecond));
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MiB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KiB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    private static void CleanupFallbackTemporary(string prefix)
    {
        var directory = Path.GetDirectoryName(prefix);
        if (directory is null || !Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(prefix) + ".*"))
        {
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

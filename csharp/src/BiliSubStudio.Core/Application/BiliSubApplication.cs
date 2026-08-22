using System.Text;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Media;
using BiliSubStudio.Core.Maintenance;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Subtitle;
using BiliSubStudio.Core.Tools;
using BiliSubStudio.Core.Video;

namespace BiliSubStudio.Core.Application;

public sealed class BiliSubApplication : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly JsonConfigStore _configStore;
    private readonly OcrManager _ocr;
    private readonly OcrScanner _ocrScanner;
    private readonly VideoDownloadService _video;
    private readonly SubtitleService _subtitle;
    private readonly VideoEditorService _editor;
    private readonly EditorProjectStore _editorProjects;
    private readonly WindowsProcessContainment _containment;

    public BiliSubApplication(AppPaths paths)
    {
        Paths = paths;
        _containment = new WindowsProcessContainment();
        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        }) { Timeout = TimeSpan.FromMinutes(10) };
        _configStore = new JsonConfigStore(paths);
        Settings = new SettingsApplicationService(paths, _configStore, new StorageUsageReader());
        Sessions = new SessionStore(paths);
        Authentication = new BilibiliAuthService(_http, Sessions);
        Jobs = new JobManager();
        Updates = new UpdateService(paths, _http, Jobs);
        BugReports = new BugReportService(_http);
        Processes = new ProcessRunner();
        Tools = new ToolManager(paths, _http);
        Hardware = new HardwareService();
        Media = new MediaPreviewService(Tools, Processes);
        Resolver = new YtDlpResolver(Tools, Processes);
        _video = new VideoDownloadService(paths, Resolver, new RangeDownloader(_http), Tools, Processes);
        _subtitle = new SubtitleService(Resolver, _http);
        _editor = new VideoEditorService(Tools, Processes);
        _editorProjects = new EditorProjectStore(paths);
        _ocr = new OcrManager(paths, Hardware, new OcrInstaller(paths, _http, Processes));
        _ocrScanner = new OcrScanner(Tools, Processes, _ocr, Hardware, new OcrCheckpointStore(paths));
    }

    public AppPaths Paths { get; }
    public SettingsApplicationService Settings { get; }
    public SessionStore Sessions { get; }
    public BilibiliAuthService Authentication { get; }
    public JobManager Jobs { get; }
    public UpdateService Updates { get; }
    public BugReportService BugReports { get; }
    public ProcessRunner Processes { get; }
    public ToolManager Tools { get; }
    public HardwareService Hardware { get; }
    public MediaPreviewService Media { get; }
    public YtDlpResolver Resolver { get; }
    public AppConfig Config => _configStore.Snapshot;
    public OcrStatus OcrStatus => _ocr.Status;
    public PreparedUpdate? PendingUpdate { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.InitializeAsync(cancellationToken);
        await Sessions.LoadAsync(cancellationToken);
        await _ocr.ConfigureDeviceAsync(Config.OcrDevice, cancellationToken);
    }

    public async Task<VideoMetadata> GetMetadataAsync(string url, CancellationToken cancellationToken)
    {
        var cookieFile = await Sessions.WriteNetscapeFileAsync(cancellationToken);
        return await Resolver.GetMetadataAsync(url, cookieFile, cancellationToken);
    }

    public string StartVideo(VideoDownloadRequest request)
    {
        var bundledMedia = request.MediaBundle;
        var bundledVideo = !bundledMedia || request.BundleVideo;
        var bundledThumbnail = bundledMedia && request.BundleThumbnail;
        var bundledSubtitle = bundledMedia && (request.BundleSubtitleIfAvailable || !string.IsNullOrWhiteSpace(request.BundleSubtitleTrack));
        var job = Jobs.Create(bundledMedia ? "media" : "video");
        _ = RunJobAsync(job, async () =>
        {
            await _configStore.UpdateAsync(config => config with
            {
                VideoSpeed = request.Speed,
                VideoContainer = request.Container,
                VideoMode = request.Mode,
                SubtitleFormat = bundledSubtitle && !string.IsNullOrWhiteSpace(request.BundleSubtitleFormat)
                    ? request.BundleSubtitleFormat
                    : config.SubtitleFormat,
            }, job.CancellationToken);

            var outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? Config.OutputDirectory
                : Path.GetFullPath(request.OutputDirectory.Trim());
            Directory.CreateDirectory(outputDirectory);
            var cookie = await Sessions.WriteNetscapeFileAsync(job.CancellationToken);

            if (!bundledMedia)
            {
                var result = await _video.RunAsync(job, request with
                {
                    OutputDirectory = outputDirectory,
                    CookieFile = cookie,
                });
                job.Finish(null, "Đã tải: " + result.OutputPath, result);
                return;
            }

            if (!bundledVideo && !bundledThumbnail && !bundledSubtitle)
            {
                throw new InvalidOperationException("Tác vụ media không có nội dung nào được chọn.");
            }

            VideoMetadata? metadata = null;
            if (bundledThumbnail || bundledSubtitle)
            {
                metadata = await Resolver.GetMetadataAsync(request.Url, cookie, job.CancellationToken);
            }

            SubtitleTrack? subtitleTrack = null;
            if (bundledSubtitle && metadata is not null)
            {
                subtitleTrack = string.IsNullOrWhiteSpace(request.BundleSubtitleTrack)
                    ? null
                    : metadata.Subtitles.FirstOrDefault(x => string.Equals(x.Language, request.BundleSubtitleTrack, StringComparison.Ordinal));
                if (subtitleTrack is null && metadata.Subtitles.Count > 0)
                {
                    subtitleTrack = metadata.Subtitles
                        .OrderBy(track =>
                        {
                            var separator = track.Language.IndexOf(':');
                            var language = separator >= 0 ? track.Language[(separator + 1)..] : track.Language;
                            var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                                || language.Contains("chi", StringComparison.OrdinalIgnoreCase);
                            return track.Official && chinese ? 0 : chinese ? 1 : track.Official ? 2 : 3;
                        })
                        .ThenBy(track => track.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .First();
                }
            }

            var thumbnailWeight = bundledThumbnail ? 5d : 0d;
            var videoWeight = bundledVideo ? 85d : 0d;
            var subtitleWeight = bundledSubtitle ? 10d : 0d;
            var totalWeight = Math.Max(1d, thumbnailWeight + videoWeight + subtitleWeight);
            var phaseCursor = 0d;

            async Task<T> RunPhaseAsync<T>(
                string phase,
                string label,
                double start,
                double span,
                Func<AppJob, Task<T>> action)
            {
                using var child = new AppJob($"{job.Id}:{phase}", phase);
                using var registration = job.CancellationToken.Register(child.Cancel);
                var task = action(child);
                var logOffset = 0;

                void Publish(JobSnapshot snapshot, bool completed = false)
                {
                    logOffset = snapshot.LogNext;
                    foreach (var line in snapshot.Logs) job.Log($"{label} · {line}");
                    var mapped = completed
                        ? start + span
                        : start + Math.Clamp(snapshot.Progress, 0, 100) * span / 100d;
                    job.Set(phase, mapped, $"{label}: {snapshot.Message}");
                    job.SetTransport(snapshot.BytesPerSecond, snapshot.ActiveConnections, snapshot.RangeSupported);
                }

                try
                {
                    while (!task.IsCompleted)
                    {
                        Publish(child.Snapshot(logOffset));
                        await Task.WhenAny(task, Task.Delay(250, job.CancellationToken));
                        job.CancellationToken.ThrowIfCancellationRequested();
                    }

                    Publish(child.Snapshot(logOffset), completed: true);
                    return await task;
                }
                catch (OperationCanceledException)
                {
                    child.Cancel();
                    try { await task; } catch { }
                    throw;
                }
            }

            string? thumbnailPath = null;
            string? thumbnailWarning = null;
            if (bundledThumbnail)
            {
                var thumbnailSpan = thumbnailWeight * 100d / totalWeight;
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.ThumbnailUrl))
                {
                    thumbnailWarning = "Nguồn không cung cấp thumbnail.";
                    job.Log("Thumbnail · nguồn không cung cấp thumbnail; bỏ qua.");
                    phaseCursor += thumbnailSpan;
                    job.Set("bundle-thumbnail-skip", phaseCursor, "Thumbnail: nguồn không có, đã bỏ qua.");
                }
                else
                {
                    try
                    {
                        thumbnailPath = await RunPhaseAsync(
                            "bundle-thumbnail",
                            "Thumbnail",
                            phaseCursor,
                            thumbnailSpan,
                            async child =>
                            {
                                var url = metadata.ThumbnailUrl.StartsWith("//", StringComparison.Ordinal)
                                    ? "https:" + metadata.ThumbnailUrl
                                    : metadata.ThumbnailUrl;
                                Exception? last = null;
                                for (var attempt = 1; attempt <= 4; attempt++)
                                {
                                    child.CancellationToken.ThrowIfCancellationRequested();
                                    try
                                    {
                                        child.Set("downloading", 10, $"Đang tải thumbnail · lần {attempt}/4...");
                                        using var message = new HttpRequestMessage(HttpMethod.Get, url);
                                        message.Headers.Referrer = new Uri("https://www.bilibili.com/");
                                        message.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BiliSubStudio/4");
                                        if (!string.IsNullOrWhiteSpace(Sessions.Cookie)) message.Headers.TryAddWithoutValidation("Cookie", Sessions.Cookie);
                                        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, child.CancellationToken);
                                        response.EnsureSuccessStatusCode();
                                        const long maxThumbnailBytes = 32L * 1024 * 1024;
                                        if (response.Content.Headers.ContentLength is > maxThumbnailBytes)
                                            throw new InvalidDataException("Thumbnail vượt giới hạn 32 MiB.");

                                        var extension = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
                                        {
                                            "image/png" => ".png",
                                            "image/webp" => ".webp",
                                            "image/avif" => ".avif",
                                            _ => ".jpg",
                                        };
                                        var baseName = FileNamePolicy.Sanitize(metadata.Title, FileNamePolicy.Sanitize(metadata.Id, "BiliSub_Video"));
                                        var path = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, baseName + " [thumbnail]" + extension));
                                        var temporary = path + ".tmp";
                                        try
                                        {
                                            await using var source = await response.Content.ReadAsStreamAsync(child.CancellationToken);
                                            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                                            var buffer = new byte[128 * 1024];
                                            long total = 0;
                                            var expected = response.Content.Headers.ContentLength.GetValueOrDefault();
                                            while (true)
                                            {
                                                var read = await source.ReadAsync(buffer, child.CancellationToken);
                                                if (read == 0) break;
                                                total += read;
                                                if (total > maxThumbnailBytes) throw new InvalidDataException("Thumbnail vượt giới hạn 32 MiB.");
                                                await target.WriteAsync(buffer.AsMemory(0, read), child.CancellationToken);
                                                if (expected > 0) child.Set("downloading", Math.Min(95, 10 + total * 85d / expected), "Đang tải thumbnail...");
                                            }
                                            await target.FlushAsync(child.CancellationToken);
                                            target.Flush(flushToDisk: true);
                                            target.Close();
                                            File.Move(temporary, path);
                                            child.Log($"Đã lưu: {path}");
                                            child.Set("done", 100, path);
                                            return path;
                                        }
                                        finally
                                        {
                                            try { File.Delete(temporary); } catch { }
                                        }
                                    }
                                    catch (OperationCanceledException error) when (!child.CancellationToken.IsCancellationRequested)
                                    {
                                        last = error;
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception error) when (error is HttpRequestException or IOException)
                                    {
                                        last = error;
                                    }

                                    if (attempt < 4) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), child.CancellationToken);
                                }
                                throw new HttpRequestException("Tải thumbnail thất bại sau 4 lần thử.", last);
                            });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        thumbnailWarning = error.Message;
                        job.Log("Thumbnail · cảnh báo: " + error.Message);
                    }
                    finally
                    {
                        phaseCursor += thumbnailSpan;
                    }
                }
            }

            VideoDownloadResult? video = null;
            if (bundledVideo)
            {
                var videoSpan = videoWeight * 100d / totalWeight;
                video = await RunPhaseAsync(
                    "bundle-video",
                    "Video",
                    phaseCursor,
                    videoSpan,
                    child => _video.RunAsync(child, request with
                    {
                        OutputDirectory = outputDirectory,
                        CookieFile = cookie,
                    }));
                phaseCursor += videoSpan;
                job.SetTransport(0, 0, null);
            }

            SubtitleResult? subtitle = null;
            string? subtitleWarning = null;
            if (bundledSubtitle)
            {
                var subtitleSpan = subtitleWeight * 100d / totalWeight;
                if (subtitleTrack is null)
                {
                    subtitleWarning = "Nguồn không có track phụ đề phù hợp.";
                    job.Log("Phụ đề · nguồn không có track phù hợp; đã bỏ qua.");
                    phaseCursor += subtitleSpan;
                    job.Set("bundle-subtitle-skip", phaseCursor, "Phụ đề: nguồn không có, đã bỏ qua.");
                }
                else
                {
                    try
                    {
                        subtitle = await RunPhaseAsync(
                            "bundle-subtitle",
                            "Phụ đề",
                            phaseCursor,
                            subtitleSpan,
                            child => _subtitle.RunAsync(child, new SubtitleRequest(
                                request.Url,
                                string.IsNullOrWhiteSpace(request.BundleSubtitleFormat) ? Config.SubtitleFormat : request.BundleSubtitleFormat,
                                subtitleTrack.Language,
                                outputDirectory,
                                cookie,
                                Sessions.Cookie)));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        subtitleWarning = error.Message;
                        job.Log("Phụ đề · cảnh báo: " + error.Message);
                    }
                    finally
                    {
                        phaseCursor += subtitleSpan;
                    }
                }
            }

            if (video is not null) job.Log($"Video hoàn tất: {video.OutputPath}");
            if (thumbnailPath is not null) job.Log($"Thumbnail hoàn tất: {thumbnailPath}");
            if (subtitle is not null) job.Log($"Phụ đề hoàn tất: {subtitle.OutputPath}");

            var hasWarning = thumbnailWarning is not null || subtitleWarning is not null;
            var completed = new List<string>();
            if (video is not null) completed.Add("video");
            if (thumbnailPath is not null) completed.Add("thumbnail");
            if (subtitle is not null) completed.Add("phụ đề");

            if (hasWarning)
                job.Set("bundle-complete", 100, "Tác vụ đã hoàn tất; có mục bị bỏ qua/cảnh báo, xem nhật ký.");
            else
                job.Set("bundle-complete", 100, "Các mục media đã chọn đều hoàn tất.");

            var completedLabel = completed.Count > 0 ? string.Join(" + ", completed) : "không có file phù hợp";
            var warningSuffix = hasWarning ? " · có cảnh báo" : string.Empty;
            object? mediaResult = video is not null ? video : subtitle is not null ? subtitle : thumbnailPath;
            job.Finish(null, $"Hoàn tất media · {completedLabel}{warningSuffix}: {outputDirectory}", mediaResult);
        });
        return job.Id;
    }

    public string StartSubtitle(SubtitleRequest request)
    {
        var job = Jobs.Create("subtitle");
        _ = RunJobAsync(job, async () =>
        {
            await _configStore.UpdateAsync(config => config with { SubtitleFormat = request.Format }, job.CancellationToken);
            var cookie = await Sessions.WriteNetscapeFileAsync(job.CancellationToken);
            var result = await _subtitle.RunAsync(job, request with
            {
                OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Config.OutputDirectory : request.OutputDirectory,
                CookieFile = cookie,
                CookieRaw = Sessions.Cookie,
            });
            job.Finish(null, "Đã lưu: " + result.OutputPath, result);
        });
        return job.Id;
    }

    public string StartEditor(VideoEditRequest request)
    {
        var job = Jobs.Create("editor", cleanupAwareCancel: true);
        _ = RunJobAsync(job, async () =>
        {
            var result = await _editor.RunAsync(job, request with
            {
                OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Config.OutputDirectory : request.OutputDirectory,
            });
            job.Finish(null, "Đã xuất: " + result.OutputPath, result);
        });
        return job.Id;
    }

    public Task<EditorProject> LoadEditorProjectAsync(string path, MediaPreviewInfo media, CancellationToken cancellationToken) =>
        _editorProjects.LoadOrCreateAsync(path, media.Width, media.Height, media.Duration, cancellationToken);

    public Task SaveEditorProjectAsync(EditorProject project, CancellationToken cancellationToken) =>
        _editorProjects.SaveAsync(project, cancellationToken);

    public Task<byte[]> GetEditorPreviewFrameJpegAsync(
        string path,
        double seconds,
        MediaPreviewInfo media,
        IReadOnlyList<EditRegion> regions,
        CancellationToken cancellationToken) =>
        _editor.GetPreviewFrameJpegAsync(path, seconds, media.Width, media.Height, media.Duration, regions, cancellationToken);

    public async Task<OcrResult> RecognizeFrameAsync(string path, double at, OcrRegion region, string device, CancellationToken cancellationToken) =>
        await _ocrScanner.RecognizeFrameAsync(path, at, region, device, cancellationToken);

    public async Task<OcrStatus> PrepareOcrAsync(string device, CancellationToken cancellationToken)
    {
        await _ocr.ConfigureDeviceAsync(device, cancellationToken);
        await _ocr.EnsureAsync(cancellationToken);
        await _configStore.UpdateAsync(config => config with { OcrDevice = device }, cancellationToken);
        return _ocr.Status;
    }

    public string StartOcrScan(OcrScanRequest request, OcrScanStartMode startMode)
    {
        var job = Jobs.Create("ocrscan", pausable: true);
        _ = RunJobAsync(job, async () =>
        {
            await _configStore.UpdateAsync(config => config with { OcrDevice = request.Device }, job.CancellationToken);
            var result = await _ocrScanner.RunAsync(job, request, startMode);
            if (!result.Paused) job.Finish(null, $"Đã quét xong: {result.Cues.Count} câu · {result.RealtimeSpeed:0.0}× realtime", result);
        });
        return job.Id;
    }

    public async Task SetOcrRegionAsync(OcrRegion region, CancellationToken cancellationToken)
    {
        var normalized = OcrCheckpointStore.NormalizeRegion(region);
        var left = Math.Clamp((int)Math.Floor(normalized.X * 100), 0, 99);
        var top = Math.Clamp((int)Math.Floor(normalized.Y * 100), 0, 99);
        var right = Math.Clamp(Math.Max(left + 1, (int)Math.Ceiling((normalized.X + normalized.Width) * 100)), 1, 100);
        var bottom = Math.Clamp(Math.Max(top + 1, (int)Math.Ceiling((normalized.Y + normalized.Height) * 100)), 1, 100);
        await _configStore.UpdateAsync(config => config with
        {
            OcrLeft = left,
            OcrTop = top,
            OcrRight = right,
            OcrBottom = bottom,
        }, cancellationToken);
    }

    public Task<OcrCheckpointInfo> InspectOcrCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _ocrScanner.InspectCheckpointAsync(request, cancellationToken);
    public Task RemoveOcrCheckpointAsync(OcrScanRequest request, CancellationToken cancellationToken) => _ocrScanner.RemoveCheckpointAsync(request, cancellationToken);

    public async Task<string> ExportOcrAsync(IEnumerable<OcrCue> cues, string? outputDirectory, string? fileName, CancellationToken cancellationToken)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory) ? Config.OutputDirectory : Path.GetFullPath(outputDirectory.Trim());
        Directory.CreateDirectory(directory);
        var name = FileNamePolicy.Sanitize(fileName, "BiliSub_OCR_Chinese");
        if (!name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) name += ".srt";
        var path = FileNamePolicy.UniquePath(Path.Combine(directory, name));
        var output = new StringBuilder();
        var index = 0;
        foreach (var cue in cues)
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text)) continue;
            output.Append(++index).Append('\n').Append(SrtTime(cue.Start)).Append(" --> ").Append(SrtTime(cue.End)).Append('\n').Append(text).Append("\n\n");
        }
        await File.WriteAllTextAsync(path, output.ToString(), new UTF8Encoding(false), cancellationToken);
        return path;
    }

    public async Task PauseJobAsync(string id, CancellationToken cancellationToken)
    {
        if (!Jobs.TryGet(id, out var job) || job is null) throw new KeyNotFoundException("Tác vụ không tồn tại.");
        var pause = job.RequestPauseAsync();
        await pause.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
        if (job.Snapshot().Status != "paused") throw new InvalidOperationException(job.Snapshot().Message);
    }

    public void CancelJob(string id) => Jobs.Cancel(id);

    public async Task CancelOcrScanAsync(string id, OcrScanRequest request, CancellationToken cancellationToken)
    {
        if (Jobs.TryGet(id, out var job) && job is not null)
        {
            job.Cancel();
            if (!job.Snapshot().Done)
                await job.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        }
        await _ocr.StopAsync(cancellationToken);
        if (_ocr.Status.Workers != 0)
            throw new IOException($"OCR vẫn còn {_ocr.Status.Workers} Python worker sau khi Hủy.");
        if (_ocrScanner.ActiveProcessCount != 0)
            throw new IOException($"OCR vẫn còn {_ocrScanner.ActiveProcessCount} FFmpeg/process tree sau khi Hủy.");
        await _ocrScanner.RemoveCheckpointAsync(request, cancellationToken);
        var checkpoint = await _ocrScanner.InspectCheckpointAsync(request, cancellationToken);
        if (checkpoint.Exists)
            throw new IOException("Checkpoint OCR vẫn còn sau khi Hủy; không thể xác nhận trạng thái Quét từ đầu.");
    }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var snapshot in Jobs.ActiveSnapshots().Where(x => x.PauseSupported))
        {
            await PauseJobAsync(snapshot.Id, cancellationToken);
        }
        Jobs.CancelAll();
        await _ocr.StopAsync();
        await Sessions.DeleteTemporaryAsync(cancellationToken);
    }

    public void CleanupStorage()
    {
        if (Jobs.HasActiveJobs) throw new InvalidOperationException("Đang có tác vụ; không thể dọn cache.");
        if (PendingUpdate is not null) throw new InvalidOperationException("Đã chuẩn bị bản cập nhật; hãy đóng ứng dụng trước khi dọn Temp.");
        if (Directory.Exists(Paths.Temp)) Directory.Delete(Paths.Temp, recursive: true);
        if (Directory.Exists(Paths.Cache)) Directory.Delete(Paths.Cache, recursive: true);
        Directory.CreateDirectory(Paths.Temp);
        Directory.CreateDirectory(Paths.Cache);
    }

    public async Task ResetToolsAsync(CancellationToken cancellationToken)
    {
        if (Jobs.HasActiveJobs) throw new InvalidOperationException("Đang có tác vụ; không thể xóa Tools.");
        await _ocr.StopAsync();
        if (Directory.Exists(Paths.Tools)) Directory.Delete(Paths.Tools, recursive: true);
        Directory.CreateDirectory(Paths.Tools);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task RemoveOcrAsync(CancellationToken cancellationToken) => _ocr.RemoveAsync(cancellationToken);

    public async Task<PreparedUpdate> PrepareUpdateAsync(CancellationToken cancellationToken)
    {
        PendingUpdate = await Updates.PrepareAsync(cancellationToken);
        return PendingUpdate;
    }

    public void LaunchPendingUpdate()
    {
        if (PendingUpdate is null) return;
        Updates.LaunchPrepared(PendingUpdate);
        PendingUpdate = null;
    }

    private static async Task RunJobAsync(AppJob job, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException)
        {
            if (!job.Snapshot().Done) job.CancelComplete();
        }
        catch (Exception error) { job.Finish(error, error.Message); }
    }

    private static string SrtTime(double seconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    public async ValueTask DisposeAsync()
    {
        Jobs.CancelAll();
        await _ocr.DisposeAsync();
        await Sessions.DeleteTemporaryAsync();
        Jobs.Dispose();
        _configStore.Dispose();
        _http.Dispose();
        _containment.Dispose();
    }
}

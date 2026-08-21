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
        var job = Jobs.Create("video");
        _ = RunJobAsync(job, async () =>
        {
            await _configStore.UpdateAsync(config => config with
            {
                VideoSpeed = request.Speed,
                VideoContainer = request.Container,
                VideoMode = request.Mode,
            }, job.CancellationToken);
            var cookie = await Sessions.WriteNetscapeFileAsync(job.CancellationToken);
            var result = await _video.RunAsync(job, request with
            {
                OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Config.OutputDirectory : request.OutputDirectory,
                CookieFile = cookie,
            });
            job.Finish(null, "Đã tải: " + result.OutputPath, result);
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
        var job = Jobs.Create("editor");
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

    public async Task<OcrResult> RecognizeFrameAsync(string path, double at, OcrRegion region, string device, CancellationToken cancellationToken) =>
        await _ocrScanner.RecognizeFrameAsync(path, at, region, device, cancellationToken);

    public async Task<OcrStatus> PrepareOcrAsync(string device, CancellationToken cancellationToken)
    {
        await _ocr.ConfigureDeviceAsync(device, cancellationToken);
        await _ocr.EnsureAsync(cancellationToken);
        await _configStore.UpdateAsync(config => config with { OcrDevice = device }, cancellationToken);
        return _ocr.Status;
    }

    public string StartOcrScan(OcrScanRequest request)
    {
        var job = Jobs.Create("ocrscan", pausable: true);
        _ = RunJobAsync(job, async () =>
        {
            await _configStore.UpdateAsync(config => config with { OcrDevice = request.Device }, job.CancellationToken);
            var result = await _ocrScanner.RunAsync(job, request);
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
            if (!job.Snapshot().Done) job.Cancel();
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

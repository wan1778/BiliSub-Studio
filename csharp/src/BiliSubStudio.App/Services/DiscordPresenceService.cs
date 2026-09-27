using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Diagnostics;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Presence;

namespace BiliSubStudio.App.Services;

// Presence Discord theo job thật: details = việc đang làm, state = % tiến
// triển, elapsed theo job, ảnh lớn key "logo" (upload trong portal). Poll
// 15s (giới hạn Discord), chỉ gửi khi nội dung đổi. Discord tắt thì lặng lẽ
// thử lại sau; không bao giờ ảnh hưởng scan/Edit. Chỉ đọc snapshot job,
// không đụng điều khiển.
internal sealed class DiscordPresenceService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    private readonly BiliSubApplication _application;
    private readonly string _clientId;
    private readonly ApplicationLog? _log;
    private readonly DiscordIpcClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly long _appStartUnix;
    private string _lastPayload = string.Empty;
    private string _location = string.Empty;
    private string? _video;
    private string? _project;
    private readonly object _locationGate = new();
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    public DiscordPresenceService(BiliSubApplication application, string clientId, ApplicationLog? log = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _clientId = string.IsNullOrWhiteSpace(clientId) ? throw new ArgumentException("Thiếu Discord Application ID.", nameof(clientId)) : clientId.Trim();
        _log = log;
        _appStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _loop = Task.Run(LoopAsync);
    }

    // Pure (có contract test): kind job -> chữ hiện dòng details.
    internal static string ActivityName(string kind) => DiscordPresenceContent.ActivityName(kind);

    internal static string BuildActivityJson(string details, string state, long? startUnix, string largeImageKey = "logo") =>
        DiscordPresenceContent.BuildActivityJson(details, state, startUnix, largeImageKey);

    // Tab hiện tại (MainWindow gọi khi đổi tab): có job chạy thì job thắng,
    // không job thì hiện tab đang xem. Thread-safe, never throws.
    public void SetLocation(string location)
    {
        try
        {
            lock (_locationGate) _location = (location ?? string.Empty).Trim();
        }
        catch { }
    }

    private string CurrentLocation()
    {
        try { lock (_locationGate) return _location; }
        catch { return string.Empty; }
    }

    // Context media (page đẩy khi chọn video/project, quét, nghe): tên video
    // hiện khi có job, tên project Edit hiện khi rảnh ở tab Edit. Sticky cho
    // tới khi context mới đè (không tự xóa để presence không chớp tắt).
    public void SetContext(string? video, string? project)
    {
        try
        {
            lock (_locationGate)
            {
                if (video is not null) _video = video;
                if (project is not null) _project = project;
            }
        }
        catch { }
    }

    private (string? Video, string? Project) CurrentContext()
    {
        try { lock (_locationGate) return (_video, _project); }
        catch { return (null, null); }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch { }
            try
            {
                await Task.Delay(_client.Connected ? PollInterval : RetryInterval, _cts.Token);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Tick()
    {
        if (DateTime.UtcNow < _nextAttemptUtc && !_client.Connected) return;
        if (!_client.Connected && !_client.TryConnect(_clientId))
        {
            _nextAttemptUtc = DateTime.UtcNow + RetryInterval;
            return;
        }
        JobSnapshot? active = null;
        try { active = _application.Jobs.ActiveSnapshots().FirstOrDefault(); }
        catch { }
        // Mốc elapsed DUY NHẤT từ lúc mở app (user chốt): chuyển tab, bấm gì,
        // job bắt đầu/kết thúc cũng không reset; chỉ tắt/mở app mới đếm lại.
        string payload;
        var (video, project) = CurrentContext();
        if (active is null)
        {
            var location = CurrentLocation();
            payload = string.IsNullOrWhiteSpace(location)
                ? BuildActivityJson("Đang rảnh", "Mở BiliSub Studio", _appStartUnix)
                : BuildActivityJson("Tab " + location,
                    string.Equals(location, "Edit", StringComparison.Ordinal) || string.Equals(location, "Timeline", StringComparison.Ordinal)
                        ? DiscordPresenceContent.ComposeIdleState(video, project) : "Đang xem",
                    _appStartUnix);
        }
        else
        {
            payload = BuildActivityJson(ActivityName(active.Kind),
                DiscordPresenceContent.ComposeJobState(active.Progress, active.Status, active.PauseRequested, video),
                _appStartUnix);
        }
        if (string.Equals(payload, _lastPayload, StringComparison.Ordinal)) return;
        if (_client.TrySendActivity(payload))
            _lastPayload = payload;
        else
            _nextAttemptUtc = DateTime.UtcNow + RetryInterval;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _client.TryClearActivity(); } catch { }
        _client.Dispose();
        _cts.Dispose();
    }
}

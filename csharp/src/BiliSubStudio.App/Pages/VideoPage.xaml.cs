using System.Collections.ObjectModel;
using BiliSubStudio.App.Services;
using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Diagnostics;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Video;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class VideoPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly IFolderPickerService _folderPicker;
    private readonly ApplicationLog _log;
    private readonly ObservableCollection<MediaQueueItem> _queueItems = [];
    private int _queueCounter;
    private string? _metadataUrl;
    private string? _metadataTitle;
    private string? _jobId;
    private bool _monitoringQueue;
    private bool _submitting;
    private readonly CancellationTokenSource _queueMonitorCancellation = new();

    public VideoPage(BiliSubApplication application, IFolderPickerService folderPicker, ApplicationLog log)
    {
        _application = application;
        _folderPicker = folderPicker;
        _log = log;
        InitializeComponent();
        QueueList.ItemsSource = _queueItems;
    }

    public void ApplyConfiguration()
    {
        Select(SpeedBox, _application.Config.VideoSpeed);
        Select(ContainerBox, _application.Config.VideoContainer);
        Select(ModeBox, _application.Config.VideoMode);
        Select(FormatBox, _application.Config.SubtitleFormat);
        OutputPathBox.Text = string.Equals(
            _application.Config.OutputDirectory,
            _application.Paths.DefaultDownloads,
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : _application.Config.OutputDirectory;
        StartButton.IsEnabled = false;
    }

    private static void Select(ComboBox box, string value)
    {
        for (var index = 0; index < box.Items.Count; index++)
            if (string.Equals(box.Items[index]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = index; return; }
    }

    private async void LoadMetadata_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        _metadataUrl = null;
        _metadataTitle = null;
        StartButton.IsEnabled = false;
        QualityBox.Items.Clear();
        TrackBox.Items.Clear();

        if (string.IsNullOrWhiteSpace(url))
        {
            MetadataText.Text = "Hãy nhập liên kết Bilibili.";
            StatusText.Text = "Chưa có nguồn để kiểm tra.";
            _log.Warning("Media", "Kiểm tra nguồn bị bỏ qua vì URL rỗng.");
            return;
        }

        try
        {
            LoadMetadataButton.IsEnabled = false;
            StatusText.Text = "Đang đọc video, thumbnail và phụ đề...";
            _log.Info("Media", "Đang kiểm tra metadata Bilibili.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var metadata = await _application.GetMetadataAsync(url, timeout.Token);

            if (!string.Equals(UrlBox.Text.Trim(), url, StringComparison.Ordinal))
            {
                StatusText.Text = "URL đã đổi trong lúc kiểm tra; kết quả cũ đã bị bỏ.";
                _log.Warning("Media", "Metadata cũ bị bỏ vì URL đã đổi trong lúc request đang chạy.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.SubtitleDiscoveryWarning))
                _log.Warning("Media", metadata.SubtitleDiscoveryWarning);

            foreach (var quality in metadata.Qualities) QualityBox.Items.Add(quality);
            if (QualityBox.Items.Count == 0) QualityBox.Items.Add("best");
            QualityBox.SelectedIndex = 0;

            foreach (var track in metadata.Subtitles) TrackBox.Items.Add(track);
            var preferred = SubtitleTrackPolicy.Preferred(metadata.Subtitles);
            if (preferred is not null)
            {
                for (var index = 0; index < metadata.Subtitles.Count; index++)
                {
                    if (string.Equals(metadata.Subtitles[index].Language, preferred.Language, StringComparison.Ordinal))
                    {
                        TrackBox.SelectedIndex = index;
                        break;
                    }
                }
            }

            _metadataUrl = url;
            _metadataTitle = metadata.Title;
            var hasThumbnail = !string.IsNullOrWhiteSpace(metadata.ThumbnailUrl);
            var hasOutput = !string.IsNullOrWhiteSpace(OutputPathBox.Text);
            var normalCount = metadata.Subtitles.Count(track => track.Official);
            var aiCount = metadata.Subtitles.Count(track => track.Ai);
            var subtitleSummary = normalCount > 0
                ? $"{normalCount} có sẵn"
                : aiCount > 0
                    ? $"{aiCount} Bilibili AI"
                    : "Không";
            MetadataText.Text = $"{metadata.Title}\nVideo: {QualityBox.Items.Count} lựa chọn · Thumbnail: {(hasThumbnail ? "Có" : "Không")} · Phụ đề: {subtitleSummary}";
            StartButton.IsEnabled = hasOutput;
            StatusText.Text = !hasOutput
                ? "Nguồn hợp lệ. Chọn thư mục lưu để tiếp tục."
                : "Nguồn hợp lệ. Không chọn mục tải riêng = tải Video + Thumbnail + Phụ đề nếu có.";
            _log.Info("Media", $"Metadata hợp lệ · {QualityBox.Items.Count} chất lượng · {(hasThumbnail ? "có" : "không có")} thumbnail · phụ đề {subtitleSummary}.");
        }
        catch (OperationCanceledException)
        {
            MetadataText.Text = "Kiểm tra metadata quá thời gian; dữ liệu nguồn cũ đã bị xóa.";
            StatusText.Text = "Không nhận được metadata trong 90 giây. Hãy thử lại.";
            _log.Warning("Media", "Kiểm tra metadata quá thời gian sau 90 giây.");
        }
        catch (Exception error)
        {
            MetadataText.Text = "Không đọc được metadata; dữ liệu nguồn cũ đã bị xóa.";
            StatusText.Text = error.Message;
            _log.Error("Media", "Không đọc được metadata: " + error.Message);
        }
        finally
        {
            LoadMetadataButton.IsEnabled = true;
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(UrlBox.Text.Trim(), _metadataUrl, StringComparison.Ordinal))
        {
            _metadataUrl = null;
            _metadataTitle = null;
            StartButton.IsEnabled = false;
            QualityBox.Items.Clear();
            TrackBox.Items.Clear();
            MetadataText.Text = "URL đã đổi. Bấm Kiểm tra lại để đọc nguồn mới.";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ChooseOutputButton))
        {
            try
            {
                ChooseOutputButton.IsEnabled = false;
                var selected = await _folderPicker.PickFolderAsync(OutputPathBox.Text.Trim());
                if (string.IsNullOrWhiteSpace(selected))
                {
                    StatusText.Text = "Đã hủy chọn thư mục; nơi lưu không thay đổi.";
                    return;
                }

                var snapshot = await _application.Settings.SetOutputDirectoryAsync(selected);
                OutputPathBox.Text = snapshot.Config.OutputDirectory;
                StartButton.IsEnabled = _metadataUrl is not null;
                StatusText.Text = StartButton.IsEnabled
                    ? "Đã chọn nơi lưu. Sẵn sàng tải media."
                    : "Đã chọn nơi lưu. Hãy Kiểm tra nguồn trước khi tải.";
                _log.Info("Media", "Đã chọn thư mục đầu ra cho Media.");
            }
            catch (Exception error)
            {
                StatusText.Text = "Không dùng được thư mục đã chọn: " + error.Message;
                StartButton.IsEnabled = false;
                _log.Error("Media", "Thư mục đầu ra không dùng được: " + error.Message);
            }
            finally
            {
                ChooseOutputButton.IsEnabled = true;
            }
            return;
        }

        if (_submitting) return;
        _submitting = true;
        SetSubmissionControlsEnabled(false);
        try
        {
            await EnqueueCurrentAsync();
        }
        finally
        {
            _submitting = false;
            SetSubmissionControlsEnabled(true);
            StartButton.IsEnabled = _metadataUrl is not null && !string.IsNullOrWhiteSpace(OutputPathBox.Text);
        }
    }

    private async Task EnqueueCurrentAsync()
    {
        var sourceUrl = _metadataUrl;
        var metadataTitle = _metadataTitle;
        if (sourceUrl is null || !string.Equals(sourceUrl, UrlBox.Text.Trim(), StringComparison.Ordinal))
        {
            StatusText.Text = "URL chưa được kiểm tra hoặc đã thay đổi. Bấm Kiểm tra trước khi tải.";
            _log.Warning("Media", "Không bắt đầu tải vì URL chưa được kiểm tra hoặc đã thay đổi.");
            return;
        }

        var videoSelected = VideoAssetCheckBox.IsChecked == true;
        var thumbnailSelected = ThumbnailAssetCheckBox.IsChecked == true;
        var subtitleSelected = SubtitleAssetCheckBox.IsChecked == true;
        var hasExplicitSelection = videoSelected || thumbnailSelected || subtitleSelected;
        var downloadVideo = !hasExplicitSelection || videoSelected;
        var downloadThumbnail = !hasExplicitSelection || thumbnailSelected;
        var downloadSubtitle = !hasExplicitSelection || subtitleSelected;
        var quality = QualityBox.SelectedItem?.ToString();
        var container = ContainerBox.SelectedItem?.ToString() ?? "mp4";
        var mode = ModeBox.SelectedItem?.ToString() ?? "video+audio";
        var speed = SpeedBox.SelectedItem?.ToString() ?? "fast";
        var subtitleFormat = FormatBox.SelectedItem?.ToString() ?? "srt";
        var track = TrackBox.SelectedItem as SubtitleTrack;

        if (downloadVideo && quality is null)
        {
            StatusText.Text = "Nguồn chưa có lựa chọn chất lượng video hợp lệ.";
            _log.Warning("Media", "Không bắt đầu tải vì chưa có chất lượng video hợp lệ.");
            return;
        }

        var outputDirectory = OutputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            StatusText.Text = "Hãy chọn thư mục lưu trước khi tải.";
            StartButton.IsEnabled = false;
            _log.Warning("Media", "Không bắt đầu tải vì chưa chọn thư mục lưu.");
            return;
        }

        if (_queueItems.Count(item => !item.Done) >= 50)
        {
            StatusText.Text = "Hàng đợi đã có 50 mục. Hãy đợi hoặc hủy bớt mục trước khi thêm.";
            _log.Warning("Media", "Không thêm media vì hàng đợi đã đạt giới hạn 50 mục.");
            return;
        }

        try
        {
            var snapshot = await _application.Settings.SetOutputDirectoryAsync(outputDirectory);
            outputDirectory = snapshot.Config.OutputDirectory;
            OutputPathBox.Text = outputDirectory;
        }
        catch (Exception error)
        {
            StatusText.Text = "Thư mục lưu không dùng được: " + error.Message;
            StartButton.IsEnabled = false;
            _log.Error("Media", "Xác minh thư mục lưu lỗi: " + error.Message);
            return;
        }

        Progress.Value = 0;
        PercentText.Text = "0%";

        var request = new VideoDownloadRequest(
            sourceUrl,
            quality ?? "best",
            container,
            mode,
            speed,
            outputDirectory,
            BundleSubtitleFormat: subtitleFormat,
            BundleSubtitleTrack: downloadSubtitle ? track?.Language ?? string.Empty : string.Empty,
            BundleSubtitleIfAvailable: downloadSubtitle,
            BundleThumbnail: downloadThumbnail,
            MediaBundle: true,
            BundleVideo: downloadVideo);
        string jobId;
        try
        {
            jobId = _application.StartVideo(request);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không thể thêm vào hàng đợi: " + error.Message;
            _log.Error("Media", StatusText.Text);
            return;
        }
        var item = new MediaQueueItem(++_queueCounter, jobId, metadataTitle ?? sourceUrl, sourceUrl, outputDirectory);
        _queueItems.Add(item);
        QueueList.Visibility = Visibility.Visible;
        QueueEmptyText.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        StatusText.Text = $"Đã thêm mục {item.Number} vào hàng đợi. Bạn có thể nhập URL tiếp theo.";
        _log.Info("Media", hasExplicitSelection ? "Đã thêm các asset đã chọn vào hàng đợi." : "Đã thêm bộ Media mặc định vào hàng đợi.", jobId);
        ResetSourceForNextItem();
        _ = MonitorQueueAsync(_queueMonitorCancellation.Token);
    }

    private void SetSubmissionControlsEnabled(bool enabled)
    {
        UrlBox.IsEnabled = enabled;
        LoadMetadataButton.IsEnabled = enabled;
        ChooseOutputButton.IsEnabled = enabled;
        VideoAssetCheckBox.IsEnabled = enabled;
        ThumbnailAssetCheckBox.IsEnabled = enabled;
        SubtitleAssetCheckBox.IsEnabled = enabled;
        QualityBox.IsEnabled = enabled;
        ModeBox.IsEnabled = enabled;
        ContainerBox.IsEnabled = enabled;
        SpeedBox.IsEnabled = enabled;
        TrackBox.IsEnabled = enabled;
        FormatBox.IsEnabled = enabled;
        if (!enabled) StartButton.IsEnabled = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var jobId = _application.CancelCurrentVideo();
        if (jobId is null) return;
        _jobId = jobId;
        var targetItem = _queueItems.FirstOrDefault(item => string.Equals(item.JobId, jobId, StringComparison.Ordinal));
        targetItem?.Apply(_application.Jobs.GetSnapshot(jobId, int.MaxValue));
        UpdateQueueSummary();
    }

    private void CancelQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string jobId }) return;
        var item = _queueItems.FirstOrDefault(candidate => string.Equals(candidate.JobId, jobId, StringComparison.Ordinal));
        if (item is null || item.Done) return;
        _application.CancelJob(jobId);
        item.Apply(_application.Jobs.GetSnapshot(jobId, int.MaxValue));
        UpdateQueueSummary();
    }

    private void ResetSourceForNextItem()
    {
        _metadataUrl = null;
        _metadataTitle = null;
        UrlBox.Text = string.Empty;
        QualityBox.Items.Clear();
        TrackBox.Items.Clear();
        MetadataText.Text = "Đã thêm vào hàng đợi. Nhập URL Bilibili tiếp theo và bấm Kiểm tra.";
        StartButton.IsEnabled = false;
    }

    internal void StopMonitoring() => _queueMonitorCancellation.Cancel();

    private async Task MonitorQueueAsync(CancellationToken cancellationToken)
    {
        if (_monitoringQueue) return;
        _monitoringQueue = true;
        try
        {
            while (true)
            {
                var snapshots = new List<(MediaQueueItem Item, JobSnapshot Snapshot)>();
                foreach (var item in _queueItems)
                {
                    if (item.Done) continue;
                    var snapshot = _application.Jobs.GetSnapshot(item.JobId, int.MaxValue);
                    item.Apply(snapshot);
                    snapshots.Add((item, snapshot));
                }
                // QUEUE-AUTO-REMOVE: mục đã xong (hoàn tất/lỗi/hủy) tự gỡ khỏi
                // danh sách để chừa chỗ cho mục sau; kết quả vẫn nằm ở thư mục
                // lưu và phần trạng thái phía dưới không đổi.
                var finished = _queueItems.Where(item => item.Done).ToList();
                var latestFinished = finished.LastOrDefault();
                foreach (var item in finished)
                {
                    if (string.Equals(item.StatusCode, "error", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.StatusCode, "cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info("Media", $"Gỡ khỏi hàng đợi: {item.Title} · {item.Detail}");
                    }
                    _queueItems.Remove(item);
                }
                if (finished.Count > 0 && _queueItems.Count == 0)
                {
                    QueueList.Visibility = Visibility.Collapsed;
                    QueueEmptyText.Visibility = Visibility.Visible;
                }
                UpdateQueueSummary();

                var current = snapshots.FirstOrDefault(entry =>
                    !entry.Snapshot.Done && !string.Equals(entry.Snapshot.Status, "queued", StringComparison.OrdinalIgnoreCase));
                if (current.Item is null)
                    current = snapshots.FirstOrDefault(entry => !entry.Snapshot.Done);
                if (current.Item is null)
                {
                    _jobId = null;
                    CancelButton.IsEnabled = false;
                    var latest = _queueItems.LastOrDefault() ?? latestFinished;
                    if (latest is not null)
                    {
                        Progress.Value = latest.Progress;
                        PercentText.Text = $"Mục {latest.Number} · {latest.Progress:0}%";
                        StatusText.Text = latest.Detail;
                    }
                    return;
                }

                _jobId = current.Item.JobId;
                // Keep page-local technical log rendering disabled; only read the current snapshot.
                var active = _application.Jobs.GetSnapshot(_jobId, int.MaxValue);
                Progress.Value = Math.Max(0, active.Progress);
                var transport = active.ActiveConnections > 0
                    ? $" · {active.ActiveConnections} kết nối · {active.BytesPerSecond / (1024 * 1024):0.0} MiB/s"
                    : string.Empty;
                var range = active.RangeSupported is null
                    ? string.Empty
                    : $" · Range {(active.RangeSupported == true ? "ON" : "fallback")}";
                PercentText.Text = $"Mục {current.Item.Number} · {active.Progress:0}%{transport}{range}";
                StatusText.Text = active.Message;
                CancelButton.IsEnabled = !active.Done && !string.Equals(active.Status, "cancelling", StringComparison.OrdinalIgnoreCase);
                await Task.Delay(350, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _monitoringQueue = false;
        }
    }

    private void UpdateQueueSummary()
    {
        var waiting = _queueItems.Count(item => !item.Done && string.Equals(item.StatusCode, "queued", StringComparison.OrdinalIgnoreCase));
        var active = _queueItems.Count(item => !item.Done && !string.Equals(item.StatusCode, "queued", StringComparison.OrdinalIgnoreCase));
        var completed = _queueItems.Count(item => item.Done);
        QueueCountText.Text = completed > 0
            ? $"{active} đang tải · {waiting} đang chờ · {completed} đã xong (đã gỡ khỏi danh sách)"
            : $"{active} đang tải · {waiting} đang chờ";
    }
}

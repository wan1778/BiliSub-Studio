using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Diagnostics;
using BiliSubStudio.Core.Video;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class VideoPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly IFolderPickerService _folderPicker;
    private readonly ApplicationLog _log;
    private string? _metadataUrl;
    private string? _jobId;

    public VideoPage(BiliSubApplication application, IFolderPickerService folderPicker, ApplicationLog log)
    {
        _application = application;
        _folderPicker = folderPicker;
        _log = log;
        InitializeComponent();
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
                ChooseOutputButton.IsEnabled = _jobId is null;
            }
            return;
        }

        if (_metadataUrl is null || !string.Equals(_metadataUrl, UrlBox.Text.Trim(), StringComparison.Ordinal))
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

        if (downloadVideo && QualityBox.SelectedItem is null)
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

        var track = TrackBox.SelectedItem as SubtitleTrack;
        Progress.Value = 0;
        PercentText.Text = "0%";

        _jobId = _application.StartVideo(new VideoDownloadRequest(
            _metadataUrl,
            QualityBox.SelectedItem?.ToString() ?? "best",
            ContainerBox.SelectedItem?.ToString() ?? "mp4",
            ModeBox.SelectedItem?.ToString() ?? "video+audio",
            SpeedBox.SelectedItem?.ToString() ?? "fast",
            outputDirectory,
            BundleSubtitleFormat: FormatBox.SelectedItem?.ToString() ?? "srt",
            BundleSubtitleTrack: downloadSubtitle ? track?.Language ?? string.Empty : string.Empty,
            BundleSubtitleIfAvailable: downloadSubtitle,
            BundleThumbnail: downloadThumbnail,
            MediaBundle: true,
            BundleVideo: downloadVideo));

        StartButton.IsEnabled = false;
        LoadMetadataButton.IsEnabled = false;
        ChooseOutputButton.IsEnabled = false;
        VideoAssetCheckBox.IsEnabled = false;
        ThumbnailAssetCheckBox.IsEnabled = false;
        SubtitleAssetCheckBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = hasExplicitSelection
            ? "Đang tải các mục bạn đã chọn..."
            : "Đang tải bộ media đầy đủ...";
        _log.Info("Media", hasExplicitSelection ? "Đã bắt đầu tải các asset được chọn." : "Đã bắt đầu tải bộ Media mặc định.", _jobId);
        await MonitorJobAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is not null) _application.CancelJob(_jobId);
    }

    private async Task MonitorJobAsync()
    {
        while (_jobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_jobId, int.MaxValue);
            Progress.Value = Math.Max(0, snapshot.Progress);

            var transport = snapshot.ActiveConnections > 0
                ? $" · {snapshot.ActiveConnections} kết nối · {snapshot.BytesPerSecond / (1024 * 1024):0.0} MiB/s"
                : string.Empty;
            var range = snapshot.RangeSupported is null
                ? string.Empty
                : $" · Range {(snapshot.RangeSupported == true ? "ON" : "fallback")}";
            PercentText.Text = $"{snapshot.Progress:0}%{transport}{range}";
            StatusText.Text = snapshot.Message;

            if (snapshot.Done)
            {
                CancelButton.IsEnabled = false;
                LoadMetadataButton.IsEnabled = true;
                ChooseOutputButton.IsEnabled = true;
                VideoAssetCheckBox.IsEnabled = true;
                ThumbnailAssetCheckBox.IsEnabled = true;
                SubtitleAssetCheckBox.IsEnabled = true;
                StartButton.IsEnabled = _metadataUrl is not null
                    && !string.IsNullOrWhiteSpace(OutputPathBox.Text);
                _jobId = null;
                return;
            }
            await Task.Delay(350);
        }
    }
}

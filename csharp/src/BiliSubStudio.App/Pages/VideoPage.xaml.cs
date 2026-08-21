using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Video;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class VideoPage : Page
{
    private readonly BiliSubApplication _application;
    private string? _metadataUrl;
    private string? _jobId;
    private int _logOffset;

    public VideoPage(BiliSubApplication application)
    {
        _application = application;
        InitializeComponent();
    }

    public void ApplyConfiguration()
    {
        Select(SpeedBox, _application.Config.VideoSpeed);
        Select(ContainerBox, _application.Config.VideoContainer);
        Select(ModeBox, _application.Config.VideoMode);
        Select(FormatBox, _application.Config.SubtitleFormat);
        OutputText.Text = _application.Config.OutputDirectory;
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
            return;
        }

        try
        {
            LoadMetadataButton.IsEnabled = false;
            StatusText.Text = "Đang đọc thông tin video và phụ đề...";
            var metadata = await _application.GetMetadataAsync(url, CancellationToken.None);

            if (!string.Equals(UrlBox.Text.Trim(), url, StringComparison.Ordinal))
            {
                StatusText.Text = "URL đã đổi trong lúc kiểm tra; kết quả cũ đã bị bỏ.";
                return;
            }

            foreach (var quality in metadata.Qualities) QualityBox.Items.Add(quality);
            if (QualityBox.Items.Count == 0) QualityBox.Items.Add("best");
            QualityBox.SelectedIndex = 0;

            foreach (var track in metadata.Subtitles) TrackBox.Items.Add(track);
            if (TrackBox.Items.Count > 0)
            {
                var preferredIndex = 0;
                var bestRank = int.MaxValue;
                for (var index = 0; index < metadata.Subtitles.Count; index++)
                {
                    var track = metadata.Subtitles[index];
                    var chinese = track.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                        || track.Language.Contains("chi", StringComparison.OrdinalIgnoreCase);
                    var rank = track.Official && chinese ? 0 : chinese ? 1 : track.Official ? 2 : 3;
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        preferredIndex = index;
                    }
                }
                TrackBox.SelectedIndex = preferredIndex;
            }

            _metadataUrl = url;
            MetadataText.Text = $"{metadata.Title} · {QualityBox.Items.Count} mức chất lượng · {TrackBox.Items.Count} track phụ đề";
            StartButton.IsEnabled = TrackBox.SelectedIndex >= 0;
            StatusText.Text = StartButton.IsEnabled
                ? "Nguồn hợp lệ. Sẵn sàng tải video + phụ đề trong cùng một tác vụ."
                : "Nguồn không có track phụ đề; chưa thể chạy tác vụ media chung.";
        }
        catch (Exception error)
        {
            MetadataText.Text = "Không đọc được metadata; quality/track cũ đã bị xóa.";
            StatusText.Text = error.Message;
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
            MetadataText.Text = "URL đã đổi; cần Kiểm tra lại video + phụ đề.";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_metadataUrl is null || !string.Equals(_metadataUrl, UrlBox.Text.Trim(), StringComparison.Ordinal)) return;
        if (QualityBox.SelectedItem is null || TrackBox.SelectedItem is not SubtitleTrack track)
        {
            StatusText.Text = "Cần quality video và track phụ đề hợp lệ.";
            return;
        }

        _logOffset = 0;
        LogBox.Text = string.Empty;
        Progress.Value = 0;
        PercentText.Text = "0%";

        _jobId = _application.StartVideo(new VideoDownloadRequest(
            _metadataUrl,
            QualityBox.SelectedItem.ToString() ?? "best",
            ContainerBox.SelectedItem?.ToString() ?? "mp4",
            ModeBox.SelectedItem?.ToString() ?? "video+audio",
            SpeedBox.SelectedItem?.ToString() ?? "fast",
            _application.Config.OutputDirectory,
            BundleSubtitleFormat: FormatBox.SelectedItem?.ToString() ?? "srt",
            BundleSubtitleTrack: track.Language));

        StartButton.IsEnabled = false;
        LoadMetadataButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
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
            var snapshot = _application.Jobs.GetSnapshot(_jobId, _logOffset);
            _logOffset = snapshot.LogNext;
            if (snapshot.Logs.Count > 0) LogBox.Text += string.Join("\n", snapshot.Logs) + "\n";
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
                StartButton.IsEnabled = _metadataUrl is not null
                    && QualityBox.SelectedItem is not null
                    && TrackBox.SelectedItem is SubtitleTrack;
                _jobId = null;
                return;
            }
            await Task.Delay(350);
        }
    }
}

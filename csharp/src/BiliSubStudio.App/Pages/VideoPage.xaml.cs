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
    }

    private static void Select(ComboBox box, string value)
    {
        for (var index = 0; index < box.Items.Count; index++)
            if (string.Equals(box.Items[index]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = index; return; }
    }

    private async void LoadMetadata_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadMetadataButton.IsEnabled = false;
            StatusText.Text = "Đang lấy metadata...";
            var url = UrlBox.Text.Trim();
            var metadata = await _application.GetMetadataAsync(url, CancellationToken.None);
            QualityBox.Items.Clear();
            foreach (var quality in metadata.Qualities) QualityBox.Items.Add(quality);
            QualityBox.SelectedIndex = 0;
            _metadataUrl = url;
            MetadataText.Text = $"{metadata.Title} · {metadata.Qualities.Count} mức chất lượng";
            StartButton.IsEnabled = true;
            StatusText.Text = "Metadata đã sẵn sàng.";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { LoadMetadataButton.IsEnabled = true; }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(UrlBox.Text.Trim(), _metadataUrl, StringComparison.Ordinal))
        {
            _metadataUrl = null;
            StartButton.IsEnabled = false;
            MetadataText.Text = "URL đã đổi; cần lấy lại metadata.";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_metadataUrl is null || _metadataUrl != UrlBox.Text.Trim()) return;
        _logOffset = 0;
        LogBox.Text = string.Empty;
        _jobId = _application.StartVideo(new VideoDownloadRequest(
            _metadataUrl,
            QualityBox.SelectedItem?.ToString() ?? "best",
            ContainerBox.SelectedItem?.ToString() ?? "mp4",
            ModeBox.SelectedItem?.ToString() ?? "video+audio",
            SpeedBox.SelectedItem?.ToString() ?? "fast",
            _application.Config.OutputDirectory));
        StartButton.IsEnabled = false;
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
            PercentText.Text = snapshot.RangeSupported is null ? $"{snapshot.Progress:0}%" : $"{snapshot.Progress:0}% · Range {(snapshot.RangeSupported == true ? "ON" : "fallback")}";
            StatusText.Text = snapshot.Message;
            if (snapshot.Done)
            {
                CancelButton.IsEnabled = false;
                StartButton.IsEnabled = _metadataUrl is not null;
                _jobId = null;
                return;
            }
            await Task.Delay(350);
        }
    }
}

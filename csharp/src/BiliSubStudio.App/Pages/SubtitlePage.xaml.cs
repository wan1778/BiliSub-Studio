using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Subtitle;
using BiliSubStudio.Core.Video;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class SubtitlePage : Page
{
    private readonly BiliSubApplication _application;
    private string? _metadataUrl;
    private string? _jobId;
    private int _logOffset;

    public SubtitlePage(BiliSubApplication application) { _application = application; InitializeComponent(); }

    public void ApplyConfiguration()
    {
        for (var index = 0; index < FormatBox.Items.Count; index++)
            if (string.Equals(FormatBox.Items[index]?.ToString(), _application.Config.SubtitleFormat, StringComparison.OrdinalIgnoreCase)) { FormatBox.SelectedIndex = index; break; }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadButton.IsEnabled = false;
            StatusText.Text = "Đang lấy danh sách track...";
            var url = UrlBox.Text.Trim();
            var metadata = await _application.GetMetadataAsync(url, CancellationToken.None);
            TrackBox.Items.Clear();
            foreach (var track in metadata.Subtitles) TrackBox.Items.Add(track);
            TrackBox.SelectedIndex = metadata.Subtitles.Count > 0 ? 0 : -1;
            _metadataUrl = url;
            TitleText.Text = $"{metadata.Title} · {metadata.Subtitles.Count} track";
            StartButton.IsEnabled = metadata.Subtitles.Count > 0;
        }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { LoadButton.IsEnabled = true; }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_metadataUrl != UrlBox.Text.Trim()) { _metadataUrl = null; StartButton.IsEnabled = false; TitleText.Text = "URL đã đổi; cần lấy lại track."; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_metadataUrl is null || TrackBox.SelectedItem is not SubtitleTrack track) return;
        _jobId = _application.StartSubtitle(new SubtitleRequest(_metadataUrl, FormatBox.SelectedItem?.ToString() ?? "srt", track.Language, _application.Config.OutputDirectory));
        _logOffset = 0; LogBox.Text = string.Empty; StartButton.IsEnabled = false; CancelButton.IsEnabled = true;
        while (_jobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_jobId, _logOffset); _logOffset = snapshot.LogNext;
            if (snapshot.Logs.Count > 0) LogBox.Text += string.Join("\n", snapshot.Logs) + "\n";
            Progress.Value = snapshot.Progress; StatusText.Text = snapshot.Message;
            if (snapshot.Done) { _jobId = null; CancelButton.IsEnabled = false; StartButton.IsEnabled = _metadataUrl is not null; break; }
            await Task.Delay(350);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_jobId is not null) _application.CancelJob(_jobId); }
}

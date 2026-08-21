using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Maintenance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class SupportPage : Page
{
    private readonly BiliSubApplication _application;
    private bool _updateAvailable;
    private bool _updateBusy;
    private bool _reportBusy;
    public SupportPage(BiliSubApplication application) { _application = application; InitializeComponent(); }
    public void ApplyUpdateInfo(UpdateInfo info)
    {
        _updateAvailable = info.Available && info.ChannelReady;
        UpdateText.Text = $"Hiện tại {info.Current} · kênh {info.Latest} · {info.Message}\n{string.Join("\n", info.Notes)}";
        SyncControls();
    }
    public void ApplyUpdateError(string message) { _updateAvailable = false; UpdateText.Text = message; SyncControls(); }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) { try { _updateBusy = true; SyncControls(); UpdateText.Text = "Đang kiểm tra kênh cập nhật..."; ApplyUpdateInfo(await _application.Updates.CheckAsync(CancellationToken.None)); } catch (Exception error) { ApplyUpdateError(error.Message); } finally { _updateBusy = false; SyncControls(); } }
    private async void PrepareUpdate_Click(object sender, RoutedEventArgs e) { if (!_updateAvailable) return; try { _updateBusy = true; SyncControls(); UpdateText.Text = "Đang tải và xác minh gói cập nhật..."; var prepared = await _application.PrepareUpdateAsync(CancellationToken.None); _updateAvailable = false; UpdateText.Text = $"Đã xác minh và staging {prepared.Version}. Đóng ứng dụng để cập nhật an toàn."; } catch (Exception error) { UpdateText.Text = error.Message; } finally { _updateBusy = false; SyncControls(); } }
    private async void SendReport_Click(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(NoteBox.Text)) return; try { _reportBusy = true; SyncControls(); ReportStatus.Text = "Đang sanitize và gửi báo lỗi..."; var logs = _application.Jobs.ActiveSnapshots().ToDictionary(x => x.Id, x => string.Join("\n", x.Logs), StringComparer.Ordinal); await _application.BugReports.SendAsync(_application.Updates.CurrentVersion, PageBox.SelectedItem?.ToString() ?? "General", NoteBox.Text, logs, CancellationToken.None); NoteBox.Text = string.Empty; ReportStatus.Text = "Đã gửi báo lỗi."; } catch (Exception error) { ReportStatus.Text = error.Message; } finally { _reportBusy = false; SyncControls(); } }

    private void NoteBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) SyncControls(); }

    private void SyncControls()
    {
        CheckButton.IsEnabled = !_updateBusy;
        PrepareButton.IsEnabled = !_updateBusy && _updateAvailable;
        UpdateProgress.IsIndeterminate = _updateBusy;
        SendButton.IsEnabled = !_reportBusy && !string.IsNullOrWhiteSpace(NoteBox.Text);
        PageBox.IsEnabled = !_reportBusy;
        NoteBox.IsEnabled = !_reportBusy;
    }
}

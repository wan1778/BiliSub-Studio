using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Diagnostics;
using BiliSubStudio.Core.Maintenance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class SupportPage : Page
{
    private readonly BiliSubApplication _application;
    private readonly ApplicationLog _log;
    private bool _updateAvailable;
    private bool _updateBusy;
    private bool _reportBusy;

    public SupportPage(BiliSubApplication application, ApplicationLog log)
    {
        _application = application;
        _log = log;
        InitializeComponent();
    }

    public void ApplyUpdateInfo(UpdateInfo info)
    {
        _updateAvailable = info.Available && info.ChannelReady;
        UpdateText.Text = $"Hiện tại {info.Current} · kênh {info.Latest} · {info.Message}\n{string.Join("\n", info.Notes)}";
        SyncControls();
    }

    public void ApplyUpdateError(string message)
    {
        _updateAvailable = false;
        UpdateText.Text = message;
        SyncControls();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _updateBusy = true;
            SyncControls();
            UpdateText.Text = "Đang kiểm tra kênh cập nhật...";
            _log.Info("Cập nhật", "Đang kiểm tra kênh cập nhật thủ công.");
            var info = await _application.Updates.CheckAsync(CancellationToken.None);
            ApplyUpdateInfo(info);
            _log.Info("Cập nhật", info.Message);
        }
        catch (Exception error)
        {
            ApplyUpdateError(error.Message);
            _log.Error("Cập nhật", "Kiểm tra cập nhật lỗi: " + error.Message);
        }
        finally
        {
            _updateBusy = false;
            SyncControls();
        }
    }

    private async void PrepareUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!_updateAvailable) return;
        try
        {
            _updateBusy = true;
            SyncControls();
            UpdateText.Text = "Đang tải và xác minh gói cập nhật...";
            _log.Info("Cập nhật", "Đang tải và xác minh gói cập nhật.");
            var prepared = await _application.PrepareUpdateAsync(CancellationToken.None);
            _updateAvailable = false;
            UpdateText.Text = $"Đã xác minh và staging {prepared.Version}. Đóng ứng dụng để cập nhật an toàn.";
            _log.Info("Cập nhật", $"Đã xác minh và staging {prepared.Version}.");
        }
        catch (Exception error)
        {
            UpdateText.Text = error.Message;
            _log.Error("Cập nhật", "Chuẩn bị cập nhật lỗi: " + error.Message);
        }
        finally
        {
            _updateBusy = false;
            SyncControls();
        }
    }

    private async void SendReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NoteBox.Text)) return;
        try
        {
            _reportBusy = true;
            SyncControls();
            ReportStatus.Text = "Đang sanitize và gửi báo lỗi...";
            _log.Info("Hỗ trợ", "Đang chuẩn bị báo lỗi cùng nhật ký toàn ứng dụng.");

            var logs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["application"] = string.Join("\n", _log.Snapshot().TakeLast(500).Select(entry =>
                    $"{entry.Timestamp:HH:mm:ss} [{entry.Level}] [{entry.Source}] {entry.Message}")),
            };
            foreach (var snapshot in _application.Jobs.ActiveSnapshots())
                logs[$"job:{snapshot.Id}"] = string.Join("\n", snapshot.Logs);

            await _application.BugReports.SendAsync(
                _application.Updates.CurrentVersion,
                PageBox.SelectedItem?.ToString() ?? "General",
                NoteBox.Text,
                logs,
                CancellationToken.None);
            NoteBox.Text = string.Empty;
            ReportStatus.Text = "Đã gửi báo lỗi.";
            _log.Info("Hỗ trợ", "Đã gửi báo lỗi sau khi sanitize.");
        }
        catch (Exception error)
        {
            ReportStatus.Text = error.Message;
            _log.Error("Hỗ trợ", "Gửi báo lỗi thất bại: " + error.Message);
        }
        finally
        {
            _reportBusy = false;
            SyncControls();
        }
    }

    private void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) SyncControls();
    }

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

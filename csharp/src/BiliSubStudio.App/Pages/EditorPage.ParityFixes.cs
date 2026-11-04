using BiliSubStudio.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private TextBox? _editorOutputPathText;
    private Button? _editorChooseOutputButton;
    private Button? _editorOpenOutputButton;
    private ToggleSwitch? _editorAutoCompositeToggle;
    private CancellationTokenSource? _editorAutoCompositeCancellation;
    private bool _editorAutoCompositeRebuilding;

    private void PublishPresenceContext(string? video, string? project)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current is BiliSubStudio.App.App app
                && app.MainWindow is BiliSubStudio.App.MainWindow window)
                window.SetPresenceContext(video, project);
        }
        catch (Exception error)
        {
            BiliSubStudio.App.Services.StartupDiagnostics.WriteException("editor-presence-context", error);
        }
    }

    private async void EditorChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as BiliSubStudio.App.App
                ?? throw new InvalidOperationException("Không lấy được cửa sổ chính.");
            var window = app.MainWindow ?? throw new InvalidOperationException("Cửa sổ chính chưa sẵn sàng.");
            var picker = new FolderPickerService(() => window);
            var path = await picker.PickFolderAsync(_application.Config.EditorOutputDirectory);
            if (string.IsNullOrWhiteSpace(path)) return;
            await _application.Settings.SetEditorOutputDirectoryAsync(path, CancellationToken.None);
            if (_editorOutputPathText is not null) _editorOutputPathText.Text = _application.Config.EditorOutputDirectory;
            StatusText.Text = "Đã đổi nơi lưu Editor: " + _application.Config.EditorOutputDirectory;
            RefreshEditorActions();
        }
        catch (Exception error)
        {
            StatusText.Text = "Không đổi được nơi lưu: " + error.Message;
        }
    }

    private async void EditorOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as BiliSubStudio.App.App
                ?? throw new InvalidOperationException("Không lấy được cửa sổ chính.");
            var window = app.MainWindow ?? throw new InvalidOperationException("Cửa sổ chính chưa sẵn sàng.");
            var picker = new FolderPickerService(() => window);
            // Open exactly the folder currently shown in the export dialog
            // (before Start is pressed it is just a draft); if empty, fall back to
            // the output location saved in Config.
            var shown = ExportDialogDirectoryBox.Text?.Trim() ?? string.Empty;
            await picker.OpenFolderAsync(shown.Length > 0 ? shown : _application.Config.EditorOutputDirectory);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được nơi lưu: " + error.Message;
        }
    }

    private void NotifyEditorCompositeChanged()
    {
        RefreshEditorParityControls();
        QueueEditorCompositeRefresh();
    }

    private void SetAutoCompositeBusyText(string status)
    {
        if (AutoCompositeBusyText is null) return;
        AutoCompositeBusyText.Text = status;
        AutoCompositeBusyText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _voiceAdvancedExpanded;

    private void VoiceAdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        _voiceAdvancedExpanded = !_voiceAdvancedExpanded;
        if (VoiceAdvancedPanel is not null)
            VoiceAdvancedPanel.Visibility = _voiceAdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (VoiceAdvancedToggleButton is not null)
            VoiceAdvancedToggleButton.Content = _voiceAdvancedExpanded ? "Ẩn nâng cao · nhận dạng nhịp" : "Nâng cao · nhận dạng nhịp";
    }

    private void EditorAutoComposite_Toggled(object sender, RoutedEventArgs e)
    {
        if (_editorAutoCompositeToggle?.IsOn != true)
        {
            _editorAutoCompositeCancellation?.Cancel();
            _editorAutoCompositeCancellation?.Dispose();
            _editorAutoCompositeCancellation = null;
        }
        RefreshEditorParityControls();
    }

    private void QueueEditorCompositeRefresh()
    {
        if (_editorAutoCompositeToggle?.IsOn != true || !_playback.IsPreviewMode || _playback.IsRendering || _editorAutoCompositeRebuilding) return;
        _editorAutoCompositeCancellation?.Cancel();
        _editorAutoCompositeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _editorAutoCompositeCancellation = cancellation;
        _ = RebuildEditorCompositePreviewAsync(cancellation);
    }

    private async Task RebuildEditorCompositePreviewAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(320, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_playback.IsPreviewMode || _media is null) return;
            var resumePlayback = _playback.IsPlaying;
            var sourcePosition = Math.Clamp(Timeline.Value, Timeline.Minimum, Timeline.Maximum);
            _editorAutoCompositeRebuilding = true;
            StatusText.Text = "Đang cập nhật bản xem trước theo thay đổi mới...";
            SetAutoCompositeBusyText("Đang cập nhật bản xem trước theo thay đổi mới...");
            await _playback.SetModeAsync(enabled: false, play: false);
            cancellation.Token.ThrowIfCancellationRequested();
            _syncingTimeline = true;
            try { Timeline.Value = sourcePosition; }
            finally { _syncingTimeline = false; }
            await _playback.SetModeAsync(enabled: true, play: resumePlayback);
            cancellation.Token.ThrowIfCancellationRequested();
            StatusText.Text = "Bản xem trước đã cập nhật.";
            SetAutoCompositeBusyText("Bản xem trước đã cập nhật.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không tự cập nhật được preview: " + error.Message;
            SetAutoCompositeBusyText("Không tự cập nhật được preview: " + error.Message);
        }
        finally
        {
            _editorAutoCompositeRebuilding = false;
            if (ReferenceEquals(_editorAutoCompositeCancellation, cancellation)) _editorAutoCompositeCancellation = null;
            cancellation.Dispose();
            RefreshEditorParityControls();
        }
    }

    private void RefreshEditorParityControls()
    {
        if (_editorCoreInitialized is false) return;
        if (_editorOutputPathText is not null) _editorOutputPathText.Text = _application.Config.EditorOutputDirectory;
        if (_editorChooseOutputButton is not null) _editorChooseOutputButton.IsEnabled = !EditorBusy && !_playback.IsPreviewMode;
        if (_editorOpenOutputButton is not null) _editorOpenOutputButton.IsEnabled = Directory.Exists(_application.Config.EditorOutputDirectory);
        if (_editorAutoCompositeToggle is not null) _editorAutoCompositeToggle.IsEnabled = !EditorBusy;

    }

    private void CleanupEditorParity()
    {
        // CLEAN-01: EditorPage_Unloaded is the only Unloaded event owner.
        // Progress/voice cleanup is a subordinate lifecycle operation, never a second event subscription.
        CleanupEditorProgress();
        CleanupVoiceCuePreview();
        _editorAutoCompositeCancellation?.Cancel();
        _editorAutoCompositeCancellation?.Dispose();
        _editorAutoCompositeCancellation = null;
        _editorAutoCompositeRebuilding = false;
    }
}

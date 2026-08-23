using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _toolTransitionRepairInitialized;

    private void EnsureToolTransitionRepairInitialized()
    {
        if (_toolTransitionRepairInitialized) return;
        _toolTransitionRepairInitialized = true;

        SubtitleModeButton.Click += EditorToolMode_Click;
        BlurModeButton.Click += EditorToolMode_Click;
        AudioModeButton.Click += EditorToolMode_Click;
        ExportModeButton.Click += EditorToolMode_Click;
        if (_imageModeButton is not null) _imageModeButton.Click += EditorToolMode_Click;
    }

    private async void EditorToolMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_playerMode || _continuousPreviewTransition) return;
        try
        {
            await SetPlaybackModeAsync(enabled: false, play: false);
            UpdateCompactPlayState(false);
            RefreshEditorActions();
            RefreshEditorParityControls();
            RefreshImageControls();
            RenderOverlays();
            RenderImageOverlays();
            StatusText.Text = "Đã dừng tại frame hiện tại để chỉnh trực tiếp trên Preview.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng Preview để chuyển công cụ.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Không chuyển được sang chế độ chỉnh: " + error.Message;
        }
    }
}

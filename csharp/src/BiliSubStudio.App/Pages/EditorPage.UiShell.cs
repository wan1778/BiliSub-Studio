using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    // Legacy code still references these names, but they are no longer part of the visual tree.
    // The user-facing Player owns the real controls in EditorPage.xaml.
    private readonly Button PlaybackButton = new() { Content = "Xem bản chỉnh" };
    private readonly Button RefreshFrameButton = new();
    private readonly Canvas RegionTimelineCanvas = new();

    private void BindStaticUiShell()
    {
        // Parity controls are declared in XAML; no Parent walking or runtime insertion.
        _editorParityInitialized = true;
        _editorOutputPathText = EditorOutputPathText;
        _editorUseCurrentStartButton = EditorUseCurrentStartButton;
        _editorUseCurrentEndButton = EditorUseCurrentEndButton;
        _editorChooseOutputButton = EditorChooseOutputButton;
        _editorOpenOutputButton = EditorOpenOutputButton;
        _editorAutoCompositeToggle = EditorAutoCompositeToggle;
        EditorOutputPathText.Text = _application.Config.OutputDirectory;

        // Image/Logo controls and overlay are also native XAML elements.
        _imageFeatureInitialized = true;
        _imageModeButton = ImageModeButton;
        _imageInspectorPanel = ImageInspectorPanel;
        _imageOverlayCanvas = ImageOverlayCanvas;
        _addImageButton = AddImageButton;
        _removeImageButton = RemoveImageButton;
        _imageTopRightButton = ImageTopRightButton;
        _imageTopLeftButton = ImageTopLeftButton;
        _imageList = ImageSourceList;
        _imageStatusText = ImageStatusText;
        _imageXBox = ImageXBox;
        _imageYBox = ImageYBox;
        _imageWidthBox = ImageWidthBox;
        _imageHeightBox = ImageHeightBox;
        _imageOpacitySlider = ImageOpacitySlider;

        LayoutUpdated += EditorUiShell_LayoutUpdated;
        SelectShellTool("Subtitle");
        RefreshImageControls();
        RefreshEditorParityControls();
    }

    private void ShellTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        SelectShellTool(tag);
    }

    private void SelectShellTool(string tag)
    {
        var subtitle = string.Equals(tag, "Subtitle", StringComparison.OrdinalIgnoreCase);
        var blur = string.Equals(tag, "Blur", StringComparison.OrdinalIgnoreCase);
        var audio = string.Equals(tag, "Audio", StringComparison.OrdinalIgnoreCase);
        var voice = string.Equals(tag, "Voice", StringComparison.OrdinalIgnoreCase);
        var image = string.Equals(tag, "Image", StringComparison.OrdinalIgnoreCase);
        var export = string.Equals(tag, "Export", StringComparison.OrdinalIgnoreCase);
        if (!subtitle && !blur && !audio && !voice && !image && !export) return;

        // Core editing state stays authoritative. Voice shares the non-ROI Audio core mode;
        // the six-way shell only decides which Details panel is visible.
        _inspectorMode = subtitle ? InspectorMode.Subtitle
            : blur ? InspectorMode.Blur
            : image ? InspectorMode.Image
            : export ? InspectorMode.Export
            : InspectorMode.Audio;

        SubtitleModeButton.IsChecked = subtitle;
        BlurModeButton.IsChecked = blur;
        AudioModeButton.IsChecked = audio;
        VoiceModeButton.IsChecked = voice;
        ImageModeButton.IsChecked = image;
        ExportModeButton.IsChecked = export;

        SubtitleInspectorPanel.Visibility = subtitle ? Visibility.Visible : Visibility.Collapsed;
        BlurInspectorPanel.Visibility = blur ? Visibility.Visible : Visibility.Collapsed;
        AudioInspectorPanel.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        VoiceInspectorPanel.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
        ImageInspectorPanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        ExportInspectorPanel.Visibility = export ? Visibility.Visible : Visibility.Collapsed;

        RenderOverlays();
        RenderImageOverlays();
        RefreshEditorActions();
        SyncShellPlayerControls();
    }

    private async void PlayerPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_playerMode && _player is not null)
            {
                if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
                else _player.Play();
            }
            else
            {
                await SetPlaybackModeAsync(enabled: true, play: true);
            }
            SyncShellPlayerControls();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng tạo bản xem trước.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Preview bản chỉnh: " + error.Message;
        }
    }

    private void EditorUiShell_LayoutUpdated(object? sender, object e)
    {
        SyncShellPlayerControls();
        RenderImageOverlays();
    }

    private void SyncShellPlayerControls()
    {
        PlayerPlayPauseButton.IsEnabled = PlaybackButton.IsEnabled;
        PlayerPlayPauseButton.Content = _playerMode && _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "⏸" : "▶";
        PreviewMuteToggle.Content = PreviewMuteToggle.IsOn ? "🔇" : "🔊";
    }
}

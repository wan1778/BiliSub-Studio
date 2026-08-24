using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorCoreInitialized;

    // Compatibility objects only. They are never attached to the visual tree.
    private readonly Button RefreshFrameButton = new();
    private readonly Canvas RegionTimelineCanvas = new();

    private void EditorPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_editorCoreInitialized)
        {
            BindStaticUiShell();
            _editorCoreInitialized = true;
        }

        RefreshEditorActions();
        RefreshImageControls();
        RefreshEditorParityControls();
        SyncShellPlayerControls();
    }

    void BindStaticUiShell()
    {
        // UI-02: every user-facing shell element already exists in EditorPage.xaml.
        // This method binds state/events only; it never reparents or inserts controls.
        _editorParityInitialized = true;
        _imageFeatureInitialized = true;

        // Preserve the CORE lifecycle contract while preventing the retired dynamic
        // builders from running: both guarded methods return immediately when the
        // corresponding initialized flag is already true.
        EnsureEditorParityInitialized();
        EnsureImageFeatureInitialized();

        _editorOutputPathText = EditorOutputPathText;
        _editorUseCurrentStartButton = EditorUseCurrentStartButton;
        _editorUseCurrentEndButton = EditorUseCurrentEndButton;
        _editorChooseOutputButton = EditorChooseOutputButton;
        _editorOpenOutputButton = EditorOpenOutputButton;
        _editorAutoCompositeToggle = EditorAutoCompositeToggle;
        EditorOutputPathText.Text = _application.Config.OutputDirectory;

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

        // One navigation owner for all six tools; no per-tool ownership is reintroduced.
        foreach (var toolButton in new[]
        {
            SubtitleModeButton,
            BlurModeButton,
            AudioModeButton,
            VoiceModeButton,
            ImageModeButton,
            ExportModeButton,
        })
        {
            toolButton.Click += ShellTool_Click;
        }
        ImageSourceList.SelectionChanged += ImageList_SelectionChanged;
        ImageOverlayCanvas.PointerPressed += ImageOverlay_PointerPressed;
        ImageOverlayCanvas.PointerMoved += ImageOverlay_PointerMoved;
        ImageOverlayCanvas.PointerReleased += ImageOverlay_PointerReleased;
        ImageOverlayCanvas.PointerCanceled += ImageOverlay_PointerCanceled;
        ImageOverlayCanvas.SizeChanged += ImageOverlay_SizeChanged;
        AddImageButton.Click += AddImage_Click;
        RemoveImageButton.Click += RemoveImage_Click;
        ImageTopLeftButton.Click += ImageTopLeft_Click;
        ImageTopRightButton.Click += ImageTopRight_Click;
        ImageXBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageYBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageWidthBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageHeightBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageOpacitySlider.ValueChanged += ImageOpacity_ValueChanged;

        EditorAutoCompositeToggle.Toggled += EditorAutoComposite_Toggled;
        EditorUseCurrentStartButton.Click += EditorUseCurrentStart_Click;
        EditorUseCurrentEndButton.Click += EditorUseCurrentEnd_Click;
        EditorChooseOutputButton.Click += EditorChooseOutput_Click;
        EditorOpenOutputButton.Click += EditorOpenOutput_Click;

        // UI-11: MainWindow startup smoke resizes to 800x600, 1000x700 and 1500x900.
        // Validate the real shell at those layouts without affecting normal user resize.
        var layoutSmoke = Environment.GetCommandLineArgs()
            .Any(arg => arg.StartsWith("--startup-smoke-test=", StringComparison.OrdinalIgnoreCase));
        if (layoutSmoke) WorkspaceGrid.SizeChanged += (_, _) => ValidateUiShellLayoutForSmoke();

        SelectShellTool("Subtitle");
        RefreshImageControls();
        RefreshEditorParityControls();
    }

    void ShellTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) SelectShellTool(tag);
    }

    void SelectShellTool(string tag)
    {
        var subtitle = string.Equals(tag, "Subtitle", StringComparison.OrdinalIgnoreCase);
        var blur = string.Equals(tag, "Blur", StringComparison.OrdinalIgnoreCase);
        var audio = string.Equals(tag, "Audio", StringComparison.OrdinalIgnoreCase);
        var voice = string.Equals(tag, "Voice", StringComparison.OrdinalIgnoreCase);
        var image = string.Equals(tag, "Image", StringComparison.OrdinalIgnoreCase);
        var export = string.Equals(tag, "Export", StringComparison.OrdinalIgnoreCase);
        if (!subtitle && !blur && !audio && !voice && !image && !export) return;

        // Core ROI state remains authoritative. Audio and Voice are separate Details
        // tools but both are intentionally non-ROI core modes.
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

    void SyncShellPlayerControls()
    {
        PlayerPlayPauseButton.Content = _playback.IsPlaying ? "⏸" : "▶";
        PreviewMuteToggle.OffContent = "🔊";
        PreviewMuteToggle.OnContent = "🔇";
    }

    void ValidateUiShellLayoutForSmoke()
    {
        if (WorkspaceGrid.ActualWidth < 500 || WorkspaceGrid.ActualHeight < 250) return;
        if (WorkspaceGrid.ColumnDefinitions.Count != 3)
            throw new InvalidOperationException("Editor shell phải có đúng ba cột Source / Player / Details.");
        if (SourceColumn.ActualWidth < 110)
            throw new InvalidOperationException($"Source quá hẹp: {SourceColumn.ActualWidth:0}px.");
        if (PlayerColumn.ActualWidth < 210)
            throw new InvalidOperationException($"Player quá hẹp: {PlayerColumn.ActualWidth:0}px.");
        if (DetailsColumn.ActualWidth < 180)
            throw new InvalidOperationException($"Details quá hẹp: {DetailsColumn.ActualWidth:0}px.");
        if (PreviewSurface.ActualWidth <= 0 || PreviewSurface.ActualHeight <= 0)
            throw new InvalidOperationException("Player không có diện tích preview.");
        if (PlayerControlBar.ActualHeight > 0 && (PlayerControlBar.ActualHeight < 40 || PlayerControlBar.ActualHeight > 56))
            throw new InvalidOperationException($"Thanh Player sai chiều cao: {PlayerControlBar.ActualHeight:0}px.");
        if (PreviewPlayer.AreTransportControlsEnabled)
            throw new InvalidOperationException("Transport WinUI mặc định đã xuất hiện lại trong Editor.");

        var visiblePanels = new[]
        {
            SubtitleInspectorPanel,
            BlurInspectorPanel,
            AudioInspectorPanel,
            VoiceInspectorPanel,
            ImageInspectorPanel,
            ExportInspectorPanel,
        }.Count(panel => panel.Visibility == Visibility.Visible);
        if (visiblePanels != 1)
            throw new InvalidOperationException($"Details đang hiện {visiblePanels} tool panel thay vì đúng một panel.");
    }
}

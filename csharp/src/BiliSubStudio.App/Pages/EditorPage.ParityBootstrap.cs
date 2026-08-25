using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorCoreInitialized;
    private DispatcherQueueTimer? _editorExportProgressTimer;
    private string? _observedImageProgressJobId;
    private double _imageStageProgressFloor;
    private double _imageStageDisplayProgress;
    private readonly SemaphoreSlim _editorTabLifecycleGate = new(1, 1);

    private async void EditorPage_Loaded(object sender, RoutedEventArgs e)
    {
        await _editorTabLifecycleGate.WaitAsync();
        try
        {
            if (!IsLoaded) return;
            if (!_editorCoreInitialized)
            {
                BindStaticUiShell();
                _editorCoreInitialized = true;
            }

            EnsureEditorExportProgressTimer();
            StartVoiceArtifactMonitor();
            if (_path is not null && _media is not null && !_playback.IsReady)
            {
                try
                {
                    await _playback.PrepareAsync();
                    if (!IsLoaded)
                    {
                        await _playback.UnloadAsync();
                        return;
                    }
                    await UpdateFrameAsync();
                    RenderOverlays();
                    RenderImageOverlays();
                }
                catch (OperationCanceledException)
                {
                    if (IsLoaded) StatusText.Text = "Đã dừng khôi phục preview khi mở lại tab Chỉnh video.";
                }
                catch (Exception error)
                {
                    if (IsLoaded) StatusText.Text = "Không khôi phục được preview khi mở lại tab: " + error.Message;
                }
            }
            RefreshEditorActions();
            RefreshImageControls();
            RefreshEditorParityControls();
            SyncShellPlayerControls();
        }
        finally
        {
            _editorTabLifecycleGate.Release();
        }
    }

    void BindStaticUiShell()
    {
        // UI-02: every user-facing shell element already exists in EditorPage.xaml.
        // CLEAN-08: _editorCoreInitialized is the only stored shell-initialization state.
        // This method binds state/events only; it never reparents or inserts controls.

        _editorOutputPathText = EditorOutputPathText;
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
        RemoveImageButton.Click += RemoveImageSafe_Click;
        ImageTopLeftButton.Click += ImageCornerPreset_Click;
        ImageTopRightButton.Click += ImageCornerPreset_Click;
        ImageXBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageYBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageWidthBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageHeightBox.ValueChanged += ImageGeometry_ValueChanged;
        ImageOpacitySlider.ValueChanged += ImageOpacity_ValueChanged;

        EditorAutoCompositeToggle.Toggled += EditorAutoComposite_Toggled;
        EditorChooseOutputButton.Click += EditorChooseOutput_Click;
        EditorOpenOutputButton.Click += EditorOpenOutput_Click;
        FileNameBox.LostFocus += EditorFileName_LostFocus;

        // UI-11: MainWindow startup smoke resizes to 800x600, 1000x700 and 1500x900.
        // Validate the real shell at those layouts without affecting normal user resize.
        var layoutSmoke = Environment.GetCommandLineArgs()
            .Any(arg => arg.StartsWith("--startup-smoke-test=", StringComparison.OrdinalIgnoreCase));
        if (layoutSmoke) WorkspaceGrid.SizeChanged += ValidateUiShellLayoutForSmoke;

        SelectShellTool("Subtitle");
        RefreshImageControls();
        RefreshEditorParityControls();
    }

    private void EnsureEditorExportProgressTimer()
    {
        if (_editorExportProgressTimer is null)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(150);
            timer.Tick += EditorExportProgressTimer_Tick;
            _editorExportProgressTimer = timer;
        }
        _editorExportProgressTimer.Start();
    }

    private void EditorExportProgressTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var jobId = _jobId;
        if (string.IsNullOrWhiteSpace(jobId)) return;
        if (!_application.Jobs.TryGet(jobId, out var job) || job is null) return;
        var snapshot = job.Snapshot();
        if (!string.Equals(snapshot.Kind, "editor-image", StringComparison.Ordinal)) return;

        if (!string.Equals(_observedImageProgressJobId, jobId, StringComparison.Ordinal))
        {
            _observedImageProgressJobId = jobId;
            var hasBaseEdit = _document.Regions.Count > 0
                || CompletedSubtitleBurn() is not null
                || _audioSettings.SourceMode != "keep"
                || _voiceTrack is not null;
            _imageStageProgressFloor = hasBaseEdit ? 68d : 0d;
            _imageStageDisplayProgress = _imageStageProgressFloor;
            Progress.Value = _imageStageProgressFloor;
        }

        var childProgress = Math.Clamp(snapshot.Progress, 0, 99);
        var mapped = _imageStageProgressFloor
            + (99d - _imageStageProgressFloor) * childProgress / 99d;
        _imageStageDisplayProgress = Math.Max(_imageStageDisplayProgress, mapped);
        Progress.Value = Math.Clamp(_imageStageDisplayProgress, _imageStageProgressFloor, 99d);
        StatusText.Text = _imageStageProgressFloor > 0
            ? "Giai đoạn 2/2 · " + snapshot.Message
            : snapshot.Message;
    }

    private void CleanupEditorProgress()
    {
        StopVoiceArtifactMonitor();
        _editorExportProgressTimer?.Stop();
        _observedImageProgressJobId = null;
        _imageStageProgressFloor = 0;
        _imageStageDisplayProgress = 0;
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

    void ValidateUiShellLayoutForSmoke(object sender, SizeChangedEventArgs e)
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

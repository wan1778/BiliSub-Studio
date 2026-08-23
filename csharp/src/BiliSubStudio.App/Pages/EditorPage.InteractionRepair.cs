using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Playback;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _interactionRepairInitialized;
    private Border? _compactPreviewChrome;
    private Button? _compactPlayButton;
    private Button? _compactMuteButton;
    private Button? _compactFullscreenButton;
    private Slider? _compactVolumeSlider;
    private TextBlock? _compactCurrentTime;
    private TextBlock? _compactTotalTime;
    private CancellationTokenSource? _continuousSeekCancellation;
    private bool _continuousPreviewTransition;

    private void EnsureInteractionRepairInitialized()
    {
        if (_interactionRepairInitialized) return;
        _interactionRepairInitialized = true;

        InstallSafePickerHandlers();
        BuildCompactPreviewChrome();

        PreviewPlayer.AreTransportControlsEnabled = false;
        if (_editorAutoCompositeToggle is not null)
        {
            _editorAutoCompositeToggle.IsOn = false;
            _editorAutoCompositeToggle.Visibility = Visibility.Collapsed;
        }

        Timeline.ValueChanged += CompactTimeline_ValueChanged;
        PreviewMuteToggle.Toggled += CompactExternalMute_Toggled;
        PreviewVolumeSlider.ValueChanged += CompactExternalVolume_ValueChanged;
        Unloaded += InteractionRepair_Unloaded;

        if (_imageModeButton is null || _imageInspectorPanel is null || _imageOverlayCanvas is null)
            throw new InvalidOperationException("Editor chưa khởi tạo được công cụ Ảnh/logo.");
        if (_compactPreviewChrome is null || PreviewPlayer.AreTransportControlsEnabled)
            throw new InvalidOperationException("Editor chưa khởi tạo được thanh điều khiển preview gọn.");
    }

    private void InstallSafePickerHandlers()
    {
        OpenVideoButton.Click -= Pick_Click;
        OpenVideoButton.Click += SafePickVideo_Click;
        ImportSrtButton.Click -= ImportSrt_Click;
        ImportSrtButton.Click += SafeImportSrt_Click;

        if (_addImageButton is not null)
        {
            _addImageButton.Click -= AddImage_Click;
            _addImageButton.Click += SafeAddImage_Click;
        }
    }

    private void BuildCompactPreviewChrome()
    {
        if (Overlay.Parent is not Grid previewHost)
            throw new InvalidOperationException("Không tìm thấy preview host của Editor.");

        if (Timeline.Parent is Grid oldTimelineHost)
        {
            oldTimelineHost.Children.Remove(Timeline);
            if (oldTimelineHost.Parent is StackPanel oldTransportHost)
                oldTransportHost.Visibility = Visibility.Collapsed;
        }

        PlaybackButton.Visibility = Visibility.Collapsed;
        FullscreenButton.Visibility = Visibility.Collapsed;
        RefreshFrameButton.Visibility = Visibility.Collapsed;
        ClockText.Visibility = Visibility.Collapsed;
        RegionTimelineCanvas.Visibility = Visibility.Collapsed;

        var chrome = new Border
        {
            Height = 50,
            Margin = new Thickness(16, 0, 16, 12),
            Padding = new Thickness(8, 2, 8, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 24, 25, 29)),
        };
        AutomationProperties.SetName(chrome, "Điều khiển preview Editor");

        var grid = new Grid { RowSpacing = 0, ColumnSpacing = 7 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Timeline.Margin = new Thickness(0, -2, 0, 0);
        Timeline.MinWidth = 120;
        Grid.SetRow(Timeline, 0);
        Grid.SetColumn(Timeline, 0);
        Grid.SetColumnSpan(Timeline, 7);
        grid.Children.Add(Timeline);

        _compactCurrentTime = new TextBlock
        {
            Text = "00:00",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
        };
        Grid.SetRow(_compactCurrentTime, 1);
        Grid.SetColumn(_compactCurrentTime, 0);
        grid.Children.Add(_compactCurrentTime);

        _compactPlayButton = new Button
        {
            Content = "▶",
            Width = 34,
            Height = 26,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(_compactPlayButton, "Phát hoặc tạm dừng preview toàn video");
        _compactPlayButton.Click += CompactPlay_Click;
        Grid.SetRow(_compactPlayButton, 1);
        Grid.SetColumn(_compactPlayButton, 2);
        grid.Children.Add(_compactPlayButton);

        _compactMuteButton = new Button
        {
            Content = "Âm",
            MinWidth = 38,
            Height = 26,
            Padding = new Thickness(6, 0, 6, 0),
        };
        AutomationProperties.SetName(_compactMuteButton, "Bật hoặc tắt tiếng preview");
        _compactMuteButton.Click += CompactMute_Click;
        Grid.SetRow(_compactMuteButton, 1);
        Grid.SetColumn(_compactMuteButton, 3);
        grid.Children.Add(_compactMuteButton);

        _compactVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = PreviewVolumeSlider.Value,
            Width = 74,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_compactVolumeSlider, "Âm lượng preview");
        _compactVolumeSlider.ValueChanged += CompactVolume_ValueChanged;
        Grid.SetRow(_compactVolumeSlider, 1);
        Grid.SetColumn(_compactVolumeSlider, 4);
        grid.Children.Add(_compactVolumeSlider);

        _compactFullscreenButton = new Button
        {
            Content = "⛶",
            Width = 34,
            Height = 26,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(_compactFullscreenButton, "Xem preview toàn màn hình");
        _compactFullscreenButton.Click += CompactFullscreen_Click;
        Grid.SetRow(_compactFullscreenButton, 1);
        Grid.SetColumn(_compactFullscreenButton, 5);
        grid.Children.Add(_compactFullscreenButton);

        _compactTotalTime = new TextBlock
        {
            Text = "00:00",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 42,
        };
        Grid.SetRow(_compactTotalTime, 1);
        Grid.SetColumn(_compactTotalTime, 6);
        grid.Children.Add(_compactTotalTime);

        chrome.Child = grid;
        previewHost.Children.Add(chrome);
        _compactPreviewChrome = chrome;
        UpdateCompactClock();
    }

    private async void SafePickVideo_Click(object sender, RoutedEventArgs e)
    {
        if (EditorBusy || _playerMode) return;
        StatusText.Text = "Đang mở hộp chọn video...";
        try
        {
            var path = await _picker.PickVideoAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText.Text = "Đã hủy chọn video.";
                return;
            }
            await LoadVideoPathSafeAsync(path);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không chọn/mở được video: " + error.Message;
        }
    }

    private async Task LoadVideoPathSafeAsync(string path)
    {
        if (_playerMode) await SetPlaybackModeAsync(enabled: false, play: false);
        var pendingSubtitle = _project is null ? _subtitleSource : null;
        var pendingPlacement = _subtitlePlacement;
        await SaveProjectNowAsync();
        _path = path;
        _media = await _application.Media.ProbeAsync(path, CancellationToken.None);
        _project = await _application.LoadEditorProjectAsync(path, _media, CancellationToken.None);
        _document.Reset(_project.Regions);
        _audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);
        ApplyAudioSettingsToUi();
        if (pendingSubtitle is not null)
        {
            _subtitleSource = pendingSubtitle;
            _subtitlePlacement = pendingPlacement;
            AttachSubtitleToProject(string.Empty);
            SrtPathText.Text = pendingSubtitle.Path;
            UpdateSubtitleSummary();
            TranslationStatusText.Text = "Đã gắn SRT đã chọn vào video; có thể đặt khung và Vietsub.";
        }
        else await RestoreSubtitleAsync(_project.Subtitle);
        await RestoreSpeechAndVoiceAsync();
        _draftRegion = null;
        Timeline.Maximum = Math.Max(0.1, _media.Duration);
        Timeline.Value = 0;
        PathText.Text = path;
        MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · preview toàn video";
        _syncingInputs = true;
        try
        {
            FileNameBox.Text = _project.FileName;
            EndBox.Value = _media.Duration;
        }
        finally { _syncingInputs = false; }
        await PreparePlayerAsync();
        AttachContinuousPreviewHandlers();
        Timeline.IsEnabled = true;
        RefreshFrameButton.IsEnabled = true;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        else SetCoordinateBoxes(0, 0, 0, 0);
        RenderDocument();
        await UpdateFrameAsync();
        UpdateCompactClock();
        StatusText.Text = _document.Regions.Count > 0
            ? $"Đã mở project với {_document.Regions.Count} vùng."
            : _subtitleSource is not null
                ? $"Đã mở SRT {_subtitleSource.Cues.Count} câu; khung phụ đề có thể kéo/resize trực tiếp."
                : "Đã mở video. Chọn SRT tiếng Trung hoặc chọn công cụ cần chỉnh.";
        RefreshEditorActions();
        QueueProjectSave();
    }

    private async void SafeImportSrt_Click(object sender, RoutedEventArgs e)
    {
        if (EditorBusy || _playerMode) return;
        TranslationStatusText.Text = "Đang mở hộp chọn SRT...";
        try
        {
            var path = await _picker.PickSubtitleAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                TranslationStatusText.Text = "Đã hủy chọn SRT.";
                return;
            }
            var source = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);
            _subtitleSource = source;
            _subtitlePlacement = EditorSubtitlePlacement.Default;
            if (_project is not null)
            {
                _voiceTrack = null;
                _project = _project with { Tts = null };
                AttachSubtitleToProject(string.Empty);
                await RefreshSpeechTimingForSubtitleAsync();
            }
            SrtPathText.Text = source.Path;
            AsrStatusText.Text = _project?.Speech is { Status: "complete" }
                ? "Đang dùng SRT đã chọn; Whisper timing của video vẫn được giữ và ánh xạ vào SRT này."
                : "Đã dùng SRT đã chọn. Vào Âm thanh để chạy Whisper word timing/nhịp thoại.";
            TranslationProgress.Value = 0;
            TranslationStatusText.Text = _media is null
                ? "Đã nạp SRT. Có thể chuẩn bị AI ngay; mở video để đặt khung phụ đề trên preview."
                : "Đã nạp SRT. Kéo/resize khung phụ đề trực tiếp trên preview rồi Vietsub.";
            UpdateSubtitleSummary();
            RenderOverlays();
            QueueProjectSave();
            RefreshEditorActions();
        }
        catch (Exception error)
        {
            TranslationStatusText.Text = "Không chọn/đọc được SRT: " + error.Message;
            StatusText.Text = TranslationStatusText.Text;
        }
    }

    private async void SafeAddImage_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _project is null)
        {
            if (_imageStatusText is not null) _imageStatusText.Text = "Hãy mở video trước khi thêm ảnh/logo.";
            return;
        }
        if (EditorBusy || _playerMode) return;
        if (_imageOverlays.Count >= MaxEditorImages)
        {
            if (_imageStatusText is not null) _imageStatusText.Text = $"Tối đa {MaxEditorImages} ảnh/logo.";
            return;
        }
        if (_imageStatusText is not null) _imageStatusText.Text = "Đang mở hộp chọn ảnh/logo...";
        try
        {
            var path = await _picker.PickImageAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_imageStatusText is not null) _imageStatusText.Text = "Đã hủy chọn ảnh/logo.";
                return;
            }
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg"))
                throw new InvalidDataException("Chỉ hỗ trợ PNG, JPG hoặc JPEG.");
            var file = await StorageFile.GetFileFromPathAsync(path);
            uint pixelWidth;
            uint pixelHeight;
            using (var stream = await file.OpenReadAsync())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                pixelWidth = decoder.PixelWidth;
                pixelHeight = decoder.PixelHeight;
            }
            if (pixelWidth == 0 || pixelHeight == 0) throw new InvalidDataException("Không đọc được kích thước ảnh/logo.");

            var width = .18;
            var aspect = pixelWidth / (double)pixelHeight;
            var height = width * _media.Width / (aspect * _media.Height);
            if (height > .34)
            {
                height = .34;
                width = height * aspect * _media.Height / _media.Width;
            }
            width = Math.Clamp(width, .03, .45);
            height = Math.Clamp(height, .03, .45);
            var state = new EditorImageOverlayState(
                Path.GetFullPath(path),
                Math.Max(0, 1 - width - .025),
                .025,
                width,
                height,
                1,
                pixelWidth,
                pixelHeight);
            _imageOverlays.Add(state);
            _selectedImageIndex = _imageOverlays.Count - 1;
            await EnsureBitmapLoadedAsync(state.Path);
            await SaveImageSidecarAsync();
            RenderImageList();
            LoadSelectedImageIntoInputs();
            RenderImageOverlays();
            if (_imageStatusText is not null) _imageStatusText.Text = $"Đã thêm {Path.GetFileName(state.Path)}. Kéo trực tiếp trên preview để đặt logo.";
            RefreshImageControls();
        }
        catch (Exception error)
        {
            if (_imageStatusText is not null) _imageStatusText.Text = "Không thêm được ảnh/logo: " + error.Message;
            StatusText.Text = _imageStatusText?.Text ?? error.Message;
        }
    }

    private async void CompactPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _player is null || EditorBusy && !_playerMode) return;
        try
        {
            if (_playerMode)
            {
                await SetPlaybackModeAsync(enabled: false, play: false);
                UpdateCompactPlayState(false);
                StatusText.Text = "Đã tạm dừng tại frame hiện tại; các khung chỉnh đã hoạt động lại.";
                return;
            }
            AttachContinuousPreviewHandlers();
            await SetPlaybackModeAsync(enabled: true, play: true);
            UpdateCompactPlayState(true);
            StatusText.Text = "Đang phát preview toàn video; BiliSub tự dựng cache xử lý theo vị trí phát.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã dừng tạo preview.";
            UpdateCompactPlayState(false);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không phát được preview: " + error.Message;
            UpdateCompactPlayState(false);
        }
    }

    private void CompactMute_Click(object sender, RoutedEventArgs e)
    {
        PreviewMuteToggle.IsOn = !PreviewMuteToggle.IsOn;
        UpdateCompactMuteState();
    }

    private void CompactVolume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(PreviewVolumeSlider.Value - e.NewValue) > .01)
            PreviewVolumeSlider.Value = e.NewValue;
        if (_player is not null) _player.Volume = Math.Clamp(e.NewValue / 100, 0, 1);
    }

    private async void CompactFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _player is null) return;
        try
        {
            if (!_playerMode)
            {
                AttachContinuousPreviewHandlers();
                await SetPlaybackModeAsync(enabled: true, play: true);
                UpdateCompactPlayState(true);
            }
            PreviewPlayer.IsFullWindow = true;
        }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được toàn màn hình: " + error.Message;
        }
    }

    private void CompactTimeline_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateCompactClock();
        if (!_playerMode || _syncingTimeline || _continuousPreviewTransition) return;

        _continuousSeekCancellation?.Cancel();
        _continuousSeekCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _continuousSeekCancellation = cancellation;
        _ = RestartProcessedPreviewAtAsync(e.NewValue, cancellation);
    }

    private async Task RestartProcessedPreviewAtAsync(double target, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(220, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _continuousPreviewTransition = true;
            await SetPlaybackModeAsync(enabled: false, play: false);
            cancellation.Token.ThrowIfCancellationRequested();
            _syncingTimeline = true;
            try { Timeline.Value = Math.Clamp(target, Timeline.Minimum, Timeline.Maximum); }
            finally { _syncingTimeline = false; }
            await SetPlaybackModeAsync(enabled: true, play: true);
            cancellation.Token.ThrowIfCancellationRequested();
            UpdateCompactPlayState(true);
            StatusText.Text = "Đã chuyển vị trí; tiếp tục phát preview toàn video.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không chuyển được vị trí preview: " + error.Message;
            UpdateCompactPlayState(false);
        }
        finally
        {
            _continuousPreviewTransition = false;
            if (ReferenceEquals(_continuousSeekCancellation, cancellation)) _continuousSeekCancellation = null;
            cancellation.Dispose();
        }
    }

    private void AttachContinuousPreviewHandlers()
    {
        if (_player is null) return;
        _player.MediaEnded -= PlayerMediaEnded;
        _player.MediaEnded -= ContinuousPreview_MediaEnded;
        _player.MediaEnded += ContinuousPreview_MediaEnded;
    }

    private void ContinuousPreview_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_playerMode || _media is null || _continuousPreviewTransition) return;
            var next = _playerSourceStart + _playerSourceDuration;
            if (next >= _media.Duration - .05)
            {
                try
                {
                    await SetPlaybackModeAsync(enabled: false, play: false);
                    _syncingTimeline = true;
                    try { Timeline.Value = _media.Duration; }
                    finally { _syncingTimeline = false; }
                    UpdateCompactClock();
                    UpdateCompactPlayState(false);
                    StatusText.Text = "Đã phát hết preview toàn video.";
                }
                catch (Exception error) { StatusText.Text = "Không kết thúc được preview: " + error.Message; }
                return;
            }

            _continuousPreviewTransition = true;
            try
            {
                await SetPlaybackModeAsync(enabled: false, play: false);
                _syncingTimeline = true;
                try { Timeline.Value = Math.Clamp(next, Timeline.Minimum, Timeline.Maximum); }
                finally { _syncingTimeline = false; }
                await SetPlaybackModeAsync(enabled: true, play: true);
                UpdateCompactPlayState(true);
                StatusText.Text = "Đang phát preview toàn video.";
            }
            catch (Exception error)
            {
                StatusText.Text = "Không nối tiếp được preview: " + error.Message;
                UpdateCompactPlayState(false);
            }
            finally { _continuousPreviewTransition = false; }
        });
    }

    private void CompactExternalMute_Toggled(object sender, RoutedEventArgs e) => UpdateCompactMuteState();

    private void CompactExternalVolume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_compactVolumeSlider is not null && Math.Abs(_compactVolumeSlider.Value - e.NewValue) > .01)
            _compactVolumeSlider.Value = e.NewValue;
    }

    private void UpdateCompactClock()
    {
        if (_compactCurrentTime is not null) _compactCurrentTime.Text = FormatClock(Timeline.Value);
        if (_compactTotalTime is not null) _compactTotalTime.Text = FormatClock(_media?.Duration ?? 0);
    }

    private void UpdateCompactPlayState(bool playing)
    {
        if (_compactPlayButton is not null) _compactPlayButton.Content = playing ? "Ⅱ" : "▶";
    }

    private void UpdateCompactMuteState()
    {
        if (_compactMuteButton is not null) _compactMuteButton.Content = PreviewMuteToggle.IsOn ? "Tắt" : "Âm";
    }

    private void InteractionRepair_Unloaded(object sender, RoutedEventArgs e)
    {
        _continuousSeekCancellation?.Cancel();
        _continuousSeekCancellation?.Dispose();
        _continuousSeekCancellation = null;
    }
}

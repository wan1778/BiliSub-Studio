using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage : Page
{
    private enum DragKind { None, Create, Move, North, South, East, West, NorthEast, NorthWest, SouthEast, SouthWest }
    private enum InspectorMode { Subtitle, Blur, Audio, Export }

    private readonly BiliSubApplication _application;
    private readonly IFilePickerService _picker;
    private readonly EditorRegionDocument _document = new();
    private string? _path;
    private MediaPreviewInfo? _media;
    private EditorProject? _project;
    private EditorSubtitleSource? _subtitleSource;
    private EditorSubtitlePlacement _subtitlePlacement = EditorSubtitlePlacement.Default;
    private EditorAudioSettings _audioSettings = EditorAudioSettings.Default;
    private EditRegion? _draftRegion;
    private string? _jobId;
    private string? _translationJobId;
    private MediaPlayer? _player;
    private bool _playerMode;
    private bool _syncingTimeline;
    private bool _syncingInputs;
    private bool _syncingList;
    private bool _syncingAudio;
    private InspectorMode _inspectorMode = InspectorMode.Subtitle;
    private Point? _dragStartNormalized;
    private EditRegion? _dragOriginal;
    private DragKind _dragKind;
    private bool _dragHistoryCaptured;
    private bool _subtitleDrag;
    private EditorSubtitlePlacement? _subtitleDragOriginal;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _saveCancellation;
    private int _previewRevision;
    private double _lastOverlayWidth = -1;
    private double _lastOverlayHeight = -1;
    private double _lastTimelineWidth = -1;

    public EditorPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application;
        _picker = picker;
        InitializeComponent();
        LayoutUpdated += EditorPage_LayoutUpdated;
        Unloaded += EditorPage_Unloaded;
        SetInspectorMode(InspectorMode.Subtitle);
        RefreshEditorActions();
    }

    private void InspectorMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<InspectorMode>(tag, ignoreCase: true, out var mode))
            SetInspectorMode(mode);
    }

    private void SetInspectorMode(InspectorMode mode)
    {
        _inspectorMode = mode;
        SubtitleModeButton.IsChecked = mode == InspectorMode.Subtitle;
        BlurModeButton.IsChecked = mode == InspectorMode.Blur;
        AudioModeButton.IsChecked = mode == InspectorMode.Audio;
        ExportModeButton.IsChecked = mode == InspectorMode.Export;
        SubtitleInspectorPanel.Visibility = mode == InspectorMode.Subtitle ? Visibility.Visible : Visibility.Collapsed;
        BlurInspectorPanel.Visibility = mode == InspectorMode.Blur ? Visibility.Visible : Visibility.Collapsed;
        AudioInspectorPanel.Visibility = mode == InspectorMode.Audio ? Visibility.Visible : Visibility.Collapsed;
        ExportInspectorPanel.Visibility = mode == InspectorMode.Export ? Visibility.Visible : Visibility.Collapsed;
        RenderOverlays();
        RefreshEditorActions();
    }

    internal Task RunLayoutSmokeAsync()
    {
        if (!ImportSrtButton.IsEnabled || !PrepareAiButton.IsEnabled)
            throw new InvalidOperationException("Editor phải cho phép chọn SRT và chuẩn bị AI trước khi chọn video.");
        foreach (var mode in Enum.GetValues<InspectorMode>())
        {
            SetInspectorMode(mode);
            var visible = new[] { SubtitleInspectorPanel, BlurInspectorPanel, AudioInspectorPanel, ExportInspectorPanel }
                .Count(panel => panel.Visibility == Visibility.Visible);
            if (visible != 1) throw new InvalidOperationException("Editor icon rail không chọn đúng một inspector.");
        }
        SetInspectorMode(InspectorMode.Subtitle);
        return Task.CompletedTask;
    }

    private void EditorPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _player?.Pause();
        _previewCancellation?.Cancel();
        _ = SaveProjectNowAsync();
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var path = await _picker.PickVideoAsync();
        if (path is null) return;
        try
        {
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
            _draftRegion = null;
            Timeline.Maximum = Math.Max(0.1, _media.Duration);
            Timeline.Value = 0;
            PathText.Text = path;
            MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · {(_media.DirectCompatible ? "player native + preview hiệu ứng FFmpeg" : "preview hiệu ứng FFmpeg")}";
            _syncingInputs = true;
            try
            {
                FileNameBox.Text = _project.FileName;
                EndBox.Value = _media.Duration;
            }
            finally { _syncingInputs = false; }
            await PreparePlayerAsync(path, _media.DirectCompatible);
            Timeline.IsEnabled = true;
            RefreshFrameButton.IsEnabled = true;
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            else SetCoordinateBoxes(0, 0, 0, 0);
            RenderDocument();
            await UpdateFrameAsync();
            StatusText.Text = _document.Regions.Count > 0
                ? $"Đã mở lại project với {_document.Regions.Count} vùng."
                : _subtitleSource is not null
                    ? $"Đã mở lại SRT {_subtitleSource.Cues.Count} câu; khung phụ đề có thể kéo/resize trực tiếp."
                    : "Chọn SRT tiếng Trung để bắt đầu Vietsub, hoặc kéo frame để tạo vùng hiệu ứng.";
            RefreshEditorActions();
            QueueProjectSave();
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { await UpdateFrameAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async Task RestoreSubtitleAsync(EditorSubtitleProject? saved)
    {
        _subtitleSource = null;
        _subtitlePlacement = EditorSubtitlePlacement.Default;
        SrtPathText.Text = "Chưa chọn SRT.";
        SrtSummaryText.Text = "Skill: Dịch Trung Tu Tiên (tích hợp, đã khóa SHA-256).";
        OpenTranslatedSrtButton.IsEnabled = false;
        if (saved is null) return;
        try
        {
            var current = await _application.LoadEditorSubtitleAsync(saved.SourcePath, CancellationToken.None);
            if (!string.Equals(current.Sha256, saved.SourceSha256, StringComparison.Ordinal))
            {
                TranslationStatusText.Text = "File SRT nguồn đã thay đổi; hãy chọn lại để không ghép nhầm checkpoint/bản dịch.";
                return;
            }
            var translated = saved.Cues.ToDictionary(x => x.Id, x => x.VietnameseText, StringComparer.Ordinal);
            _subtitleSource = current with
            {
                Cues = current.Cues.Select(x => x with
                {
                    VietnameseText = translated.TryGetValue(x.Id, out var value) ? value : string.Empty,
                }).ToArray(),
            };
            _subtitlePlacement = saved.Placement ?? EditorSubtitlePlacement.Default;
            SrtPathText.Text = saved.SourcePath;
            UpdateSubtitleSummary();
            OpenTranslatedSrtButton.IsEnabled = File.Exists(saved.OutputPath);
            TranslationStatusText.Text = _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))
                ? "Bản Vietsub đã hoàn tất; có thể mở thư mục SRT Việt."
                : "Đã khôi phục SRT và các câu dịch/checkpoint hiện có.";
        }
        catch (Exception error)
        {
            TranslationStatusText.Text = "Không khôi phục được SRT: " + error.Message;
        }
    }

    private async void ImportSrt_Click(object sender, RoutedEventArgs e)
    {
        var path = await _picker.PickSubtitleAsync();
        if (path is null) return;
        try
        {
            var source = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);
            _subtitleSource = source;
            _subtitlePlacement = EditorSubtitlePlacement.Default;
            if (_project is not null) AttachSubtitleToProject(string.Empty);
            SrtPathText.Text = source.Path;
            TranslationProgress.Value = 0;
            TranslationStatusText.Text = _media is null
                ? "Đã khóa timecode và thứ tự. Có thể chuẩn bị AI ngay; hãy chọn video để đặt khung và Vietsub."
                : "Đã khóa timecode và thứ tự. Kéo/resize khung phụ đề trên preview rồi bấm Chuẩn bị AI.";
            UpdateSubtitleSummary();
            RenderOverlays();
            QueueProjectSave();
            RefreshEditorActions();
        }
        catch (Exception error) { TranslationStatusText.Text = error.Message; }
    }

    private void AttachSubtitleToProject(string outputPath)
    {
        if (_project is null || _subtitleSource is null) return;
        var skill = _application.LocalTranslationStatus;
        _project = _project with
        {
            Subtitle = new EditorSubtitleProject(
                _subtitleSource.Path,
                _subtitleSource.Size,
                _subtitleSource.LastWriteUtcTicks,
                _subtitleSource.Sha256,
                _subtitleSource.Cues,
                _subtitlePlacement,
                skill.SkillName,
                skill.SkillSha256,
                outputPath),
        };
    }

    private async void PrepareAi_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is not null) return;
        try
        {
            _translationJobId = _application.StartLocalTranslationPreparation();
            RefreshEditorActions();
            await PollTranslationJobAsync(preparing: true);
        }
        catch (Exception error)
        {
            _translationJobId = null;
            TranslationStatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null) return;
        try
        {
            var outputName = System.IO.Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt";
            _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(
                _project.Id,
                _subtitleSource,
                _application.Config.OutputDirectory,
                outputName));
            RefreshEditorActions();
            await PollTranslationJobAsync(preparing: false);
        }
        catch (Exception error)
        {
            _translationJobId = null;
            TranslationStatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private void CancelTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is null) return;
        _application.CancelJob(_translationJobId);
        TranslationStatusText.Text = "Đang dừng AI an toàn; các batch đã xong vẫn nằm trong checkpoint...";
    }

    private async Task PollTranslationJobAsync(bool preparing)
    {
        while (_translationJobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_translationJobId);
            TranslationProgress.Value = snapshot.Progress;
            TranslationStatusText.Text = snapshot.Message;
            if (snapshot.Done)
            {
                if (!preparing && snapshot.Result is EditorTranslationResult result && _project?.Subtitle is { } subtitle && _subtitleSource is not null)
                {
                    _subtitleSource = _subtitleSource with { Cues = result.Cues };
                    _project = _project with
                    {
                        Subtitle = subtitle with
                        {
                            Cues = result.Cues,
                            Placement = _subtitlePlacement,
                            SkillSha256 = result.SkillSha256,
                            OutputPath = result.OutputPath,
                        },
                    };
                    await SaveProjectNowAsync();
                    UpdateSubtitleSummary();
                    RenderOverlays();
                }
                _translationJobId = null;
                RefreshEditorActions();
                break;
            }
            await Task.Delay(350);
        }
    }

    private async void OpenTranslatedSrt_Click(object sender, RoutedEventArgs e)
    {
        var output = _project?.Subtitle?.OutputPath;
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Không xác định được thư mục SRT Việt.");
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception error) { TranslationStatusText.Text = "Không mở được thư mục: " + error.Message; }
    }

    private void UpdateSubtitleSummary()
    {
        if (_subtitleSource is null) return;
        var translated = _subtitleSource.Cues.Count(x => !string.IsNullOrWhiteSpace(x.VietnameseText));
        SrtSummaryText.Text = $"{_subtitleSource.Cues.Count} câu · đã dịch {translated} · Skill Dịch Trung Tu Tiên · timecode giữ nguyên";
    }

    private void Timeline_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_playerMode && !_syncingTimeline && _player is not null)
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
        UpdateClock();
        RenderOverlays();
        RenderTimelineRegions();
        if (!_playerMode && _media is not null) QueuePreviewRefresh();
    }

    private async Task UpdateFrameAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var revision = ++_previewRevision;
        await RefreshPreviewAsync(revision, TimeSpan.Zero, cancellation.Token);
    }

    private void QueuePreviewRefresh()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var revision = ++_previewRevision;
        _ = RefreshPreviewAsync(revision, TimeSpan.FromMilliseconds(140), cancellation.Token);
    }

    private async Task RefreshPreviewAsync(int revision, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_path is null || _media is null || _playerMode) return;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            var regions = CurrentPreviewRegions();
            var bytes = await _application.GetEditorPreviewFrameJpegAsync(_path, Timeline.Value, _media, regions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _previewRevision) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (revision == _previewRevision) PreviewImage.Source = bitmap;
            UpdateClock();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision == _previewRevision) StatusText.Text = "Preview hiệu ứng: " + error.Message;
        }
    }

    private IReadOnlyList<EditRegion> CurrentPreviewRegions()
    {
        if (_draftRegion is null) return _document.Regions.ToArray();
        return [.. _document.Regions, _draftRegion];
    }

    private void UpdateClock() => ClockText.Text = $"{FormatClock(Timeline.Value)} / {FormatClock(_media?.Duration ?? 0)}";

    private async void Playback_Click(object sender, RoutedEventArgs e)
    {
        try { await SetPlaybackModeAsync(!_playerMode, play: true); }
        catch (Exception error) { StatusText.Text = "Player native: " + error.Message; }
    }

    private async void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetPlaybackModeAsync(true, play: false);
            PreviewPlayer.IsFullWindow = true;
        }
        catch (Exception error) { StatusText.Text = "Toàn màn hình: " + error.Message; }
    }

    private void WholeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingInputs) return;
        StartBox.IsEnabled = EndBox.IsEnabled = !WholeToggle.IsOn && _jobId is null;
        if (!WholeToggle.IsOn && _media is not null && _document.Selected is null)
        {
            _syncingInputs = true;
            try
            {
                StartBox.Value = Timeline.Value;
                EndBox.Value = Math.Min(_media.Duration, Timeline.Value + 5);
            }
            finally { _syncingInputs = false; }
        }
        ApplyInputsToDocument();
    }

    private void EffectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void EditInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void RegionCoordinates_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded && !_syncingInputs) ApplyInputsToDocument();
    }

    private void ApplyInputsToDocument()
    {
        var region = ReadRegionFromInputs(_document.Selected?.Id ?? string.Empty);
        if (region is null)
        {
            _draftRegion = null;
            RegionValidationText.Text = _media is null
                ? "Chọn video rồi kéo để tạo vùng."
                : "Vùng phải lớn hơn 0, nằm trong video và có thời gian hợp lệ.";
            RefreshEditorActions();
            return;
        }
        try
        {
            ValidateRegion(region);
            if (_document.Selected is not null)
            {
                _document.ReplaceSelected(region);
                _draftRegion = null;
                RegionValidationText.Text = "Đã cập nhật vùng đang chọn.";
                RenderDocument();
                QueueProjectSave();
                QueuePreviewRefresh();
            }
            else
            {
                _draftRegion = region;
                RegionValidationText.Text = "Tọa độ hợp lệ; bấm Thêm để lưu vùng.";
                RenderOverlays();
            }
        }
        catch (Exception error)
        {
            _draftRegion = null;
            RegionValidationText.Text = error.Message;
        }
        RefreshEditorActions();
    }

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        var region = ReadRegionFromInputs(string.Empty);
        if (region is null) { StatusText.Text = "Hãy nhập một vùng hợp lệ trước."; return; }
        try
        {
            ValidateRegion(region);
            _document.Add(region);
            _draftRegion = null;
            LoadSelectedIntoInputs();
            DocumentChanged($"Đã thêm vùng {_document.Regions.Count}.");
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_document.RemoveSelected())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            else SetCoordinateBoxes(0, 0, 0, 0);
            DocumentChanged("Đã xóa vùng chọn. Có thể Hoàn tác.");
        }
    }

    private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        _document.Select(RegionList.SelectedIndex);
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        RenderDocument();
        RefreshEditorActions();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Undo())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã hoàn tác.");
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Redo())
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã làm lại.");
        }
    }

    private void SubtitlePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        _document.Add(new EditRegion(.08, .72, .84, .18, "blur", 18, true, 0, _media.Duration));
        LoadSelectedIntoInputs();
        DocumentChanged("Đã thêm preset vùng phụ đề dưới.");
    }

    private void WatermarkPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        _document.Add(new EditRegion(.78, .04, .18, .10, "mosaic", 12, true, 0, _media.Duration));
        LoadSelectedIntoInputs();
        DocumentChanged("Đã thêm preset watermark góc phải.");
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_syncingInputs)
        {
            QueueProjectSave();
            RefreshEditorActions();
        }
    }

    private void PreviewMute_Toggled(object sender, RoutedEventArgs e)
    {
        if (_player is not null) _player.IsMuted = PreviewMuteToggle.IsOn;
    }

    private void PreviewVolume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_player is not null) _player.Volume = Math.Clamp(PreviewVolumeSlider.Value / 100, 0, 1);
    }

    private void SourceAudioMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAudioSettingsFromUi();

    private void SourceAudioGain_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateAudioSettingsFromUi();

    private void UpdateAudioSettingsFromUi()
    {
        if (_syncingAudio || !IsLoaded) return;
        var mode = (SourceAudioModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "keep";
        var gain = mode switch
        {
            "mute" => 0,
            "duck" => SourceAudioGainSlider.Value / 100,
            _ => 1,
        };
        _audioSettings = EditorProjectStore.NormalizeAudio(new EditorAudioSettings(mode, gain));
        AudioStatusText.Text = _audioSettings.SourceMode switch
        {
            "duck" => $"Video xuất sẽ giữ {_audioSettings.SourceGain:P0} mức âm thanh gốc.",
            "mute" => "Video xuất sẽ không có âm thanh gốc.",
            _ => "Video xuất sẽ giữ nguyên âm thanh gốc.",
        };
        QueueProjectSave();
        RefreshEditorActions();
    }

    private void ApplyAudioSettingsToUi()
    {
        _syncingAudio = true;
        try
        {
            for (var index = 0; index < SourceAudioModeBox.Items.Count; index++)
            {
                if (SourceAudioModeBox.Items[index] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), _audioSettings.SourceMode, StringComparison.OrdinalIgnoreCase))
                {
                    SourceAudioModeBox.SelectedIndex = index;
                    break;
                }
            }
            if (_audioSettings.SourceMode == "duck") SourceAudioGainSlider.Value = _audioSettings.SourceGain * 100;
            AudioStatusText.Text = _audioSettings.SourceMode switch
            {
                "duck" => $"Video xuất sẽ giữ {_audioSettings.SourceGain:P0} mức âm thanh gốc.",
                "mute" => "Video xuất sẽ không có âm thanh gốc.",
                _ => "Video xuất sẽ giữ nguyên âm thanh gốc.",
            };
        }
        finally { _syncingAudio = false; }
        RefreshEditorActions();
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        var subtitleBurn = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))
            ? new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement)
            : null;
        var audioChanged = _audioSettings.SourceMode != "keep";
        if (_path is null || _media is null || _document.Regions.Count == 0 && subtitleBurn is null && !audioChanged)
        {
            StatusText.Text = "Cần ít nhất một vùng hiệu ứng, bản Vietsub đã hoàn tất hoặc thay đổi âm thanh.";
            return;
        }
        try
        {
            await SaveProjectNowAsync();
            _jobId = _application.StartEditor(new VideoEditRequest(
                _path,
                _application.Config.OutputDirectory,
                FileNameBox.Text,
                _media.Width,
                _media.Height,
                _media.Duration,
                _document.Regions.ToArray(),
                subtitleBurn,
                _audioSettings));
            RefreshEditorActions();
            while (_jobId is not null)
            {
                var snapshot = _application.Jobs.GetSnapshot(_jobId);
                Progress.Value = snapshot.Progress;
                StatusText.Text = snapshot.Message;
                if (snapshot.Done)
                {
                    _jobId = null;
                    RefreshEditorActions();
                    QueuePreviewRefresh();
                    break;
                }
                await Task.Delay(350);
            }
        }
        catch (Exception error)
        {
            _jobId = null;
            StatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null) return;
        _application.CancelJob(_jobId);
        StatusText.Text = "Đang dừng FFmpeg và xóa file render dở...";
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_media is null || _jobId is not null || _translationJobId is not null || _playerMode) return;
        var point = e.GetCurrentPoint(Overlay).Position;
        if (!TryNormalize(point, out var normalized)) return;
        if (_inspectorMode == InspectorMode.Subtitle)
        {
            var subtitleHit = HitTestSubtitle(point);
            if (subtitleHit == DragKind.None) return;
            _subtitleDrag = true;
            _subtitleDragOriginal = _subtitlePlacement;
            _dragKind = subtitleHit;
            _document.Select(-1);
            _draftRegion = null;
        }
        else if (_inspectorMode == InspectorMode.Blur)
        {
            var hit = HitTestRegion(point);
            if (hit.Index >= 0)
            {
                _document.Select(hit.Index);
                _dragKind = hit.Kind;
                _dragOriginal = _document.Selected;
                LoadSelectedIntoInputs();
            }
            else
            {
                _document.Select(-1);
                _dragKind = DragKind.Create;
                _dragOriginal = null;
                _draftRegion = null;
            }
        }
        else return;
        _dragStartNormalized = normalized;
        _dragHistoryCaptured = false;
        Overlay.CapturePointer(e.Pointer);
        RenderDocument();
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartNormalized is null || _media is null || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (!TryNormalize(e.GetCurrentPoint(Overlay).Position, out var current)) return;
        if (_subtitleDrag && _subtitleDragOriginal is not null)
        {
            _subtitlePlacement = ResizeOrMove(_subtitleDragOriginal, _dragStartNormalized.Value, current, _dragKind);
            RenderOverlays();
            return;
        }
        if (_dragKind == DragKind.Create)
        {
            var x = Math.Min(_dragStartNormalized.Value.X, current.X);
            var y = Math.Min(_dragStartNormalized.Value.Y, current.Y);
            var width = Math.Abs(current.X - _dragStartNormalized.Value.X);
            var height = Math.Abs(current.Y - _dragStartNormalized.Value.Y);
            _draftRegion = RegionWithCurrentSettings(x, y, width, height, string.Empty);
            RenderOverlays();
            return;
        }
        if (_dragOriginal is null || _document.Selected is null) return;
        var updated = ResizeOrMove(_dragOriginal, _dragStartNormalized.Value, current, _dragKind);
        if (!_dragHistoryCaptured)
        {
            _document.BeginChange();
            _dragHistoryCaptured = true;
        }
        _document.ReplaceSelected(updated, capture: false);
        SetCoordinateBoxes(updated.X, updated.Y, updated.Width, updated.Height);
        RenderDocument(renderInputs: false);
    }

    private void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e) => FinishDrag(e, commit: true);
    private void Overlay_PointerCanceled(object sender, PointerRoutedEventArgs e) => FinishDrag(e, commit: false);

    private void FinishDrag(PointerRoutedEventArgs e, bool commit)
    {
        if (_dragStartNormalized is null) return;
        if (_subtitleDrag)
        {
            if (!commit && _subtitleDragOriginal is not null) _subtitlePlacement = _subtitleDragOriginal;
            if (commit)
            {
                QueueProjectSave();
                TranslationStatusText.Text = "Đã lưu vị trí/kích thước khung phụ đề.";
            }
            _subtitleDrag = false;
            _subtitleDragOriginal = null;
            _dragStartNormalized = null;
            _dragKind = DragKind.None;
            Overlay.ReleasePointerCapture(e.Pointer);
            RenderOverlays();
            RefreshEditorActions();
            return;
        }
        if (!commit && _dragHistoryCaptured) _document.Undo();
        if (commit && _dragKind == DragKind.Create && _draftRegion is { Width: >= .002, Height: >= .002 } created)
        {
            _document.Add(created);
            _draftRegion = null;
            LoadSelectedIntoInputs();
            DocumentChanged($"Đã tạo vùng {_document.Regions.Count}.");
        }
        else if (commit && _dragHistoryCaptured)
        {
            if (_document.Selected is not null) LoadSelectedIntoInputs();
            DocumentChanged("Đã cập nhật vị trí/kích thước vùng.");
        }
        else
        {
            _draftRegion = null;
            RenderDocument();
        }
        _dragStartNormalized = null;
        _dragOriginal = null;
        _dragKind = DragKind.None;
        _dragHistoryCaptured = false;
        _subtitleDrag = false;
        _subtitleDragOriginal = null;
        Overlay.ReleasePointerCapture(e.Pointer);
        RefreshEditorActions();
    }

    private void EditorPage_LayoutUpdated(object? sender, object e)
    {
        var overlayChanged = Math.Abs(Overlay.ActualWidth - _lastOverlayWidth) >= .5
            || Math.Abs(Overlay.ActualHeight - _lastOverlayHeight) >= .5;
        if (overlayChanged)
        {
            _lastOverlayWidth = Overlay.ActualWidth;
            _lastOverlayHeight = Overlay.ActualHeight;
            RenderOverlays();
        }
        if (Math.Abs(RegionTimelineCanvas.ActualWidth - _lastTimelineWidth) >= .5)
        {
            _lastTimelineWidth = RegionTimelineCanvas.ActualWidth;
            RenderTimelineRegions();
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_inspectorMode != InspectorMode.Blur || e.Key is not (VirtualKey.Delete or VirtualKey.Back) || _jobId is not null || _document.Selected is null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        if (_document.RemoveSelected())
        {
            e.Handled = true;
            DocumentChanged("Đã xóa vùng chọn. Có thể Hoàn tác.");
        }
    }

    private void DocumentChanged(string status)
    {
        RenderDocument();
        QueueProjectSave();
        QueuePreviewRefresh();
        StatusText.Text = status;
    }

    private void RenderDocument(bool renderInputs = true)
    {
        RenderRegionList();
        RenderOverlays();
        RenderTimelineRegions();
        if (renderInputs && _document.Selected is not null) LoadSelectedIntoInputs();
        RefreshEditorActions();
    }

    private void RenderRegionList()
    {
        _syncingList = true;
        try
        {
            RegionList.Items.Clear();
            for (var index = 0; index < _document.Regions.Count; index++)
            {
                var region = _document.Regions[index];
                RegionList.Items.Add($"{index + 1}. {EffectLabel(region.Effect)} · x={region.X:P0} y={region.Y:P0} w={region.Width:P0} h={region.Height:P0} · {(region.WholeVideo ? "toàn video" : $"{region.Start:0.0}-{region.End:0.0}s")}");
            }
            RegionList.SelectedIndex = _document.SelectedIndex;
            if (_document.SelectedIndex >= 0) RegionList.ScrollIntoView(RegionList.Items[_document.SelectedIndex]);
        }
        finally { _syncingList = false; }
    }

    private void RenderOverlays()
    {
        Overlay.Children.Clear();
        if (_media is null) return;
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return;
        for (var index = 0; index < _document.Regions.Count; index++)
        {
            var region = _document.Regions[index];
            var selected = index == _document.SelectedIndex;
            var active = VideoEditorService.IsActiveAt(region, Timeline.Value);
            var rectangle = RegionRectangle(region, video,
                selected ? ColorHelper.FromArgb(255, 49, 142, 242) : active ? ColorHelper.FromArgb(230, 70, 200, 220) : ColorHelper.FromArgb(190, 130, 140, 150),
                selected ? ColorHelper.FromArgb(52, 49, 142, 242) : active ? ColorHelper.FromArgb(30, 70, 200, 220) : ColorHelper.FromArgb(18, 130, 140, 150),
                selected ? 2.5 : 1.5);
            Overlay.Children.Add(rectangle);
        }
        if (_draftRegion is not null)
        {
            Overlay.Children.Add(RegionRectangle(_draftRegion, video,
                ColorHelper.FromArgb(255, 255, 190, 60), ColorHelper.FromArgb(38, 255, 190, 60), 2));
        }
        if (_inspectorMode == InspectorMode.Blur && _document.Selected is { } selectedRegion) RenderHandles(selectedRegion, video);
        if (_subtitleSource is not null) RenderSubtitlePlacement(video);
    }

    private void RenderSubtitlePlacement(Rect video)
    {
        var placement = _subtitlePlacement;
        var left = video.X + placement.X * video.Width;
        var top = video.Y + placement.Y * video.Height;
        var width = Math.Max(1, placement.Width * video.Width);
        var height = Math.Max(1, placement.Height * video.Height);
        var active = _inspectorMode == InspectorMode.Subtitle && _translationJobId is null && _jobId is null && !_playerMode;
        var stroke = active ? ColorHelper.FromArgb(255, 255, 194, 72) : ColorHelper.FromArgb(210, 170, 170, 170);
        var rectangle = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = new SolidColorBrush(stroke),
            Fill = new SolidColorBrush(ColorHelper.FromArgb(94, 0, 0, 0)),
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 5, 3 },
            RadiusX = 4,
            RadiusY = 4,
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        Overlay.Children.Add(rectangle);

        var cue = CurrentSubtitleCue();
        var text = cue is null ? "Kéo để đặt vị trí phụ đề" :
            string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText;
        var preview = new TextBlock
        {
            Text = text,
            Width = Math.Max(1, width - 16),
            MaxHeight = Math.Max(1, height - 12),
            FontSize = Math.Clamp(video.Height * .032, 13, 30),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Canvas.SetLeft(preview, left + 8);
        Canvas.SetTop(preview, top + Math.Max(6, height * .20));
        Overlay.Children.Add(preview);
        if (active) RenderSubtitleHandles(placement, video, stroke);
    }

    private EditorSubtitleCue? CurrentSubtitleCue()
    {
        if (_subtitleSource is null) return null;
        var seconds = Timeline.Value;
        return _subtitleSource.Cues.FirstOrDefault(x => seconds >= x.Start && seconds <= x.End);
    }

    private void RenderSubtitleHandles(EditorSubtitlePlacement placement, Rect video, Windows.UI.Color stroke)
    {
        var x1 = video.X + placement.X * video.Width;
        var y1 = video.Y + placement.Y * video.Height;
        var x2 = x1 + placement.Width * video.Width;
        var y2 = y1 + placement.Height * video.Height;
        var xm = (x1 + x2) / 2;
        var ym = (y1 + y2) / 2;
        foreach (var point in new[]
        {
            new Point(x1, y1), new Point(xm, y1), new Point(x2, y1), new Point(x2, ym),
            new Point(x2, y2), new Point(xm, y2), new Point(x1, y2), new Point(x1, ym),
        })
        {
            var handle = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(handle, point.X - 4.5);
            Canvas.SetTop(handle, point.Y - 4.5);
            Overlay.Children.Add(handle);
        }
    }

    private Rectangle RegionRectangle(EditRegion region, Rect video, Windows.UI.Color stroke, Windows.UI.Color fill, double thickness)
    {
        var rectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(stroke),
            Fill = new SolidColorBrush(fill),
            StrokeThickness = thickness,
            RadiusX = 2,
            RadiusY = 2,
            Width = Math.Max(1, region.Width * video.Width),
            Height = Math.Max(1, region.Height * video.Height),
        };
        Canvas.SetLeft(rectangle, video.X + region.X * video.Width);
        Canvas.SetTop(rectangle, video.Y + region.Y * video.Height);
        return rectangle;
    }

    private void RenderHandles(EditRegion region, Rect video)
    {
        var x1 = video.X + region.X * video.Width;
        var y1 = video.Y + region.Y * video.Height;
        var x2 = x1 + region.Width * video.Width;
        var y2 = y1 + region.Height * video.Height;
        var xm = (x1 + x2) / 2;
        var ym = (y1 + y2) / 2;
        foreach (var point in new[]
        {
            new Point(x1, y1), new Point(xm, y1), new Point(x2, y1), new Point(x2, ym),
            new Point(x2, y2), new Point(xm, y2), new Point(x1, y2), new Point(x1, ym),
        })
        {
            var handle = new Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 49, 142, 242)),
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(handle, point.X - 4.5);
            Canvas.SetTop(handle, point.Y - 4.5);
            Overlay.Children.Add(handle);
        }
    }

    private void RenderTimelineRegions()
    {
        RegionTimelineCanvas.Children.Clear();
        if (_media is null || _media.Duration <= 0 || RegionTimelineCanvas.ActualWidth <= 0) return;
        for (var index = 0; index < _document.Regions.Count; index++)
        {
            var region = _document.Regions[index];
            var start = region.WholeVideo ? 0 : Math.Clamp(region.Start, 0, _media.Duration);
            var end = region.WholeVideo ? _media.Duration : Math.Clamp(region.End, start, _media.Duration);
            var bar = new Rectangle
            {
                Height = 3,
                Width = Math.Max(2, (end - start) / _media.Duration * RegionTimelineCanvas.ActualWidth),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(index == _document.SelectedIndex
                    ? ColorHelper.FromArgb(230, 49, 142, 242)
                    : ColorHelper.FromArgb(170, 70, 200, 220)),
            };
            Canvas.SetLeft(bar, start / _media.Duration * RegionTimelineCanvas.ActualWidth);
            Canvas.SetTop(bar, 2 + index % 3 * 4);
            RegionTimelineCanvas.Children.Add(bar);
        }
    }

    private (int Index, DragKind Kind) HitTestRegion(Point point)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return (-1, DragKind.None);
        if (_document.Selected is { } selected)
        {
            var handle = HitSelectedHandles(point, selected, video);
            if (handle != DragKind.None) return (_document.SelectedIndex, handle);
        }
        for (var index = _document.Regions.Count - 1; index >= 0; index--)
        {
            var region = _document.Regions[index];
            var left = video.X + region.X * video.Width;
            var top = video.Y + region.Y * video.Height;
            if (point.X >= left && point.X <= left + region.Width * video.Width && point.Y >= top && point.Y <= top + region.Height * video.Height)
                return (index, DragKind.Move);
        }
        return (-1, DragKind.Create);
    }

    private DragKind HitTestSubtitle(Point point)
    {
        if (_subtitleSource is null) return DragKind.None;
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return DragKind.None;
        var placement = _subtitlePlacement;
        var left = video.X + placement.X * video.Width;
        var top = video.Y + placement.Y * video.Height;
        var right = left + placement.Width * video.Width;
        var bottom = top + placement.Height * video.Height;
        const double tolerance = 10;
        var nearLeft = Math.Abs(point.X - left) <= tolerance;
        var nearRight = Math.Abs(point.X - right) <= tolerance;
        var nearTop = Math.Abs(point.Y - top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - bottom) <= tolerance;
        var withinX = point.X >= left - tolerance && point.X <= right + tolerance;
        var withinY = point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        if (nearLeft && nearTop) return DragKind.NorthWest;
        if (nearRight && nearTop) return DragKind.NorthEast;
        if (nearLeft && nearBottom) return DragKind.SouthWest;
        if (nearRight && nearBottom) return DragKind.SouthEast;
        if (nearTop && withinX) return DragKind.North;
        if (nearBottom && withinX) return DragKind.South;
        if (nearLeft && withinY) return DragKind.West;
        if (nearRight && withinY) return DragKind.East;
        if (point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom) return DragKind.Move;
        return DragKind.None;
    }

    private static DragKind HitSelectedHandles(Point point, EditRegion region, Rect video)
    {
        var left = video.X + region.X * video.Width;
        var top = video.Y + region.Y * video.Height;
        var right = left + region.Width * video.Width;
        var bottom = top + region.Height * video.Height;
        const double tolerance = 9;
        var nearLeft = Math.Abs(point.X - left) <= tolerance;
        var nearRight = Math.Abs(point.X - right) <= tolerance;
        var nearTop = Math.Abs(point.Y - top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - bottom) <= tolerance;
        var withinX = point.X >= left - tolerance && point.X <= right + tolerance;
        var withinY = point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        if (nearLeft && nearTop) return DragKind.NorthWest;
        if (nearRight && nearTop) return DragKind.NorthEast;
        if (nearLeft && nearBottom) return DragKind.SouthWest;
        if (nearRight && nearBottom) return DragKind.SouthEast;
        if (nearTop && withinX) return DragKind.North;
        if (nearBottom && withinX) return DragKind.South;
        if (nearLeft && withinY) return DragKind.West;
        if (nearRight && withinY) return DragKind.East;
        return DragKind.None;
    }

    private static EditRegion ResizeOrMove(EditRegion original, Point start, Point current, DragKind kind)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (kind == DragKind.Move)
        {
            return original with
            {
                X = Math.Clamp(original.X + dx, 0, 1 - original.Width),
                Y = Math.Clamp(original.Y + dy, 0, 1 - original.Height),
            };
        }
        var x1 = original.X;
        var y1 = original.Y;
        var x2 = original.X + original.Width;
        var y2 = original.Y + original.Height;
        if (kind is DragKind.West or DragKind.NorthWest or DragKind.SouthWest) x1 = Math.Clamp(x1 + dx, 0, x2 - .002);
        if (kind is DragKind.East or DragKind.NorthEast or DragKind.SouthEast) x2 = Math.Clamp(x2 + dx, x1 + .002, 1);
        if (kind is DragKind.North or DragKind.NorthEast or DragKind.NorthWest) y1 = Math.Clamp(y1 + dy, 0, y2 - .002);
        if (kind is DragKind.South or DragKind.SouthEast or DragKind.SouthWest) y2 = Math.Clamp(y2 + dy, y1 + .002, 1);
        return original with { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1 };
    }

    private static EditorSubtitlePlacement ResizeOrMove(EditorSubtitlePlacement original, Point start, Point current, DragKind kind)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (kind == DragKind.Move)
        {
            return original with
            {
                X = Math.Clamp(original.X + dx, 0, 1 - original.Width),
                Y = Math.Clamp(original.Y + dy, 0, 1 - original.Height),
            };
        }
        var x1 = original.X;
        var y1 = original.Y;
        var x2 = original.X + original.Width;
        var y2 = original.Y + original.Height;
        if (kind is DragKind.West or DragKind.NorthWest or DragKind.SouthWest) x1 = Math.Clamp(x1 + dx, 0, x2 - .05);
        if (kind is DragKind.East or DragKind.NorthEast or DragKind.SouthEast) x2 = Math.Clamp(x2 + dx, x1 + .05, 1);
        if (kind is DragKind.North or DragKind.NorthEast or DragKind.NorthWest) y1 = Math.Clamp(y1 + dy, 0, y2 - .04);
        if (kind is DragKind.South or DragKind.SouthEast or DragKind.SouthWest) y2 = Math.Clamp(y2 + dy, y1 + .04, 1);
        return original with { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1 };
    }

    private bool TryNormalize(Point point, out Point normalized)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0 || point.X < video.X || point.X > video.Right || point.Y < video.Y || point.Y > video.Bottom)
        {
            normalized = default;
            return false;
        }
        normalized = new Point((point.X - video.X) / video.Width, (point.Y - video.Y) / video.Height);
        return true;
    }

    private EditRegion RegionWithCurrentSettings(double x, double y, double width, double height, string id)
    {
        var duration = _media?.Duration ?? 0;
        var start = WholeToggle.IsOn || double.IsNaN(StartBox.Value) ? 0 : StartBox.Value;
        var end = WholeToggle.IsOn || double.IsNaN(EndBox.Value) ? duration : EndBox.Value;
        return new EditRegion(x, y, width, height, SelectedEffect(), (int)Math.Clamp(StrengthBox.Value, 2, 64), WholeToggle.IsOn, start, end, id);
    }

    private EditRegion? ReadRegionFromInputs(string id)
    {
        if (_media is null) return null;
        var x = RegionXBox.Value / 100;
        var y = RegionYBox.Value / 100;
        var width = RegionWidthBox.Value / 100;
        var height = RegionHeightBox.Value / 100;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.0001 || y + height > 1.0001) return null;
        return RegionWithCurrentSettings(x, y, width, height, id);
    }

    private void ValidateRegion(EditRegion region)
    {
        if (_media is null) throw new InvalidOperationException("Chưa chọn video.");
        _ = VideoEditorService.BuildFilter(new VideoEditRequest(_path ?? "x", ".", "x.mp4", _media.Width, _media.Height, _media.Duration, [region]));
    }

    private void LoadSelectedIntoInputs()
    {
        var region = _document.Selected;
        if (region is null) return;
        _syncingInputs = true;
        try
        {
            SetCoordinateBoxes(region.X, region.Y, region.Width, region.Height);
            SelectEffect(region.Effect);
            StrengthBox.Value = region.Strength;
            WholeToggle.IsOn = region.WholeVideo;
            StartBox.Value = region.Start;
            EndBox.Value = region.End;
            StartBox.IsEnabled = EndBox.IsEnabled = !region.WholeVideo && _jobId is null;
            RegionValidationText.Text = "Vùng đang chọn có thể kéo, resize hoặc sửa bằng các ô số.";
        }
        finally { _syncingInputs = false; }
    }

    private void SetCoordinateBoxes(double x, double y, double width, double height)
    {
        var previous = _syncingInputs;
        _syncingInputs = true;
        try
        {
            RegionXBox.Value = x * 100;
            RegionYBox.Value = y * 100;
            RegionWidthBox.Value = width * 100;
            RegionHeightBox.Value = height * 100;
        }
        finally { _syncingInputs = previous; }
    }

    private string SelectedEffect() => (EffectBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "blur";

    private void SelectEffect(string effect)
    {
        for (var index = 0; index < EffectBox.Items.Count; index++)
        {
            if (EffectBox.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), effect, StringComparison.OrdinalIgnoreCase))
            {
                EffectBox.SelectedIndex = index;
                return;
            }
        }
        EffectBox.SelectedIndex = 0;
    }

    private static string EffectLabel(string effect) => effect.ToLowerInvariant() switch
    {
        "mosaic" => "Mosaic",
        "cover" => "Che đen",
        _ => "Làm mờ",
    };

    private async Task PreparePlayerAsync(string path, bool directCompatible)
    {
        if (_player is not null)
        {
            _player.PlaybackSession.PositionChanged -= PlayerPositionChanged;
            _player.Dispose();
            _player = null;
        }
        _playerMode = false;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        PlaybackButton.Content = "Phát video";
        PlaybackButton.IsEnabled = directCompatible;
        FullscreenButton.IsEnabled = directCompatible;
        if (!directCompatible) return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer
        {
            AutoPlay = false,
            IsMuted = PreviewMuteToggle.IsOn,
            Volume = Math.Clamp(PreviewVolumeSlider.Value / 100, 0, 1),
        };
        player.Source = MediaSource.CreateFromStorageFile(file);
        player.PlaybackSession.PositionChanged += PlayerPositionChanged;
        player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
            StatusText.Text = "Player native lỗi; vẫn có thể dùng preview FFmpeg: " + args.ErrorMessage);
        _player = player;
        PreviewPlayer.SetMediaPlayer(player);
    }

    private async Task SetPlaybackModeAsync(bool enabled, bool play)
    {
        if (enabled && _player is null) throw new InvalidOperationException("Codec/container này dùng preview frame FFmpeg.");
        _playerMode = enabled;
        PreviewPlayer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        PlaybackButton.Content = enabled ? "Xem frame hiệu ứng" : "Phát video";
        if (_player is not null)
        {
            if (enabled)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, Timeline.Value));
                if (play) _player.Play();
                StatusText.Text = "Đang phát video gốc; quay về frame để xem chính xác hiệu ứng.";
            }
            else
            {
                _player.Pause();
                _syncingTimeline = true;
                Timeline.Value = Math.Clamp(_player.PlaybackSession.Position.TotalSeconds, Timeline.Minimum, Timeline.Maximum);
                _syncingTimeline = false;
                await UpdateFrameAsync();
                StatusText.Text = "Preview FFmpeg đang hiển thị hiệu ứng tại frame hiện tại.";
            }
        }
        RefreshEditorActions();
    }

    private void PlayerPositionChanged(MediaPlaybackSession sender, object args)
    {
        if (!_playerMode) return;
        var seconds = sender.Position.TotalSeconds;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_playerMode || _media is null) return;
            _syncingTimeline = true;
            Timeline.Value = Math.Clamp(seconds, Timeline.Minimum, Timeline.Maximum);
            _syncingTimeline = false;
            UpdateClock();
            RenderOverlays();
            RenderTimelineRegions();
        });
    }

    private Rect VideoRect()
    {
        if (_media is null || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0)
            return new Rect(0, 0, Overlay.ActualWidth, Overlay.ActualHeight);
        var source = _media.Width / (double)_media.Height;
        var host = Overlay.ActualWidth / Overlay.ActualHeight;
        return host > source
            ? new Rect((Overlay.ActualWidth - Overlay.ActualHeight * source) / 2, 0, Overlay.ActualHeight * source, Overlay.ActualHeight)
            : new Rect(0, (Overlay.ActualHeight - Overlay.ActualWidth / source) / 2, Overlay.ActualWidth, Overlay.ActualWidth / source);
    }

    private void QueueProjectSave()
    {
        if (_project is null) return;
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        var snapshot = ProjectSnapshot();
        _ = SaveProjectLaterAsync(snapshot, cancellation.Token);
    }

    private async Task SaveProjectLaterAsync(EditorProject project, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            await _application.SaveEditorProjectAsync(project, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DispatcherQueue.TryEnqueue(() => StatusText.Text = "Không tự lưu được project: " + error.Message);
        }
    }

    private async Task SaveProjectNowAsync()
    {
        if (_project is null) return;
        _saveCancellation?.Cancel();
        try { await _application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None); }
        catch (Exception error) { StatusText.Text = "Không lưu được project: " + error.Message; }
    }

    private EditorProject ProjectSnapshot() => (_project ?? throw new InvalidOperationException("Project Editor chưa mở.")) with
    {
        FileName = FileNameBox.Text,
        Regions = _document.Regions.ToArray(),
        Subtitle = _subtitleSource is null ? null : new EditorSubtitleProject(
            _subtitleSource.Path,
            _subtitleSource.Size,
            _subtitleSource.LastWriteUtcTicks,
            _subtitleSource.Sha256,
            _subtitleSource.Cues,
            _subtitlePlacement,
            _project!.Subtitle?.SkillName ?? "Dịch Trung Tu Tiên",
            _project.Subtitle?.SkillSha256 ?? TranslationSkillBundle.BuiltInSha256,
            _project.Subtitle?.OutputPath ?? string.Empty),
        Audio = _audioSettings,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private void RefreshEditorActions()
    {
        var idle = _jobId is null && _translationJobId is null;
        var hasMedia = _media is not null;
        OpenVideoButton.IsEnabled = idle;
        Overlay.IsHitTestVisible = idle && hasMedia && !_playerMode &&
            (_inspectorMode == InspectorMode.Blur || _inspectorMode == InspectorMode.Subtitle && _subtitleSource is not null);
        AddRegionButton.IsEnabled = idle && _draftRegion is not null;
        RemoveRegionButton.IsEnabled = idle && _document.Selected is not null;
        UndoButton.IsEnabled = idle && _document.CanUndo;
        RedoButton.IsEnabled = idle && _document.CanRedo;
        SubtitlePresetButton.IsEnabled = WatermarkPresetButton.IsEnabled = idle && hasMedia;
        var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));
        var audioChanged = _audioSettings.SourceMode != "keep";
        RenderButton.IsEnabled = idle && _path is not null && hasMedia && (_document.Regions.Count > 0 || subtitleReady || audioChanged) && !string.IsNullOrWhiteSpace(FileNameBox.Text);
        CancelButton.IsEnabled = _jobId is not null;
        Timeline.IsEnabled = idle && hasMedia;
        RefreshFrameButton.IsEnabled = idle && hasMedia && !_playerMode;
        PlaybackButton.IsEnabled = idle && _player is not null;
        FullscreenButton.IsEnabled = idle && _player is not null;
        RegionXBox.IsEnabled = RegionYBox.IsEnabled = RegionWidthBox.IsEnabled = RegionHeightBox.IsEnabled = idle && hasMedia;
        EffectBox.IsEnabled = StrengthBox.IsEnabled = WholeToggle.IsEnabled = idle && hasMedia;
        StartBox.IsEnabled = EndBox.IsEnabled = idle && hasMedia && !WholeToggle.IsOn;
        FileNameBox.IsEnabled = idle;
        ImportSrtButton.IsEnabled = idle;
        PrepareAiButton.IsEnabled = idle;
        var aiReady = false;
        try { aiReady = _application.LocalTranslationStatus.RuntimeReady && _application.LocalTranslationStatus.ModelReady; }
        catch { }
        TranslateButton.IsEnabled = idle && _project is not null && hasMedia && _subtitleSource is not null && aiReady;
        CancelTranslationButton.IsEnabled = _translationJobId is not null;
        OpenTranslatedSrtButton.IsEnabled = idle && File.Exists(_project?.Subtitle?.OutputPath);
        PreviewMuteToggle.IsEnabled = _player is not null && _jobId is null;
        PreviewVolumeSlider.IsEnabled = _player is not null && _jobId is null;
        SourceAudioModeBox.IsEnabled = idle && hasMedia;
        SourceAudioGainSlider.IsEnabled = idle && hasMedia && _audioSettings.SourceMode == "duck";
    }

    private static string FormatClock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}

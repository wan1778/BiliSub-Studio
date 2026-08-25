using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage : Page
{
    private enum DragKind { None, Create, Move, North, South, East, West, NorthEast, NorthWest, SouthEast, SouthWest }
    private enum InspectorMode { Subtitle, Blur, Audio, Image, Export }

    private readonly BiliSubApplication _application;
    private readonly IFilePickerService _picker;
    private readonly EditorPlaybackController _playback;
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
    private string? _asrJobId;
    private string? _ttsJobId;
    private IReadOnlyList<EditorCueSpeechTiming> _cueSpeechTiming = [];
    private EditorVoiceTrack? _voiceTrack;
    private bool _syncingTimeline;
    private bool _syncingInputs;
    private bool _syncingList;
    private bool _syncingAudio;
    private bool _syncingVoice;
    private InspectorMode _inspectorMode = InspectorMode.Subtitle;
    private Point? _dragStartNormalized;
    private EditRegion? _dragOriginal;
    private DragKind _dragKind;
    private bool _dragHistoryCaptured;
    private bool _subtitleDrag;
    private EditorSubtitlePlacement? _subtitleDragOriginal;
    private CancellationTokenSource? _previewCancellation;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _projectSaveTimer;
    private readonly SemaphoreSlim _projectSaveGate = new(1, 1);
    private EditorProject? _pendingProjectSave;
    private bool _projectSaveFlushInProgress;
    private int _previewRevision;
    private double _lastOverlayWidth = -1;
    private double _lastOverlayHeight = -1;
    private bool EditorBusy => _jobId is not null || _translationJobId is not null || _asrJobId is not null || _ttsJobId is not null || _playback.IsRendering;

    public EditorPage(BiliSubApplication application, IFilePickerService picker)
    {
        _application = application;
        _picker = picker;
        _playback = new EditorPlaybackController(this);
        InitializeComponent();
        Loaded += EditorPage_Loaded;
        LayoutUpdated += EditorPage_LayoutUpdated;
        Unloaded += EditorPage_Unloaded;
    }

    private void SetInspectorMode(InspectorMode mode)
    {
        _inspectorMode = mode;
        SubtitleModeButton.IsChecked = mode == InspectorMode.Subtitle;
        BlurModeButton.IsChecked = mode == InspectorMode.Blur;
        AudioModeButton.IsChecked = mode == InspectorMode.Audio;
        ExportModeButton.IsChecked = mode == InspectorMode.Export;
        if (_imageModeButton is not null) _imageModeButton.IsChecked = mode == InspectorMode.Image;
        SubtitleInspectorPanel.Visibility = mode == InspectorMode.Subtitle ? Visibility.Visible : Visibility.Collapsed;
        BlurInspectorPanel.Visibility = mode == InspectorMode.Blur ? Visibility.Visible : Visibility.Collapsed;
        AudioInspectorPanel.Visibility = mode == InspectorMode.Audio ? Visibility.Visible : Visibility.Collapsed;
        ExportInspectorPanel.Visibility = mode == InspectorMode.Export ? Visibility.Visible : Visibility.Collapsed;
        if (_imageInspectorPanel is not null) _imageInspectorPanel.Visibility = mode == InspectorMode.Image ? Visibility.Visible : Visibility.Collapsed;
        RenderOverlays();
        RenderImageOverlays();
        RefreshEditorActions();
    }

    internal Task RunLayoutSmokeAsync()
    {
        if (!ImportSrtButton.IsEnabled || !PrepareAiButton.IsEnabled)
            throw new InvalidOperationException("Editor phải cho phép chọn SRT và chuẩn bị AI trước khi chọn video.");
        if (CreateAsrButton.IsEnabled)
            throw new InvalidOperationException("Editor không được cho chạy ASR khi chưa có video nguồn.");
        if (!string.Equals(PlayerPlayPauseButton.Content?.ToString(), "▶", StringComparison.Ordinal))
            throw new InvalidOperationException("Editor preview phải có đúng một nút Play/Pause do playback controller sở hữu.");
        if (PreviewPlayer.AreTransportControlsEnabled)
            throw new InvalidOperationException("Editor không được bật native MediaPlayer transport trên preview.");
        if (_imageModeButton is null || _imageInspectorPanel is null || _imageOverlayCanvas is null)
            throw new InvalidOperationException("Editor phải khởi tạo Ảnh/logo từ lifecycle chính.");
        foreach (var mode in Enum.GetValues<InspectorMode>())
        {
            SetInspectorMode(mode);
            var visible = new[] { SubtitleInspectorPanel, BlurInspectorPanel, AudioInspectorPanel, _imageInspectorPanel, ExportInspectorPanel }
                .Count(panel => panel?.Visibility == Visibility.Visible);
            if (visible != 1) throw new InvalidOperationException("Editor tool state phải có đúng một inspector.");
        }
        SetInspectorMode(InspectorMode.Subtitle);
        return Task.CompletedTask;
    }

    private async void EditorPage_Unloaded(object sender, RoutedEventArgs e)
    {
        await _editorTabLifecycleGate.WaitAsync();
        try
        {
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            StopProjectSaveTimer();
            try
            {
                CleanupEditorParity();
                await _playback.UnloadAsync();
                try { await SaveImageSidecarAsync(); } catch { }
                await SaveProjectNowAsync();
            }
            finally
            {
                CleanupProjectAutosave();
            }
        }
        finally
        {
            _editorTabLifecycleGate.Release();
        }
    }

    private async void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        try { await OpenVideoAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được video: " + error.Message;
            RefreshEditorActions();
        }
    }

    private async Task OpenVideoAsync()
    {
        // SOURCE-02: cancel is a no-op. Do not touch the current source/project before a real path exists.
        var pickedPath = await _picker.PickVideoAsync();
        if (string.IsNullOrWhiteSpace(pickedPath)) return;

        var candidatePath = EditorSourceSelection.NormalizeCandidatePath(pickedPath);
        var sameSourcePath = EditorSourceSelection.IsSameSource(_path, candidatePath);

        // SOURCE-05 / PROJECT-05: probe even when the path is unchanged. A file can
        // be replaced outside the app while keeping the exact same path.
        MediaPreviewInfo candidateMedia;
        try
        {
            candidateMedia = await _application.Media.ProbeAsync(candidatePath, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException(
                "File video không hợp lệ, đã hỏng hoặc codec không đọc được. Project hiện tại vẫn được giữ nguyên. " + error.Message,
                error);
        }

        var currentSourceChanged = _project is not null && _path is not null && _media is not null
            && !EditorProjectStore.SourceFingerprintMatchesCurrent(
                _project.Source, _path, _media.Width, _media.Height, _media.Duration);
        if (sameSourcePath && !currentSourceChanged)
        {
            StatusText.Text = "Video này đang được mở và fingerprint không đổi; giữ nguyên project và preview hiện tại.";
            return;
        }

        if (currentSourceChanged)
        {
            // Do not let a pending stale snapshot race the replacement-source load.
            StopProjectSaveTimer();
            _pendingProjectSave = null;
            await WaitForProjectSaveIdleAsync();
            ArchiveImageSidecarForSourceChange(_project!.Id);
        }

        EditorProject candidateProject;
        try
        {
            candidateProject = await _application.LoadEditorProjectAsync(candidatePath, candidateMedia, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException(
                "Không mở được project của video đã chọn. Project hiện tại vẫn được giữ nguyên. " + error.Message,
                error);
        }

        var pendingSubtitle = _project is null ? _subtitleSource : null;
        var pendingPlacement = _subtitlePlacement;

        // A replaced source invalidates the open state. Saving it now would stamp
        // old regions/subtitle/voice onto the new file, so only save an unchanged source.
        if (!currentSourceChanged)
            await SaveCurrentSourceStateForSwitchAsync();
        await DisposePreviewForSourceChangeAsync();

        _path = candidatePath;
        _media = candidateMedia;
        _project = candidateProject;
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
        await SyncSubtitleCueEditorAsync();
        await RestoreSpeechAndVoiceAsync();
        await EnsureImageProjectLoadedAsync();
        _draftRegion = null;
        Timeline.Maximum = Math.Max(0.1, _media.Duration);
        Timeline.Value = 0;
        PathText.Text = candidatePath;
        MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · preview xử lý dùng cùng pipeline FFmpeg với export";
        _syncingInputs = true;
        try
        {
            FileNameBox.Text = _project.FileName;
            StartBox.Maximum = EndBox.Maximum = _media.Duration;
            EndBox.Value = _media.Duration;
        }
        finally { _syncingInputs = false; }
        await _playback.PrepareAsync();
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        else SetCoordinateBoxes(0, 0, 0, 0);
        RenderDocument();
        RenderImageList();
        RenderImageOverlays();
        await UpdateFrameAsync();
        StatusText.Text = currentSourceChanged
            ? "Video nguồn đã thay đổi ngoài ứng dụng; project cũ đã được lưu trữ và state dẫn xuất đã reset cho file mới."
            : _document.Regions.Count > 0
                ? $"Đã mở lại project với {_document.Regions.Count} vùng."
                : _subtitleSource is not null
                    ? $"Đã mở lại SRT {_subtitleSource.Cues.Count} câu; khung phụ đề có thể kéo/resize trực tiếp."
                    : "Chọn SRT tiếng Trung để bắt đầu Vietsub, hoặc kéo frame để tạo vùng hiệu ứng.";
        RefreshEditorActions();
        QueueProjectSave();
    }

    private async Task SaveCurrentSourceStateForSwitchAsync()
    {
        if (_project is null) return;
        var snapshot = ProjectSnapshot();
        await FlushProjectSaveAsync(snapshot);
        await SaveImageSidecarAsync();
    }

    private async Task DisposePreviewForSourceChangeAsync()
    {
        var frameCancellation = _previewCancellation;
        _previewCancellation = null;
        if (frameCancellation is not null)
        {
            frameCancellation.Cancel();
            frameCancellation.Dispose();
        }
        ++_previewRevision;
        await _playback.DisposeForSourceChangeAsync();
    }

    private async Task WaitForProjectSaveIdleAsync()
    {
        await _projectSaveGate.WaitAsync();
        _projectSaveGate.Release();
    }

    private bool CurrentSourceFingerprintMatches() =>
        _project is not null && _path is not null && _media is not null
        && EditorProjectStore.SourceFingerprintMatchesCurrent(
            _project.Source, _path, _media.Width, _media.Height, _media.Duration);

    private void EnsureCurrentSourceFingerprint()
    {
        if (!CurrentSourceFingerprintMatches())
            throw new InvalidDataException("Video nguồn đã thay đổi ngoài ứng dụng; hãy mở lại chính video này để reset project trước khi Preview/Export.");
    }

    private bool CurrentSubtitleFingerprintMatches() =>
        _subtitleSource is null || EditorSubtitleDocument.SourceFingerprintMatchesCurrent(_subtitleSource);

    private void EnsureCurrentSubtitleFingerprint()
    {
        if (_subtitleSource is not null && !CurrentSubtitleFingerprintMatches())
            throw new InvalidDataException("SRT nguồn đã thay đổi ngoài ứng dụng; hãy chọn lại file SRT để reset Vietsub/voice trước khi tiếp tục.");
    }

    private async Task RestoreSubtitleAsync(EditorSubtitleProject? saved)
    {
        _subtitleSource = null;
        _subtitlePlacement = EditorSubtitlePlacement.Default;
        SrtPathText.Text = "Chưa chọn SRT.";
        SrtSummaryText.Text = "Skill: Dịch Trung Tu Tiên (tích hợp, đã khóa SHA-256).";
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
            _syncingVoice = true;
            try { KaraokeToggle.IsOn = saved.Karaoke; } finally { _syncingVoice = false; }
            SrtPathText.Text = saved.SourcePath;
            UpdateSubtitleSummary();
            TranslationStatusText.Text = _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))
                ? "Bản Vietsub đã hoàn tất; có thể mở thư mục SRT Việt."
                : "Đã khôi phục SRT và các câu dịch/checkpoint hiện có.";
        }
        catch (Exception error)
        {
            TranslationStatusText.Text = "Không khôi phục được SRT: " + error.Message;
        }
    }

    private async Task RestoreSpeechAndVoiceAsync()
    {
        _cueSpeechTiming = [];
        _voiceTrack = null;
        if (_project?.Speech is not { Status: "complete" } speech)
        {
            AsrStatusText.Text = "Whisper chưa phân tích video. Vào Âm thanh để lấy word timing, khoảng lặng và Nam/Nữ gợi ý.";
            VoiceStatusText.Text = "Chưa phân tích nhịp thoại.";
            UpdateCurrentCueVoiceUi();
            return;
        }
        try
        {
            await RefreshSpeechTimingForSubtitleAsync();
            AsrStatusText.Text = $"Whisper timing đã lưu · {speech.WordCount} từ · {speech.Device.ToUpperInvariant()}/{speech.ComputeType} · benchmark {speech.ProbeRealtimeFactor:0.00}×.";
        }
        catch (Exception error)
        {
            _project = _project with { Speech = null, Tts = null };
            _cueSpeechTiming = [];
            VoiceStatusText.Text = "Whisper timing cũ không còn hợp lệ: " + error.Message;
            AsrStatusText.Text = VoiceStatusText.Text;
            UpdateCurrentCueVoiceUi();
            return;
        }
        if (_project.Tts is { Status: "complete" } tts && File.Exists(tts.VoiceTrack.Path))
        {
            _voiceTrack = tts.VoiceTrack;
            VoiceStatusText.Text = tts.ReviewCount == 0
                ? $"Voice Việt local đã sẵn sàng · {tts.CueCount} câu · Preview/Export dùng cùng track."
                : $"Voice Việt local đã sẵn sàng · {tts.CueCount} câu · {tts.ReviewCount} câu cần xem lại.";
        }
        else
        {
            VoiceStatusText.Text = "Đã có nhịp thoại. Vietsub đầy đủ rồi bấm Tạo voice Việt local.";
        }
        UpdateCurrentCueVoiceUi();
    }

    private async Task RefreshSpeechTimingForSubtitleAsync()
    {
        _cueSpeechTiming = [];
        if (_project?.Speech is not { Status: "complete" } speech || _subtitleSource is null) return;
        _cueSpeechTiming = await _application.LoadEditorCueSpeechTimingAsync(
            speech.AnalysisPath, speech.AnalysisSha256, _subtitleSource.Cues, CancellationToken.None);
        UpdateCurrentCueVoiceUi();
    }

    private async void ImportSubtitle_Click(object sender, RoutedEventArgs e)
    {
        try { await ImportSubtitleAsync(); }
        catch (OperationCanceledException) { }
        catch (InvalidDataException error)
        {
            TranslationStatusText.Text = "SRT không hợp lệ: " + error.Message;
            RefreshEditorActions();
        }
        catch (Exception error)
        {
            TranslationStatusText.Text = "Không nhập được SRT: " + error.Message;
            RefreshEditorActions();
        }
    }

    private async Task ImportSubtitleAsync()
    {
        // SUB-02 / SUB-04: picker cancel is a no-op and works before a video exists.
        var path = await _picker.PickSubtitleAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        // SUB-05: fully validate the candidate before replacing the current SRT state.
        EditorSubtitleSource candidate;
        try
        {
            candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var detail = error is InvalidDataException or FileNotFoundException
                ? error.Message
                : "Không đọc được file SRT. " + error.Message;
            throw new InvalidDataException(detail, error);
        }

        var subtitleSourceChanged = _subtitleSource is not null
            && (!string.Equals(Path.GetFullPath(_subtitleSource.Path), Path.GetFullPath(candidate.Path), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_subtitleSource.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase));
        _subtitleSource = candidate;
        _subtitlePlacement = EditorSubtitlePlacement.Default;
        if (_project is not null)
        {
            // SUB-03 / PROJECT-06: a new SRT invalidates TTS and cue-keyed voice overrides from the old SRT.
            _voiceTrack = null;
            _project = _project with
            {
                Tts = null,
                VoiceOverrides = subtitleSourceChanged ? null : _project.VoiceOverrides,
            };
            AttachSubtitleToProject(string.Empty);
            await RefreshSpeechTimingForSubtitleAsync();
        }

        await SyncSubtitleCueEditorAsync();
        SrtPathText.Text = candidate.Path;
        AsrStatusText.Text = _project?.Speech is { Status: "complete" }
            ? "Đang dùng SRT đã chọn; Whisper timing của video vẫn được giữ và ánh xạ vào SRT này."
            : "Đã dùng SRT đã chọn. Vào Âm thanh để chạy Whisper word timing/nhịp thoại.";
        TranslationProgress.Value = 0;
        TranslationStatusText.Text = _media is null
            ? "Đã khóa timecode và thứ tự. Có thể chuẩn bị AI ngay; hãy chọn video để đặt khung và Vietsub."
            : "Đã khóa timecode và thứ tự. Kéo/resize khung phụ đề trên preview rồi bấm Chuẩn bị AI.";
        UpdateSubtitleSummary();
        RenderOverlays();
        if (_project is not null) await SaveProjectNowAsync();
        RefreshEditorActions();
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
                outputPath,
                KaraokeToggle.IsOn),
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

    private async void CreateAsr_Click(object sender, RoutedEventArgs e)
    {
        if (_asrJobId is not null || _project is null || _media is null || string.IsNullOrWhiteSpace(_path)) return;
        try
        {
            _asrJobId = _application.StartEditorAsr(new EditorAsrRequest(_project.Id, _path, _media.Duration));
            VoiceProgress.Value = 0;
            AsrStatusText.Text = "Đang benchmark Whisper local rồi phân tích word timing, khoảng lặng và chất giọng Nam/Nữ.";
            VoiceStatusText.Text = AsrStatusText.Text;
            RefreshEditorActions();
            await PollAsrJobAsync();
        }
        catch (Exception error)
        {
            _asrJobId = null;
            AsrStatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private async Task PollAsrJobAsync()
    {
        while (_asrJobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_asrJobId);
            VoiceProgress.Value = snapshot.Progress;
            AsrStatusText.Text = snapshot.Message;
            if (snapshot.Done)
            {
                if (snapshot.Result is EditorAsrResult result && _project is not null)
                {
                    if (_subtitleSource is null)
                    {
                        _subtitleSource = result.Source;
                        _subtitlePlacement = EditorSubtitlePlacement.Default;
                        AttachSubtitleToProject(string.Empty);
                        SrtPathText.Text = result.Source.Path;
                        TranslationStatusText.Text = "Video chưa có SRT nên đã giữ thêm SRT Trung do Whisper tạo; mục chính vẫn là word timing/nhịp thoại.";
                        await SyncSubtitleCueEditorAsync();
                    }
                    _voiceTrack = null;
                    _project = _project with
                    {
                        Asr = null,
                        Speech = new EditorSpeechProject(
                            "complete",
                            result.ModelName,
                            result.ModelRevision,
                            result.Device,
                            result.ComputeType,
                            result.AnalysisPath,
                            result.AnalysisSha256,
                            result.SegmentCount,
                            result.WordCount,
                            result.ProbeRealtimeFactor),
                        Tts = null,
                    };
                    await RefreshSpeechTimingForSubtitleAsync();
                    UpdateSubtitleSummary();
                    AsrStatusText.Text = $"Whisper timing hoàn tất · {result.WordCount} từ · {result.Device.ToUpperInvariant()}/{result.ComputeType} · benchmark {result.ProbeRealtimeFactor:0.00}×.";
                    VoiceStatusText.Text = $"Đã có word timing và Nam/Nữ gợi ý cho video. Có thể Vietsub rồi tạo voice Việt local.";
                    RenderOverlays();
                    await SaveProjectNowAsync();
                }
                _asrJobId = null;
                RefreshEditorActions();
                break;
            }
            await Task.Delay(350);
        }
    }

    private async void GenerateTts_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsJobId is not null || _project is null || _media is null || _subtitleSource is null || string.IsNullOrWhiteSpace(_path)) return;
        if (!CurrentSubtitleFingerprintMatches())
        {
            VoiceStatusText.Text = "SRT nguồn đã thay đổi ngoài ứng dụng; hãy chọn lại SRT trước khi tạo voice.";
            return;
        }
        if (_project.Speech is not { Status: "complete" } speech)
        {
            VoiceStatusText.Text = "Hãy chạy Phân tích nhịp + Nam/Nữ trước khi tạo voice.";
            return;
        }
        if (_subtitleSource.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText)))
        {
            VoiceStatusText.Text = "Hãy Vietsub đầy đủ trước khi tạo voice Việt.";
            return;
        }
        try
        {
            _ttsJobId = _application.StartEditorTts(new EditorTtsRequest(
                _project.Id,
                _path,
                _media.Duration,
                _subtitleSource,
                speech.AnalysisPath,
                speech.AnalysisSha256,
                _project.VoiceOverrides));
            VoiceProgress.Value = 0;
            VoiceStatusText.Text = "Đang chuẩn bị NghiTTS/Piper local và fit voice theo nhịp Whisper...";
            RefreshEditorActions();
            await PollTtsJobAsync();
        }
        catch (Exception error)
        {
            _ttsJobId = null;
            VoiceStatusText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private async Task PollTtsJobAsync()
    {
        while (_ttsJobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_ttsJobId);
            VoiceProgress.Value = snapshot.Progress;
            VoiceStatusText.Text = snapshot.Message;
            if (snapshot.Done)
            {
                if (snapshot.Result is EditorTtsResult result && _project is not null)
                {
                    EnsureCurrentSubtitleFingerprint();
                    _voiceTrack = result.VoiceTrack;
                    _project = _project with
                    {
                        Tts = new EditorTtsProject(
                            "complete",
                            result.Engine,
                            result.EngineVersion,
                            result.MaleVoice,
                            result.FemaleVoice,
                            result.ManifestPath,
                            result.ManifestSha256,
                            result.VoiceTrack,
                            result.Cues.Count,
                            result.ReviewCount),
                    };
                    VoiceStatusText.Text = result.ReviewCount == 0
                        ? $"Voice Việt hoàn tất · {result.Cues.Count} câu đều fit timing · đã vào Xem bản chỉnh."
                        : $"Voice Việt hoàn tất · {result.Cues.Count} câu · {result.ReviewCount} câu cần xem lại; track vẫn có thể preview.";
                    await SaveProjectNowAsync();
                    QueuePreviewRefresh();
                }
                _ttsJobId = null;
                RefreshEditorActions();
                break;
            }
            await Task.Delay(350);
        }
    }

    private void CancelVoice_Click(object sender, RoutedEventArgs e)
    {
        var job = _ttsJobId ?? _asrJobId;
        if (job is null) return;
        _application.CancelJob(job);
        VoiceStatusText.Text = _ttsJobId is not null
            ? "Đang dừng TTS local và thu hồi process..."
            : "Đang dừng Whisper và thu hồi Python/FFmpeg; checkpoint timing đã hoàn tất vẫn được giữ.";
    }

    private void Karaoke_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingVoice) return;
        QueueProjectSave();
        if (!_playback.IsPreviewMode) QueuePreviewRefresh();
        NotifyEditorCompositeChanged();
    }

    private void CurrentCueVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingVoice || _project is null) return;
        var cue = CurrentSubtitleCue();
        if (cue is null) return;
        var value = (CurrentCueVoiceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim().ToLowerInvariant() ?? "auto";
        var overrides = new Dictionary<string, string>(_project.VoiceOverrides ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (value is "male" or "female") overrides[cue.Id] = value;
        else overrides.Remove(cue.Id);
        _voiceTrack = null;
        _project = _project with { VoiceOverrides = overrides, Tts = null };
        VoiceStatusText.Text = value switch
        {
            "male" => "Đã ép câu hiện tại dùng voice Nam. Hãy tạo lại voice Việt để áp dụng.",
            "female" => "Đã ép câu hiện tại dùng voice Nữ. Hãy tạo lại voice Việt để áp dụng.",
            _ => "Câu hiện tại trở lại tự động Nam/Nữ. Hãy tạo lại voice Việt để áp dụng.",
        };
        QueueProjectSave();
        RefreshEditorActions();
    }

    private void UpdateCurrentCueVoiceUi()
    {
        if (!IsLoaded) return;
        _syncingVoice = true;
        try
        {
            var cue = CurrentSubtitleCue();
            var value = cue is not null && _project?.VoiceOverrides is { } overrides && overrides.TryGetValue(cue.Id, out var selected)
                ? selected
                : "auto";
            for (var index = 0; index < CurrentCueVoiceBox.Items.Count; index++)
            {
                if (CurrentCueVoiceBox.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentCueVoiceBox.SelectedIndex = index;
                    break;
                }
            }
        }
        finally { _syncingVoice = false; }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        try { await TranslateAllWithManualStateAsync(); }
        catch (Exception error)
        {
            _translationJobId = null;
            TranslationStatusText.Text = error.Message;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
    }

    private void CancelTranslation_Click(object sender, RoutedEventArgs e)
    {
        var job = _translationJobId;
        if (job is null) return;
        _application.CancelJob(job);
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
                    _voiceTrack = null;
                    _project = _project with
                    {
                        Tts = null,
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

    private async void SaveKaraokeAss_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _subtitleSource is null) return;
        if (!CurrentSubtitleFingerprintMatches())
        {
            TranslationStatusText.Text = "SRT nguồn đã thay đổi ngoài ứng dụng; hãy chọn lại SRT trước khi lưu ASS karaoke.";
            return;
        }
        var burn = CompletedSubtitleBurn();
        if (burn is null || !KaraokeToggle.IsOn || _cueSpeechTiming.Count == 0)
        {
            TranslationStatusText.Text = "Cần Vietsub đầy đủ và Whisper word timing trước khi lưu Caption ASS.";
            return;
        }
        try
        {
            var output = await _application.SaveEditorKaraokeAssAsync(
                burn, _media.Width, _media.Height, _subtitleSource.Path, CancellationToken.None);
            TranslationStatusText.Text = "Đã lưu Caption ASS: " + output;
        }
        catch (Exception error)
        {
            TranslationStatusText.Text = "Không lưu được ASS karaoke: " + error.Message;
        }
    }

    private async void OpenTranslatedSrt_Click(object sender, RoutedEventArgs e)
    {
        if (_subtitleSource is not null && !CurrentSubtitleFingerprintMatches())
        {
            TranslationStatusText.Text = "SRT nguồn đã thay đổi ngoài ứng dụng; output Vietsub cũ đã bị khóa cho tới khi chọn lại SRT.";
            return;
        }
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
        UpdateClock();
        RenderOverlays();
        UpdateCurrentCueVoiceUi();
        if (_playback.IsPreviewMode && !_syncingTimeline && _media is not null)
        {
            _ = _playback.SeekAsync(Math.Clamp(e.NewValue, Timeline.Minimum, Timeline.Maximum));
            return;
        }
        if (!_playback.IsPreviewMode && !_syncingTimeline && _media is not null) QueuePreviewRefresh();
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
        if (_path is null || _media is null || _playback.IsPreviewMode) return;
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

    private void WholeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingInputs) return;
        if (!WholeToggle.IsOn && _media is not null && _document.Selected is null)
        {
            var range = EditorRegionTimeScope.CreateDefaultTimedRange(Timeline.Value, _media.Duration);
            _syncingInputs = true;
            try
            {
                StartBox.Value = range.Start;
                EndBox.Value = range.End;
            }
            finally { _syncingInputs = false; }
        }
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private void EditorUseCurrentStart_Click(object sender, RoutedEventArgs e) =>
        SetTimedBoundaryFromCurrent(setStart: true);

    private void EditorUseCurrentEnd_Click(object sender, RoutedEventArgs e) =>
        SetTimedBoundaryFromCurrent(setStart: false);

    private void SetTimedBoundaryFromCurrent(bool setStart)
    {
        if (_media is null || WholeToggle.IsOn || EditorBusy || _playback.IsPreviewMode) return;
        var value = Math.Clamp(Timeline.Value, 0, _media.Duration);
        _syncingInputs = true;
        try
        {
            if (setStart) StartBox.Value = value;
            else EndBox.Value = value;
        }
        finally { _syncingInputs = false; }
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private void EffectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingInputs) return;
        var effect = SelectedEffect();
        if (EffectUsesStrength(effect))
        {
            var normalized = NormalizeEffectStrength(effect, StrengthBox.Value, CurrentStoredStrength());
            if (StrengthBox.Value != normalized)
            {
                _syncingInputs = true;
                try { StrengthBox.Value = normalized; }
                finally { _syncingInputs = false; }
            }
        }
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private void EditInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded || _syncingInputs) return;
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private void EffectStrength_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded || _syncingInputs) return;
        var effect = SelectedEffect();
        if (!EffectUsesStrength(effect)) return;
        if (!TryEffectStrength(effect, args.NewValue, out var strength))
        {
            var name = effect == "mosaic" ? "Mosaic" : "làm mờ";
            RegionValidationText.Text = $"Cường độ {name} phải từ {EffectStrengthMinimum(effect)} đến {EffectStrengthMaximum(effect)}.";
            RefreshEditorActions();
            return;
        }
        if (sender.Value != strength)
        {
            _syncingInputs = true;
            try { sender.Value = strength; }
            finally { _syncingInputs = false; }
        }
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private static bool TryEffectStrength(string effect, double value, out int strength) =>
        effect == "mosaic"
            ? EditorMosaicStrength.TryFromInput(value, out strength)
            : EditorBlurStrength.TryFromInput(value, out strength);

    private static bool EffectUsesStrength(string effect) =>
        !string.Equals(effect, "cover", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeEffectStrength(string effect, double value, int fallback) =>
        effect == "mosaic"
            ? EditorMosaicStrength.NormalizeInput(value, fallback)
            : EditorBlurStrength.NormalizeInput(value, fallback);

    private static int EffectStrengthMinimum(string effect) =>
        effect == "mosaic" ? EditorMosaicStrength.Minimum : EditorBlurStrength.Minimum;

    private static int EffectStrengthMaximum(string effect) =>
        effect == "mosaic" ? EditorMosaicStrength.Maximum : EditorBlurStrength.Maximum;

    private int CurrentStoredStrength() =>
        _document.Selected?.Strength
        ?? _draftRegion?.Strength
        ?? SelectedEffect() switch
        {
            "mosaic" => EditorMosaicStrength.Default,
            "cover" => EditorCoverEffect.StoredStrength,
            _ => EditorBlurStrength.Default,
        };

    private void RegionCoordinates_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded || _syncingInputs) return;
        if (ApplyInputsToDocument()) NotifyEditorCompositeChanged();
    }

    private bool ApplyInputsToDocument()
    {
        var region = ReadRegionFromInputs(_document.Selected?.Id ?? string.Empty);
        if (region is null)
        {
            _draftRegion = null;
            RegionValidationText.Text = _media is null
                ? "Chọn video rồi kéo để tạo vùng."
                : "X/Y/W/H phải nằm trong video; W và H phải đạt ít nhất 2 pixel nguồn.";
            RefreshEditorActions();
            return false;
        }
        try
        {
            ValidateRegion(region);
            if (_document.Selected is not null)
            {
                if (region == _document.Selected) return false;
                _document.ReplaceSelected(region);
                _draftRegion = null;
                RegionValidationText.Text = "Đã cập nhật vùng đang chọn.";
                RenderDocument();
                QueueProjectSave();
                QueuePreviewRefresh();
                RefreshEditorActions();
                return true;
            }

            if (_draftRegion == region) return false;
            _draftRegion = region;
            RegionValidationText.Text = "Tọa độ hợp lệ; bấm Thêm để lưu vùng.";
            RenderOverlays();
            RefreshEditorActions();
            return true;
        }
        catch (Exception error)
        {
            _draftRegion = null;
            RegionValidationText.Text = error.Message;
            RefreshEditorActions();
            return false;
        }
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
        TryDeleteSelectedRegion();
    }

    private bool TryDeleteSelectedRegion()
    {
        if (EditorBusy || _playback.IsPreviewMode || _dragStartNormalized is not null) return false;
        if (!_document.RemoveSelected()) return false;
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        else SetCoordinateBoxes(0, 0, 0, 0);
        DocumentChanged("Đã xóa vùng chọn. Có thể Hoàn tác.");
        return true;
    }

    private void RegionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        SelectRegion(RegionList.SelectedIndex);
        RenderDocument(renderInputs: false);
    }

    private void SelectRegion(int index)
    {
        _document.Select(index);
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        TryUndoDocument();
    }

    private bool TryUndoDocument()
    {
        if (EditorBusy || _playback.IsPreviewMode || _dragStartNormalized is not null) return false;
        if (!_document.Undo()) return false;
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        else SetCoordinateBoxes(0, 0, 0, 0);
        DocumentChanged("Đã hoàn tác.");
        return true;
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        TryRedoDocument();
    }

    private bool TryRedoDocument()
    {
        if (EditorBusy || _playback.IsPreviewMode || _dragStartNormalized is not null) return false;
        if (!_document.Redo()) return false;
        _draftRegion = null;
        if (_document.Selected is not null) LoadSelectedIntoInputs();
        else SetCoordinateBoxes(0, 0, 0, 0);
        DocumentChanged("Đã làm lại.");
        return true;
    }

    private void SubtitlePreset_Click(object sender, RoutedEventArgs e)
    {
        TryAddRegionPreset(EditorRegionPresetKind.SubtitleBottom);
    }

    private void WatermarkPreset_Click(object sender, RoutedEventArgs e)
    {
        TryAddRegionPreset(EditorRegionPresetKind.WatermarkTopRight);
    }

    private bool TryAddRegionPreset(EditorRegionPresetKind preset)
    {
        if (_media is null || EditorBusy || _playback.IsPreviewMode || _dragStartNormalized is not null) return false;
        var region = EditorRegionGeometry.CreatePreset(preset, _media.Width, _media.Height, _media.Duration);
        if (region is null)
        {
            StatusText.Text = "Preset không phù hợp với kích thước video nguồn.";
            return false;
        }

        _document.Add(region);
        _draftRegion = null;
        LoadSelectedIntoInputs();
        var status = preset == EditorRegionPresetKind.SubtitleBottom
            ? "Đã thêm vùng làm mờ phụ đề phía dưới."
            : "Đã thêm vùng Mosaic che logo góc phải.";
        DocumentChanged(status);
        return true;
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_syncingInputs)
        {
            QueueProjectSave();
            RefreshEditorActions();
        }
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
            "duck" => $"Preview và video xuất sẽ giữ {_audioSettings.SourceGain:P0} mức âm thanh gốc.",
            "mute" => "Preview và video xuất sẽ không có âm thanh gốc.",
            _ => "Preview và video xuất sẽ giữ nguyên âm thanh gốc.",
        };
        QueueProjectSave();
        RefreshEditorActions();
        NotifyEditorCompositeChanged();
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
                "duck" => $"Preview và video xuất sẽ giữ {_audioSettings.SourceGain:P0} mức âm thanh gốc.",
                "mute" => "Preview và video xuất sẽ không có âm thanh gốc.",
                _ => "Preview và video xuất sẽ giữ nguyên âm thanh gốc.",
            };
        }
        finally { _syncingAudio = false; }
        RefreshEditorActions();
    }

    private EditorSubtitleBurn? CompletedSubtitleBurn() =>
        _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))
            ? new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn)
            : null;

    private EditorSubtitleBurn? PreviewSubtitleBurn()
    {
        var source = _subtitleSource;
        if (source is null) return null;
        var cues = source.Cues.Select(cue => cue with
        {
            VietnameseText = string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText,
        }).ToArray();
        return new EditorSubtitleBurn(cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn);
    }

    private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)
    {
        var path = _path ?? throw new InvalidOperationException("Chưa chọn video.");
        var media = _media ?? throw new InvalidOperationException("Chưa đọc được video.");
        EnsureCurrentSourceFingerprint();
        if (subtitle is not null || _voiceTrack is not null) EnsureCurrentSubtitleFingerprint();
        return new VideoEditRequest(
            path,
            _application.Config.OutputDirectory,
            FileNameBox.Text,
            media.Width,
            media.Height,
            media.Duration,
            _document.Regions.ToArray(),
            subtitle,
            _audioSettings,
            _voiceTrack);
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        await RenderProjectAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null) return;
        _application.CancelJob(_jobId);
        StatusText.Text = "Đang dừng FFmpeg và xóa file render dở...";
    }

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_media is null || EditorBusy || _playback.IsPreviewMode) return;
        var point = e.GetCurrentPoint(Overlay).Position;
        if (!TryNormalize(point, out var normalized)) return;
        if (_inspectorMode == InspectorMode.Subtitle)
        {
            var subtitleHit = HitTestSubtitle(point);
            if (subtitleHit == DragKind.None) return;
            _subtitleDrag = true;
            _subtitleDragOriginal = _subtitlePlacement;
            _dragKind = subtitleHit;
            SelectRegion(-1);
        }
        else if (_inspectorMode == InspectorMode.Blur)
        {
            var hit = HitTestRegion(point);
            if (hit.Index >= 0)
            {
                SelectRegion(hit.Index);
                _dragKind = hit.Kind;
                _dragOriginal = _document.Selected;
            }
            else
            {
                SelectRegion(-1);
                _dragKind = DragKind.Create;
                _dragOriginal = null;
            }
        }
        else return;
        _dragStartNormalized = normalized;
        _dragHistoryCaptured = false;
        Overlay.CapturePointer(e.Pointer);
        RenderDocument(renderInputs: false);
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartNormalized is null || _media is null || !e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        var position = e.GetCurrentPoint(Overlay).Position;
        if (!TryNormalize(position, out var current))
        {
            if (_subtitleDrag || _dragKind is DragKind.None or DragKind.Create || !TryNormalizeClamped(position, out current)) return;
        }
        if (_subtitleDrag && _subtitleDragOriginal is not null)
        {
            _subtitlePlacement = ResizeOrMove(_subtitleDragOriginal, _dragStartNormalized.Value, current, _dragKind);
            RenderOverlays();
            return;
        }
        if (_dragKind == DragKind.Create)
        {
            var settings = RegionWithCurrentSettings(0, 0, 0, 0, string.Empty);
            _draftRegion = EditorRegionGeometry.FromNormalizedDrag(
                settings,
                _dragStartNormalized.Value.X,
                _dragStartNormalized.Value.Y,
                current.X,
                current.Y,
                _media.Width,
                _media.Height);
            RenderOverlays();
            return;
        }
        if (_dragOriginal is null || _document.Selected is not { } selected) return;
        var updated = _dragKind == DragKind.Move
            ? EditorRegionGeometry.MoveBy(
                _dragOriginal,
                current.X - _dragStartNormalized.Value.X,
                current.Y - _dragStartNormalized.Value.Y)
            : EditorRegionGeometry.ResizeBy(
                _dragOriginal,
                current.X - _dragStartNormalized.Value.X,
                current.Y - _dragStartNormalized.Value.Y,
                ResizeHandle(_dragKind),
                _media.Width,
                _media.Height);
        if (updated.X == selected.X && updated.Y == selected.Y
            && updated.Width == selected.Width && updated.Height == selected.Height) return;
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
        if (!commit && _dragHistoryCaptured) _document.CancelChange();
        if (commit && _dragKind == DragKind.Create && _draftRegion is { } created)
        {
            TryCommitCreatedRegion(created);
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

    private bool TryCommitCreatedRegion(EditRegion created)
    {
        try
        {
            ValidateRegion(created);
            _document.Add(created);
            _draftRegion = null;
            LoadSelectedIntoInputs();
            DocumentChanged($"Đã tạo vùng {_document.Regions.Count}.");
            return true;
        }
        catch (Exception error)
        {
            _draftRegion = null;
            RegionValidationText.Text = error.Message;
            StatusText.Text = "Không tạo được vùng: " + error.Message;
            RenderDocument();
            return false;
        }
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
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_inspectorMode != InspectorMode.Blur || EditorBusy) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        var controlDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        var shiftDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if (e.Key == VirtualKey.Z && controlDown && !shiftDown)
        {
            if (TryUndoDocument()) e.Handled = true;
            return;
        }
        var redoShortcut = controlDown &&
            (e.Key == VirtualKey.Y && !shiftDown || e.Key == VirtualKey.Z && shiftDown);
        if (redoShortcut)
        {
            if (TryRedoDocument()) e.Handled = true;
            return;
        }
        if (e.Key is not (VirtualKey.Delete or VirtualKey.Back)) return;
        if (TryDeleteSelectedRegion()) e.Handled = true;
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
        var active = _inspectorMode == InspectorMode.Subtitle && !EditorBusy && !_playback.IsPreviewMode;
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
        var text = cue is null ? "Kéo để đặt vị trí phụ đề" : SubtitlePreviewText(cue);
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

    private (int Index, DragKind Kind) HitTestRegion(Point point)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0) return (-1, DragKind.None);
        if (_document.Selected is { } selected)
        {
            var handle = HitSelectedHandles(point, selected, video);
            if (handle != DragKind.None) return (_document.SelectedIndex, handle);
        }
        var index = EditorRegionGeometry.FindTopmostContaining(
            _document.Regions,
            (point.X - video.X) / video.Width,
            (point.Y - video.Y) / video.Height);
        if (index >= 0) return (index, DragKind.Move);
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

    private static EditorRegionResizeHandle ResizeHandle(DragKind kind) => kind switch
    {
        DragKind.North => EditorRegionResizeHandle.North,
        DragKind.South => EditorRegionResizeHandle.South,
        DragKind.East => EditorRegionResizeHandle.East,
        DragKind.West => EditorRegionResizeHandle.West,
        DragKind.NorthEast => EditorRegionResizeHandle.NorthEast,
        DragKind.NorthWest => EditorRegionResizeHandle.NorthWest,
        DragKind.SouthEast => EditorRegionResizeHandle.SouthEast,
        DragKind.SouthWest => EditorRegionResizeHandle.SouthWest,
        _ => throw new InvalidOperationException("Resize handle không hợp lệ."),
    };

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

    private bool TryNormalizeClamped(Point point, out Point normalized)
    {
        var video = VideoRect();
        if (video.Width <= 0 || video.Height <= 0)
        {
            normalized = default;
            return false;
        }
        normalized = new Point(
            Math.Clamp((point.X - video.X) / video.Width, 0, 1),
            Math.Clamp((point.Y - video.Y) / video.Height, 0, 1));
        return true;
    }

    private EditRegion RegionWithCurrentSettings(double x, double y, double width, double height, string id)
    {
        var duration = _media?.Duration ?? 0;
        var start = StartBox.Value;
        var end = EndBox.Value;
        var region = new EditRegion(x, y, width, height, SelectedEffect(), CurrentEffectStrength(), WholeToggle.IsOn, start, end, id);
        return EditorRegionTimeScope.NormalizeWholeVideo(region, duration);
    }

    private int CurrentEffectStrength()
    {
        var effect = SelectedEffect();
        if (!EffectUsesStrength(effect)) return EditorCoverEffect.StoredStrength;
        if (TryEffectStrength(effect, StrengthBox.Value, out var strength)) return strength;
        return NormalizeEffectStrength(effect, double.NaN, CurrentStoredStrength());
    }

    private EditRegion? ReadRegionFromInputs(string id)
    {
        if (_media is null) return null;
        return EditorRegionGeometry.FromPercentInputs(
            RegionWithCurrentSettings(0, 0, 0, 0, id),
            RegionXBox.Value,
            RegionYBox.Value,
            RegionWidthBox.Value,
            RegionHeightBox.Value,
            _media.Width,
            _media.Height);
    }

    private void ValidateRegion(EditRegion region)
    {
        if (_media is null) throw new InvalidOperationException("Chưa chọn video.");
        var normalized = EditorRegionTimeScope.Normalize(region, _media.Duration);
        _ = VideoEditorService.BuildFilter(new VideoEditRequest(_path ?? "x", ".", "x.mp4", _media.Width, _media.Height, _media.Duration, [normalized]));
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
            if (EffectUsesStrength(region.Effect)) StrengthBox.Value = region.Strength;
            WholeToggle.IsOn = region.WholeVideo;
            StartBox.Value = region.Start;
            EndBox.Value = region.End;
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
        _pendingProjectSave = ProjectSnapshot();
        if (_projectSaveFlushInProgress) return;
        RestartProjectSaveTimer();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer EnsureProjectSaveTimer()
    {
        if (_projectSaveTimer is not null) return _projectSaveTimer;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = false;
        timer.Tick += ProjectSaveTimer_Tick;
        _projectSaveTimer = timer;
        return timer;
    }

    private void RestartProjectSaveTimer()
    {
        var timer = EnsureProjectSaveTimer();
        timer.Stop();
        timer.Start();
    }

    private void StopProjectSaveTimer()
    {
        _projectSaveTimer?.Stop();
    }

    private void CleanupProjectAutosave()
    {
        StopProjectSaveTimer();
        _pendingProjectSave = null;
    }

    private async void ProjectSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_projectSaveFlushInProgress) return;
        var snapshot = _pendingProjectSave;
        _pendingProjectSave = null;
        if (snapshot is null) return;
        try
        {
            await PersistEditorProjectAsync(snapshot);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không tự lưu được project: " + error.Message;
        }
        finally
        {
            if (!_projectSaveFlushInProgress
                && _pendingProjectSave is not null
                && ReferenceEquals(_projectSaveTimer, sender))
                RestartProjectSaveTimer();
        }
    }

    private async Task PersistEditorProjectAsync(EditorProject project)
    {
        await _projectSaveGate.WaitAsync();
        try
        {
            await _application.SaveEditorProjectAsync(project, CancellationToken.None);
        }
        finally
        {
            _projectSaveGate.Release();
        }
    }

    private async Task FlushProjectSaveAsync(EditorProject project)
    {
        _projectSaveFlushInProgress = true;
        StopProjectSaveTimer();
        _pendingProjectSave = null;
        try
        {
            await PersistEditorProjectAsync(project);
        }
        finally
        {
            _projectSaveFlushInProgress = false;
            if (_pendingProjectSave is not null) RestartProjectSaveTimer();
        }
    }

    private async Task SaveProjectNowAsync()
    {
        if (_project is null) return;
        try { await FlushProjectSaveAsync(ProjectSnapshot()); }
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
            _project.Subtitle?.OutputPath ?? string.Empty,
            KaraokeToggle.IsOn),
        Audio = _audioSettings,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private void RefreshEditorActions()
    {
        var idle = !EditorBusy;
        var hasMedia = _media is not null;
        var editable = idle && hasMedia && !_playback.IsPreviewMode;
        OpenVideoButton.IsEnabled = idle && !_playback.IsPreviewMode;
        Overlay.IsHitTestVisible = editable &&
            (_inspectorMode == InspectorMode.Blur || _inspectorMode == InspectorMode.Subtitle && _subtitleSource is not null);
        AddRegionButton.IsEnabled = editable && _draftRegion is not null;
        RemoveRegionButton.IsEnabled = editable && _document.Selected is not null;
        UndoButton.IsEnabled = editable && _document.CanUndo;
        RedoButton.IsEnabled = editable && _document.CanRedo;
        SubtitlePresetButton.IsEnabled = WatermarkPresetButton.IsEnabled = editable;
        var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));
        var audioChanged = _audioSettings.SourceMode != "keep";
        var hasImages = _imageFeatureInitialized && _imageOverlays.Count > 0;
        RenderButton.IsEnabled = editable && !_subtitleManualDirty && _path is not null
            && (_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages)
            && !string.IsNullOrWhiteSpace(FileNameBox.Text);
        CancelButton.IsEnabled = _jobId is not null;
        Timeline.IsEnabled = idle && hasMedia;
        PlayerPlayPauseButton.IsEnabled = idle && hasMedia && _playback.IsReady;
        FullscreenButton.IsEnabled = idle && hasMedia && _playback.IsReady;
        RegionXBox.IsEnabled = RegionYBox.IsEnabled = RegionWidthBox.IsEnabled = RegionHeightBox.IsEnabled = editable;
        EffectBox.IsEnabled = WholeToggle.IsEnabled = editable;
        StrengthBox.IsEnabled = editable && EffectUsesStrength(SelectedEffect());
        StartBox.IsEnabled = EndBox.IsEnabled = editable && !WholeToggle.IsOn;
        EditorUseCurrentStartButton.IsEnabled = EditorUseCurrentEndButton.IsEnabled = editable && !WholeToggle.IsOn;
        FileNameBox.IsEnabled = editable;
        ImportSrtButton.IsEnabled = idle && !_playback.IsPreviewMode;
        CreateAsrButton.IsEnabled = editable;
        PrepareAiButton.IsEnabled = idle && !_playback.IsPreviewMode;
        var aiReady = false;
        try { aiReady = _application.LocalTranslationStatus.RuntimeReady && _application.LocalTranslationStatus.ModelReady; }
        catch { }
        TranslateButton.IsEnabled = editable && _project is not null && _subtitleSource is not null && aiReady;
        CancelTranslationButton.IsEnabled = _translationJobId is not null;
        OpenTranslatedSrtButton.IsEnabled = idle && !_playback.IsPreviewMode && !_subtitleManualDirty && File.Exists(_project?.Subtitle?.OutputPath);
        SaveKaraokeAssButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady && KaraokeToggle.IsOn && _cueSpeechTiming.Count > 0;
        PreviewMuteToggle.IsEnabled = PreviewVolumeSlider.IsEnabled = idle && hasMedia;
        SourceAudioModeBox.IsEnabled = editable;
        SourceAudioGainSlider.IsEnabled = editable && _audioSettings.SourceMode == "duck";
        GenerateTtsButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady && _project?.Speech is { Status: "complete" };
        CancelVoiceButton.IsEnabled = _asrJobId is not null || _ttsJobId is not null;
        CurrentCueVoiceBox.IsEnabled = editable && _subtitleSource is not null && _project?.Speech is { Status: "complete" };
        KaraokeToggle.IsEnabled = idle && !_playback.IsPreviewMode && _subtitleSource is not null;
        RefreshImageControls();
        RefreshEditorParityControls();
        RefreshSubtitleCueEditorControls();
    }

    private static string FormatClock(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}

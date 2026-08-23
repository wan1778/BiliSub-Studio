using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private sealed record ManualCueState(string? SourceOverride, string? VietnameseOverride, bool Locked);

    private readonly Dictionary<string, ManualCueState> _manualCueStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originalCueSource = new(StringComparer.Ordinal);
    private bool _subtitleCueEditorInitialized;
    private bool _subtitleCueEditorSyncing;
    private EditorSubtitleSource? _subtitleCueEditorObservedSource;
    private int _subtitleCueEditorSelectedIndex = -1;
    private ListView? _subtitleCueList;
    private TextBlock? _subtitleCueHeader;
    private TextBox? _subtitleSourceEdit;
    private TextBox? _subtitleVietnameseEdit;
    private ToggleSwitch? _subtitleLockToggle;
    private Button? _subtitleSaveCueButton;
    private Button? _subtitleRetranslateCueButton;
    private Button? _subtitleSaveSrtButton;
    private TextBlock? _subtitleCueEditorStatus;

    private void EnsureSubtitleCueEditorInitialized()
    {
        if (_subtitleCueEditorInitialized) return;
        _subtitleCueEditorInitialized = true;

        TranslateButton.Click -= Translate_Click;
        TranslateButton.Click += TranslateAllWithManualState_Click;

        var card = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
        };
        try
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"];
            card.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RaisedSurfaceBrush"];
        }
        catch { }

        var host = new StackPanel { Spacing = 8 };
        host.Children.Add(new TextBlock
        {
            Text = "Chỉnh từng câu SRT",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        host.Children.Add(new TextBlock
        {
            Text = "Chọn câu để đưa Preview tới đúng timecode. Có thể sửa lời Trung/Việt, khóa câu để Vietsub toàn bộ không ghi đè, hoặc dịch lại riêng câu.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .72,
        });

        _subtitleCueList = new ListView { Height = 170, IsEnabled = false };
        _subtitleCueList.SelectionChanged += SubtitleCueList_SelectionChanged;
        AutomationProperties.SetName(_subtitleCueList, "Danh sách câu phụ đề");
        host.Children.Add(_subtitleCueList);

        _subtitleCueHeader = new TextBlock
        {
            Text = "Chưa chọn câu.",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        host.Children.Add(_subtitleCueHeader);

        _subtitleSourceEdit = new TextBox
        {
            Header = "Lời Trung / nguồn",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 62,
            IsEnabled = false,
        };
        host.Children.Add(_subtitleSourceEdit);

        _subtitleVietnameseEdit = new TextBox
        {
            Header = "Lời Việt",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 62,
            IsEnabled = false,
        };
        host.Children.Add(_subtitleVietnameseEdit);

        _subtitleLockToggle = new ToggleSwitch
        {
            Header = "Khóa câu này — AI Vietsub toàn bộ không được ghi đè",
            IsEnabled = false,
        };
        _subtitleLockToggle.Toggled += SubtitleLock_Toggled;
        host.Children.Add(_subtitleLockToggle);

        var actionGrid = new Grid { ColumnSpacing = 7 };
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition());
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition());
        _subtitleSaveCueButton = new Button
        {
            Content = "Lưu câu",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        _subtitleSaveCueButton.Click += SubtitleSaveCue_Click;
        _subtitleRetranslateCueButton = new Button
        {
            Content = "Dịch lại câu",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        _subtitleRetranslateCueButton.Click += SubtitleRetranslateCue_Click;
        Grid.SetColumn(_subtitleRetranslateCueButton, 1);
        actionGrid.Children.Add(_subtitleSaveCueButton);
        actionGrid.Children.Add(_subtitleRetranslateCueButton);
        host.Children.Add(actionGrid);

        _subtitleSaveSrtButton = new Button
        {
            Content = "Lưu SRT Việt",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
        };
        _subtitleSaveSrtButton.Click += SubtitleSaveSrt_Click;
        host.Children.Add(_subtitleSaveSrtButton);

        _subtitleCueEditorStatus = new TextBlock
        {
            Text = "Chọn SRT tiếng Trung để mở danh sách câu.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .72,
        };
        AutomationProperties.SetLiveSetting(_subtitleCueEditorStatus, AutomationLiveSetting.Polite);
        host.Children.Add(_subtitleCueEditorStatus);

        card.Child = host;
        SubtitleInspectorPanel.Children.Add(card);
        LayoutUpdated += SubtitleCueEditor_LayoutUpdated;
        RefreshSubtitleCueEditorControls();
    }

    private void SubtitleCueEditor_LayoutUpdated(object? sender, object e)
    {
        if (!_subtitleCueEditorInitialized || _subtitleCueEditorSyncing || ReferenceEquals(_subtitleCueEditorObservedSource, _subtitleSource)) return;
        _subtitleCueEditorObservedSource = _subtitleSource;
        _ = SyncSubtitleCueEditorAsync();
    }

    private async Task SyncSubtitleCueEditorAsync()
    {
        if (_subtitleCueEditorSyncing) return;
        _subtitleCueEditorSyncing = true;
        try
        {
            _manualCueStates.Clear();
            _originalCueSource.Clear();
            if (_subtitleSource is null)
            {
                _subtitleCueEditorSelectedIndex = -1;
                RenderSubtitleCueList();
                LoadSelectedSubtitleCueEditor();
                RefreshSubtitleCueEditorControls();
                return;
            }

            try
            {
                var original = await _application.LoadEditorSubtitleAsync(_subtitleSource.Path, CancellationToken.None);
                if (string.Equals(original.Sha256, _subtitleSource.Sha256, StringComparison.Ordinal))
                    foreach (var cue in original.Cues) _originalCueSource[cue.Id] = cue.SourceText;
            }
            catch { }

            await LoadManualCueSidecarAsync(_subtitleSource.Sha256);
            var merged = _subtitleSource.Cues.Select(cue =>
            {
                if (!_manualCueStates.TryGetValue(cue.Id, out var state)) return cue;
                return cue with
                {
                    SourceText = string.IsNullOrWhiteSpace(state.SourceOverride) ? cue.SourceText : state.SourceOverride,
                    VietnameseText = state.VietnameseOverride ?? cue.VietnameseText,
                };
            }).ToArray();
            _subtitleSource = _subtitleSource with { Cues = merged };
            _subtitleCueEditorObservedSource = _subtitleSource;
            if (_subtitleCueEditorSelectedIndex < 0 || _subtitleCueEditorSelectedIndex >= merged.Length)
                _subtitleCueEditorSelectedIndex = merged.Length > 0 ? 0 : -1;
            RenderSubtitleCueList();
            LoadSelectedSubtitleCueEditor();
            RenderOverlays();
            RefreshSubtitleCueEditorControls();
        }
        finally { _subtitleCueEditorSyncing = false; }
    }

    private async void SubtitleCueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subtitleCueEditorSyncing || _subtitleCueList is null || _subtitleSource is null) return;
        var index = _subtitleCueList.SelectedIndex;
        if (index < 0 || index >= _subtitleSource.Cues.Count) return;
        _subtitleCueEditorSelectedIndex = index;
        try
        {
            if (_playerMode) await SetPlaybackModeAsync(enabled: false, play: false);
            var cue = _subtitleSource.Cues[index];
            if (_media is not null)
            {
                _syncingTimeline = true;
                try { Timeline.Value = Math.Clamp(cue.Start, Timeline.Minimum, Timeline.Maximum); }
                finally { _syncingTimeline = false; }
                await UpdateFrameAsync();
                UpdateCompactClock();
            }
            LoadSelectedSubtitleCueEditor();
            RenderOverlays();
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
        catch (Exception error)
        {
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không chuyển được tới câu: " + error.Message;
        }
    }

    private void LoadSelectedSubtitleCueEditor()
    {
        if (_subtitleSourceEdit is null || _subtitleVietnameseEdit is null || _subtitleLockToggle is null || _subtitleCueHeader is null) return;
        _subtitleCueEditorSyncing = true;
        try
        {
            if (_subtitleSource is null || _subtitleCueEditorSelectedIndex < 0 || _subtitleCueEditorSelectedIndex >= _subtitleSource.Cues.Count)
            {
                _subtitleCueHeader.Text = "Chưa chọn câu.";
                _subtitleSourceEdit.Text = string.Empty;
                _subtitleVietnameseEdit.Text = string.Empty;
                _subtitleLockToggle.IsOn = false;
                return;
            }
            var cue = _subtitleSource.Cues[_subtitleCueEditorSelectedIndex];
            _subtitleCueHeader.Text = $"Câu {cue.Number} · {cue.Timing}";
            _subtitleSourceEdit.Text = cue.SourceText;
            _subtitleVietnameseEdit.Text = cue.VietnameseText;
            _subtitleLockToggle.IsOn = _manualCueStates.TryGetValue(cue.Id, out var state) && state.Locked;
        }
        finally { _subtitleCueEditorSyncing = false; }
    }

    private async void SubtitleSaveCue_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveCurrentSubtitleCueAsync(updateLockFromUi: true); }
        catch (Exception error) { if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không lưu được câu: " + error.Message; }
    }

    private async void SubtitleLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_subtitleCueEditorSyncing) return;
        try { await SaveCurrentSubtitleCueAsync(updateLockFromUi: true); }
        catch (Exception error) { if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không khóa/mở khóa được câu: " + error.Message; }
    }

    private async Task SaveCurrentSubtitleCueAsync(bool updateLockFromUi)
    {
        if (_subtitleSource is null || _subtitleSourceEdit is null || _subtitleVietnameseEdit is null || _subtitleLockToggle is null ||
            _subtitleCueEditorSelectedIndex < 0 || _subtitleCueEditorSelectedIndex >= _subtitleSource.Cues.Count) return;
        var sourceText = _subtitleSourceEdit.Text.Trim();
        var vietnameseText = _subtitleVietnameseEdit.Text.Trim();
        if (sourceText.Length == 0) throw new InvalidDataException("Lời nguồn không được để trống.");
        if (sourceText.Length > EditorSubtitleDocument.MaxCueCharacters || vietnameseText.Length > EditorSubtitleDocument.MaxCueCharacters)
            throw new InvalidDataException($"Mỗi câu tối đa {EditorSubtitleDocument.MaxCueCharacters} ký tự.");

        var old = _subtitleSource.Cues[_subtitleCueEditorSelectedIndex];
        var locked = updateLockFromUi && _subtitleLockToggle.IsOn;
        var updated = old with { SourceText = sourceText, VietnameseText = vietnameseText };
        var cues = _subtitleSource.Cues.ToArray();
        cues[_subtitleCueEditorSelectedIndex] = updated;
        _subtitleSource = _subtitleSource with { Cues = cues };
        _subtitleCueEditorObservedSource = _subtitleSource;

        _originalCueSource.TryGetValue(old.Id, out var originalSource);
        var sourceOverride = string.Equals(sourceText, originalSource ?? old.SourceText, StringComparison.Ordinal) ? null : sourceText;
        var previous = _manualCueStates.TryGetValue(old.Id, out var state) ? state : new ManualCueState(null, null, false);
        var vietnameseOverride = string.Equals(vietnameseText, old.VietnameseText, StringComparison.Ordinal) && previous.VietnameseOverride is null
            ? null
            : vietnameseText;
        if (locked && vietnameseOverride is null) vietnameseOverride = vietnameseText;
        if (sourceOverride is null && vietnameseOverride is null && !locked) _manualCueStates.Remove(old.Id);
        else _manualCueStates[old.Id] = new ManualCueState(sourceOverride, vietnameseOverride, locked);

        _voiceTrack = null;
        if (_project is not null)
        {
            _project = _project with { Tts = null };
            AttachSubtitleToProject(_project.Subtitle?.OutputPath ?? string.Empty);
        }
        await SaveManualCueSidecarAsync();
        await SaveProjectNowAsync();
        RenderSubtitleCueList();
        LoadSelectedSubtitleCueEditor();
        UpdateSubtitleSummary();
        RenderOverlays();
        QueuePreviewRefresh();
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
        if (_subtitleCueEditorStatus is not null)
            _subtitleCueEditorStatus.Text = locked ? "Đã lưu và khóa câu; Vietsub toàn bộ sẽ giữ nguyên lời Việt này." : "Đã lưu câu; thay đổi sẽ xuất hiện trên Preview.";
    }

    private async void TranslateAllWithManualState_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null) return;
        try
        {
            await SaveCurrentSubtitleCueAsync(updateLockFromUi: true);
            var locked = _subtitleSource.Cues
                .Where(cue => _manualCueStates.TryGetValue(cue.Id, out var state) && state.Locked)
                .ToDictionary(cue => cue.Id, cue => cue, StringComparer.Ordinal);
            var sourceHash = ShortSourceTextHash(_subtitleSource.Cues);
            var projectId = TranslationProjectId(_project.Id, "all", sourceHash);
            var outputName = Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt";
            _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(
                projectId,
                _subtitleSource,
                _application.Config.OutputDirectory,
                outputName));
            TranslationProgress.Value = 0;
            TranslationStatusText.Text = "Đang Vietsub; câu đã khóa sẽ được khôi phục nguyên văn trước khi lưu SRT Việt.";
            RefreshEditorActions();

            while (_translationJobId is not null)
            {
                var snapshot = _application.Jobs.GetSnapshot(_translationJobId);
                TranslationProgress.Value = snapshot.Progress;
                TranslationStatusText.Text = snapshot.Message;
                if (!snapshot.Done)
                {
                    await Task.Delay(350);
                    continue;
                }
                if (snapshot.Result is not EditorTranslationResult result)
                {
                    var message = snapshot.Error ?? snapshot.Message;
                    _translationJobId = null;
                    TranslationStatusText.Text = message;
                    break;
                }

                var merged = result.Cues.Select(cue => locked.TryGetValue(cue.Id, out var lockedCue)
                    ? cue with { SourceText = lockedCue.SourceText, VietnameseText = lockedCue.VietnameseText }
                    : cue).ToArray();
                _subtitleSource = _subtitleSource with { Cues = merged };
                _subtitleCueEditorObservedSource = _subtitleSource;

                foreach (var cue in merged)
                {
                    if (!_manualCueStates.TryGetValue(cue.Id, out var manual)) continue;
                    if (manual.Locked)
                        _manualCueStates[cue.Id] = manual with { VietnameseOverride = cue.VietnameseText };
                    else
                    {
                        var next = manual with { VietnameseOverride = null };
                        if (next.SourceOverride is null) _manualCueStates.Remove(cue.Id);
                        else _manualCueStates[cue.Id] = next;
                    }
                }
                await RewriteVietnameseSrtAsync(result.OutputPath, merged);
                _voiceTrack = null;
                _project = _project with { Tts = null };
                AttachSubtitleToProject(result.OutputPath);
                await SaveManualCueSidecarAsync();
                await SaveProjectNowAsync();
                _translationJobId = null;
                TranslationProgress.Value = 100;
                TranslationStatusText.Text = $"Vietsub hoàn tất · {merged.Length} câu · câu khóa được giữ nguyên · đã lưu SRT Việt.";
                RenderSubtitleCueList();
                LoadSelectedSubtitleCueEditor();
                UpdateSubtitleSummary();
                RenderOverlays();
                QueuePreviewRefresh();
                RefreshEditorActions();
                RefreshSubtitleCueEditorControls();
                break;
            }
        }
        catch (Exception error)
        {
            _translationJobId = null;
            TranslationStatusText.Text = error.Message;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
    }

    private async void SubtitleRetranslateCue_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null ||
            _subtitleCueEditorSelectedIndex < 0 || _subtitleCueEditorSelectedIndex >= _subtitleSource.Cues.Count) return;
        try
        {
            await SaveCurrentSubtitleCueAsync(updateLockFromUi: true);
            var cue = _subtitleSource.Cues[_subtitleCueEditorSelectedIndex];
            if (_manualCueStates.TryGetValue(cue.Id, out var manual) && manual.Locked)
            {
                if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Hãy mở khóa câu trước khi Dịch lại câu.";
                return;
            }
            var singleSource = _subtitleSource with { Cues = [cue] };
            var projectId = TranslationProjectId(_project.Id, "cue", ShortSourceTextHash([cue]));
            var tempDirectory = Path.Combine(_application.Paths.Temp, "Editor", "CueTranslation");
            Directory.CreateDirectory(tempDirectory);
            var outputName = $"cue-{cue.Number}-{Guid.NewGuid():N}.srt";
            _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(projectId, singleSource, tempDirectory, outputName));
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = $"Đang dịch lại riêng câu {cue.Number} bằng AI local + skill...";
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();

            while (_translationJobId is not null)
            {
                var snapshot = _application.Jobs.GetSnapshot(_translationJobId);
                if (!snapshot.Done)
                {
                    if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = snapshot.Message;
                    await Task.Delay(300);
                    continue;
                }
                if (snapshot.Result is not EditorTranslationResult result || result.Cues.Count != 1)
                {
                    _translationJobId = null;
                    if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = snapshot.Error ?? snapshot.Message;
                    break;
                }
                var translated = result.Cues[0];
                var cues = _subtitleSource.Cues.ToArray();
                cues[_subtitleCueEditorSelectedIndex] = cue with { VietnameseText = translated.VietnameseText };
                _subtitleSource = _subtitleSource with { Cues = cues };
                _subtitleCueEditorObservedSource = _subtitleSource;
                if (_manualCueStates.TryGetValue(cue.Id, out var previous))
                {
                    var next = previous with { VietnameseOverride = null, Locked = false };
                    if (next.SourceOverride is null) _manualCueStates.Remove(cue.Id);
                    else _manualCueStates[cue.Id] = next;
                }
                try { File.Delete(result.OutputPath); } catch { }
                _voiceTrack = null;
                _project = _project with { Tts = null };
                AttachSubtitleToProject(_project.Subtitle?.OutputPath ?? string.Empty);
                await SaveManualCueSidecarAsync();
                await SaveProjectNowAsync();
                _translationJobId = null;
                RenderSubtitleCueList();
                LoadSelectedSubtitleCueEditor();
                UpdateSubtitleSummary();
                RenderOverlays();
                QueuePreviewRefresh();
                RefreshEditorActions();
                RefreshSubtitleCueEditorControls();
                if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = $"Đã dịch lại câu {cue.Number}; Preview đã cập nhật.";
                break;
            }
        }
        catch (Exception error)
        {
            _translationJobId = null;
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không dịch lại được câu: " + error.Message;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
    }

    private async void SubtitleSaveSrt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentSubtitleCueAsync(updateLockFromUi: true);
            if (_subtitleSource is null) return;
            var outputDirectory = _application.Config.OutputDirectory;
            Directory.CreateDirectory(outputDirectory);
            var fileName = FileNamePolicy.Sanitize(Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt", "BiliSub.vi.srt");
            if (!fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) fileName += ".srt";
            var output = FileNamePolicy.UniquePath(Path.Combine(outputDirectory, fileName), _subtitleSource.Path);
            await RewriteVietnameseSrtAsync(output, _subtitleSource.Cues, createNew: true);
            if (_project is not null)
            {
                AttachSubtitleToProject(output);
                await SaveProjectNowAsync();
            }
            OpenTranslatedSrtButton.IsEnabled = true;
            TranslationStatusText.Text = "Đã lưu SRT Việt: " + output;
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Đã xuất SRT Việt; số thứ tự và timecode giữ nguyên.";
        }
        catch (Exception error)
        {
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không lưu được SRT Việt: " + error.Message;
        }
    }

    private void RenderSubtitleCueList()
    {
        if (_subtitleCueList is null) return;
        _subtitleCueEditorSyncing = true;
        try
        {
            _subtitleCueList.Items.Clear();
            if (_subtitleSource is null) return;
            for (var index = 0; index < _subtitleSource.Cues.Count; index++)
            {
                var cue = _subtitleSource.Cues[index];
                var locked = _manualCueStates.TryGetValue(cue.Id, out var manual) && manual.Locked ? " 🔒" : string.Empty;
                var translated = string.IsNullOrWhiteSpace(cue.VietnameseText) ? "chưa dịch" : "VI";
                var text = cue.SourceText.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length > 54) text = text[..51] + "...";
                _subtitleCueList.Items.Add($"{cue.Number}. {FormatClock(cue.Start)} · {translated}{locked} · {text}");
            }
            _subtitleCueList.SelectedIndex = _subtitleCueEditorSelectedIndex;
            if (_subtitleCueEditorSelectedIndex >= 0 && _subtitleCueEditorSelectedIndex < _subtitleCueList.Items.Count)
                _subtitleCueList.ScrollIntoView(_subtitleCueList.Items[_subtitleCueEditorSelectedIndex]);
        }
        finally { _subtitleCueEditorSyncing = false; }
    }

    private void RefreshSubtitleCueEditorControls()
    {
        var hasSource = _subtitleSource is { Cues.Count: > 0 };
        var selected = hasSource && _subtitleCueEditorSelectedIndex >= 0 && _subtitleCueEditorSelectedIndex < _subtitleSource!.Cues.Count;
        var idle = !EditorBusy && !_playerMode;
        if (_subtitleCueList is not null) _subtitleCueList.IsEnabled = hasSource && !_previewRendering;
        if (_subtitleSourceEdit is not null) _subtitleSourceEdit.IsEnabled = selected && idle;
        if (_subtitleVietnameseEdit is not null) _subtitleVietnameseEdit.IsEnabled = selected && idle;
        if (_subtitleLockToggle is not null) _subtitleLockToggle.IsEnabled = selected && idle;
        if (_subtitleSaveCueButton is not null) _subtitleSaveCueButton.IsEnabled = selected && idle;
        var aiReady = false;
        try { aiReady = _application.LocalTranslationStatus.RuntimeReady && _application.LocalTranslationStatus.ModelReady; } catch { }
        var locked = selected && _manualCueStates.TryGetValue(_subtitleSource!.Cues[_subtitleCueEditorSelectedIndex].Id, out var manual) && manual.Locked;
        if (_subtitleRetranslateCueButton is not null) _subtitleRetranslateCueButton.IsEnabled = selected && idle && _project is not null && aiReady && !locked;
        if (_subtitleSaveSrtButton is not null) _subtitleSaveSrtButton.IsEnabled = hasSource && idle && _subtitleSource!.Cues.All(c => !string.IsNullOrWhiteSpace(c.VietnameseText));
    }

    private async Task LoadManualCueSidecarAsync(string sourceSha)
    {
        var path = ManualCueSidecarPath(sourceSha);
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, ManualCueState>>(stream) ?? new();
            var validIds = _subtitleSource?.Cues.Select(c => c.Id).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in loaded)
            {
                if (!validIds.Contains(pair.Key)) continue;
                var state = pair.Value;
                if (state.SourceOverride is { Length: > EditorSubtitleDocument.MaxCueCharacters } ||
                    state.VietnameseOverride is { Length: > EditorSubtitleDocument.MaxCueCharacters }) continue;
                _manualCueStates[pair.Key] = state;
            }
        }
        catch (Exception error)
        {
            if (_subtitleCueEditorStatus is not null) _subtitleCueEditorStatus.Text = "Không đọc được chỉnh sửa SRT đã lưu: " + error.Message;
        }
    }

    private async Task SaveManualCueSidecarAsync()
    {
        if (_subtitleSource is null) return;
        var path = ManualCueSidecarPath(_subtitleSource.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (_manualCueStates.Count == 0)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, _manualCueStates);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private string ManualCueSidecarPath(string sourceSha) => Path.Combine(_application.Paths.Data, "Projects", "SubtitleManual", sourceSha + ".json");

    private static string ShortSourceTextHash(IReadOnlyList<EditorSubtitleCue> cues)
    {
        var text = string.Join('\n', cues.Select(c => c.Id + "\u001f" + c.SourceText));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static string TranslationProjectId(string projectId, string scope, string hash)
    {
        var prefix = new string(projectId.Where(char.IsAsciiLetterOrDigit).Take(28).ToArray());
        if (prefix.Length < 8) prefix = "bilisubed";
        return $"{prefix}-{scope}-{hash}";
    }

    private static async Task RewriteVietnameseSrtAsync(string output, IReadOnlyList<EditorSubtitleCue> cues, bool createNew = false)
    {
        var content = EditorSubtitleDocument.RenderVietnamese(cues);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(temporary, output, overwrite: !createNew);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}

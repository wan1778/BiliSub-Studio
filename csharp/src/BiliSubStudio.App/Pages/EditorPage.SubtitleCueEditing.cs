using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private readonly Dictionary<string, EditorManualCueState> _manualCueStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originalCueSource = new(StringComparer.Ordinal);
    private EditorSubtitleManualStore? _subtitleManualStore;
    private bool _subtitleCueSyncing;
    private int _subtitleCueSelectedIndex = -1;
    private bool _subtitleManualDirty;

    private EditorSubtitleManualStore SubtitleManualStore => _subtitleManualStore ??= new EditorSubtitleManualStore(_application.Paths);

    private async Task SyncSubtitleCueEditorAsync()
    {
        _subtitleCueSyncing = true;
        try
        {
            _manualCueStates.Clear();
            _originalCueSource.Clear();
            _subtitleCueSelectedIndex = -1;
            if (_subtitleSource is null)
            {
                RenderSubtitleCueList();
                LoadSelectedSubtitleCue();
                _subtitleManualDirty = false;
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
            var stored = await SubtitleManualStore.LoadAsync(_subtitleSource.Sha256, CancellationToken.None);
            foreach (var pair in stored) _manualCueStates[pair.Key] = pair.Value;
            _subtitleSource = EditorSubtitleManualStore.Apply(_subtitleSource, _manualCueStates);
            _subtitleCueSelectedIndex = _subtitleSource.Cues.Count > 0 ? 0 : -1;
            _subtitleManualDirty = false;
            RenderSubtitleCueList();
            LoadSelectedSubtitleCue();
            UpdateSubtitleSummary();
            RenderOverlays();
            RefreshSubtitleCueEditorControls();
        }
        finally { _subtitleCueSyncing = false; }
    }

    private async void SubtitleCueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subtitleCueSyncing || _subtitleSource is null) return;
        var index = SubtitleCueList.SelectedIndex;
        if (index < 0 || index >= _subtitleSource.Cues.Count) return;
        if (_subtitleManualDirty)
        {
            SubtitleCueEditorStatus.Text = "Hãy Lưu câu trước khi chuyển sang câu khác.";
            _subtitleCueSyncing = true;
            try { SubtitleCueList.SelectedIndex = _subtitleCueSelectedIndex; }
            finally { _subtitleCueSyncing = false; }
            return;
        }
        _subtitleCueSelectedIndex = index;
        try
        {
            var cue = _subtitleSource.Cues[index];
            await SeekEditorToSubtitleCueAsync(cue.Start);
            LoadSelectedSubtitleCue();
            RenderOverlays();
            UpdateCurrentCueVoiceUi();
            RefreshSubtitleCueEditorControls();
            SubtitleCueEditorStatus.Text = _media is null
                ? $"Đã chọn câu {cue.Number}; timecode sẽ dùng khi mở video."
                : $"Đã đưa Player tới câu {cue.Number} tại {cue.Timing}.";
        }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không chuyển được tới câu: " + error.Message; }
    }

    private async Task SeekEditorToSubtitleCueAsync(double sourceTime)
    {
        // SUB-06: cue navigation targets the compact Player seek position; no large timeline is required.
        if (_media is null) return;
        var target = Math.Clamp(sourceTime, Timeline.Minimum, Timeline.Maximum);
        _syncingTimeline = true;
        try { Timeline.Value = target; }
        finally { _syncingTimeline = false; }
        UpdateClock();
        RenderOverlays();
        UpdateCurrentCueVoiceUi();
        if (_playback.IsPreviewMode) await _playback.SeekAsync(target);
        else await UpdateFrameAsync();
    }

    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_subtitleCueSyncing || _subtitleSource is null || _subtitleCueSelectedIndex < 0) return;
        _subtitleManualDirty = true;
        SubtitleCueEditorStatus.Text = "Câu đang có thay đổi chưa lưu. Preview hiển thị bản nháp; Render/TTS/SRT Việt tạm khóa để tránh dùng bản cũ.";
        RenderOverlays();
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
    }

    private string SubtitlePreviewText(EditorSubtitleCue cue)
    {
        if (_subtitleManualDirty && _subtitleSource is not null &&
            _subtitleCueSelectedIndex >= 0 && _subtitleCueSelectedIndex < _subtitleSource.Cues.Count &&
            string.Equals(_subtitleSource.Cues[_subtitleCueSelectedIndex].Id, cue.Id, StringComparison.Ordinal))
        {
            var vietnamese = SubtitleVietnameseEdit.Text.Trim();
            var source = SubtitleSourceEdit.Text.Trim();
            return string.IsNullOrWhiteSpace(vietnamese) ? source : vietnamese;
        }
        return string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText;
    }

    private async void SubtitleLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_subtitleCueSyncing) return;
        try { await SaveCurrentSubtitleCueAsync(); }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không khóa/mở khóa được câu: " + error.Message; }
    }

    private async void SubtitleSaveCue_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveCurrentSubtitleCueAsync(); }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không lưu được câu: " + error.Message; }
    }

    private async Task SaveCurrentSubtitleCueAsync()
    {
        if (_subtitleSource is null || _subtitleCueSelectedIndex < 0 || _subtitleCueSelectedIndex >= _subtitleSource.Cues.Count) return;
        var sourceText = SubtitleSourceEdit.Text.Trim();
        var vietnamese = SubtitleVietnameseEdit.Text.Trim();
        if (sourceText.Length == 0) throw new InvalidDataException("Lời nguồn không được để trống.");
        if (sourceText.Length > EditorSubtitleDocument.MaxCueCharacters || vietnamese.Length > EditorSubtitleDocument.MaxCueCharacters)
            throw new InvalidDataException($"Mỗi câu tối đa {EditorSubtitleDocument.MaxCueCharacters} ký tự.");
        var old = _subtitleSource.Cues[_subtitleCueSelectedIndex];
        var updated = old with { SourceText = sourceText, VietnameseText = vietnamese };
        var cues = _subtitleSource.Cues.ToArray();
        cues[_subtitleCueSelectedIndex] = updated;
        _subtitleSource = _subtitleSource with { Cues = cues };
        _originalCueSource.TryGetValue(old.Id, out var originalSource);
        var sourceOverride = string.Equals(sourceText, originalSource ?? old.SourceText, StringComparison.Ordinal) ? null : sourceText;
        var state = new EditorManualCueState(sourceOverride, vietnamese, SubtitleLockToggle.IsOn);
        if (state.SourceOverride is null && string.IsNullOrWhiteSpace(state.VietnameseOverride) && !state.Locked) _manualCueStates.Remove(old.Id);
        else _manualCueStates[old.Id] = state;
        _subtitleManualDirty = false;
        MarkTranslatedOutputStale();
        await SubtitleManualStore.SaveAsync(_subtitleSource.Sha256, _manualCueStates, CancellationToken.None);
        await SaveProjectNowAsync();
        RenderSubtitleCueList();
        LoadSelectedSubtitleCue();
        UpdateSubtitleSummary();
        RenderOverlays();
        QueuePreviewRefresh();
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
        SubtitleCueEditorStatus.Text = state.Locked
            ? "Đã lưu và khóa câu. Vietsub toàn bộ sẽ giữ nguyên lời Việt này."
            : "Đã lưu câu. SRT Việt/voice cũ đã được đánh dấu hết hiệu lực.";
    }

    private void MarkTranslatedOutputStale()
    {
        _voiceTrack = null;
        if (_project is null) return;
        _project = _project with
        {
            Tts = null,
            Subtitle = _project.Subtitle is null ? null : _project.Subtitle with
            {
                Cues = _subtitleSource?.Cues ?? _project.Subtitle.Cues,
                OutputPath = string.Empty,
            },
        };
    }

    private async Task TranslateAllWithManualStateAsync()
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null) return;
        await SaveCurrentSubtitleCueAsync();
        var locked = _subtitleSource.Cues
            .Where(c => _manualCueStates.TryGetValue(c.Id, out var state) && state.Locked)
            .ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var outputName = Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt";
        var projectId = TranslationProjectId(_project.Id, "all", SourceTextHash(_subtitleSource.Cues));
        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(
            projectId, _subtitleSource, _application.Config.OutputDirectory, outputName));
        TranslationProgress.Value = 0;
        TranslationStatusText.Text = "Đang Vietsub bằng AI local + skill; câu khóa sẽ không bị ghi đè.";
        RefreshEditorActions();
        try
        {
            while (_translationJobId is not null)
            {
                var snapshot = _application.Jobs.GetSnapshot(_translationJobId);
                TranslationProgress.Value = snapshot.Progress;
                TranslationStatusText.Text = snapshot.Message;
                if (!snapshot.Done) { await Task.Delay(350); continue; }
                if (string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    TranslationStatusText.Text = "Đã hủy Vietsub an toàn. Chỉ checkpoint của các batch hoàn tất được giữ để có thể tiếp tục sau; output cũ vẫn bị khóa.";
                    return;
                }
                if (snapshot.Result is not EditorTranslationResult result)
                    throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
                var merged = result.Cues.Select(c => locked.TryGetValue(c.Id, out var keep)
                    ? c with { SourceText = keep.SourceText, VietnameseText = keep.VietnameseText }
                    : c).ToArray();
                _subtitleSource = _subtitleSource with { Cues = merged };
                foreach (var cue in merged.ToArray())
                {
                    if (!_manualCueStates.TryGetValue(cue.Id, out var state)) continue;
                    if (state.Locked) _manualCueStates[cue.Id] = state with { VietnameseOverride = cue.VietnameseText };
                    else if (state.SourceOverride is null) _manualCueStates.Remove(cue.Id);
                    else _manualCueStates[cue.Id] = state with { VietnameseOverride = null };
                }
                await RewriteVietnameseSrtAsync(result.OutputPath, merged);
                _voiceTrack = null;
                _project = _project with { Tts = null };
                AttachSubtitleToProject(result.OutputPath);
                await SubtitleManualStore.SaveAsync(_subtitleSource.Sha256, _manualCueStates, CancellationToken.None);
                await SaveProjectNowAsync();
                _subtitleManualDirty = false;
                TranslationProgress.Value = 100;
                TranslationStatusText.Text = $"Vietsub hoàn tất · {merged.Length} câu · câu khóa được giữ nguyên.";
                RenderSubtitleCueList();
                LoadSelectedSubtitleCue();
                UpdateSubtitleSummary();
                RenderOverlays();
                QueuePreviewRefresh();
                break;
            }
        }
        finally
        {
            _translationJobId = null;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
    }

    private async void SubtitleRetranslateCue_Click(object sender, RoutedEventArgs e)
    {
        try { await RetranslateSelectedCueAsync(); }
        catch (Exception error)
        {
            _translationJobId = null;
            SubtitleCueEditorStatus.Text = "Không dịch lại được câu: " + error.Message;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
        }
    }

    private async Task RetranslateSelectedCueAsync()
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null || _subtitleCueSelectedIndex < 0) return;
        await SaveCurrentSubtitleCueAsync();
        var cue = _subtitleSource.Cues[_subtitleCueSelectedIndex];
        if (_manualCueStates.TryGetValue(cue.Id, out var state) && state.Locked)
        {
            SubtitleCueEditorStatus.Text = "Hãy mở khóa câu trước khi Dịch lại câu.";
            return;
        }
        var temp = Path.Combine(_application.Paths.Temp, "Editor", "CueTranslation");
        Directory.CreateDirectory(temp);
        var single = _subtitleSource with { Cues = [cue] };
        var projectId = TranslationProjectId(_project.Id, "cue", SourceTextHash([cue]));
        _translationJobId = _application.StartEditorTranslation(new EditorTranslationRequest(
            projectId, single, temp, $"cue-{cue.Number}.srt", ForceFresh: true));
        SubtitleCueEditorStatus.Text = $"Đang dịch mới câu {cue.Number}; checkpoint cũ đã bị loại.";
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
        while (_translationJobId is not null)
        {
            var snapshot = _application.Jobs.GetSnapshot(_translationJobId);
            SubtitleCueEditorStatus.Text = snapshot.Message;
            if (!snapshot.Done) { await Task.Delay(300); continue; }
            if (string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                _translationJobId = null;
                SubtitleCueEditorStatus.Text = $"Đã hủy dịch lại câu {cue.Number}. Không áp dụng kết quả dở; lần Dịch lại tiếp theo vẫn bắt đầu fresh.";
                RefreshEditorActions();
                RefreshSubtitleCueEditorControls();
                return;
            }
            if (snapshot.Result is not EditorTranslationResult result || result.Cues.Count != 1)
                throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
            var translated = result.Cues[0].VietnameseText;
            var cues = _subtitleSource.Cues.ToArray();
            cues[_subtitleCueSelectedIndex] = cue with { VietnameseText = translated };
            _subtitleSource = _subtitleSource with { Cues = cues };
            var previous = _manualCueStates.TryGetValue(cue.Id, out var existing)
                ? existing : new EditorManualCueState(null, null, false);
            _manualCueStates[cue.Id] = previous with { VietnameseOverride = translated, Locked = false };
            try { File.Delete(result.OutputPath); } catch { }
            MarkTranslatedOutputStale();
            await SubtitleManualStore.SaveAsync(_subtitleSource.Sha256, _manualCueStates, CancellationToken.None);
            await SaveProjectNowAsync();
            _translationJobId = null;
            _subtitleManualDirty = false;
            RenderSubtitleCueList();
            LoadSelectedSubtitleCue();
            UpdateSubtitleSummary();
            RenderOverlays();
            QueuePreviewRefresh();
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
            SubtitleCueEditorStatus.Text = $"Đã dịch mới câu {cue.Number}. Hãy lưu SRT Việt hoặc Vietsub toàn bộ trước khi tạo voice/export.";
            break;
        }
    }

    private async void SubtitleSaveSrt_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveVietnameseSrtAsync(); }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không lưu được SRT Việt: " + error.Message; }
    }

    private async Task SaveVietnameseSrtAsync()
    {
        await SaveCurrentSubtitleCueAsync();
        if (_subtitleSource is null || _subtitleSource.Cues.Any(c => string.IsNullOrWhiteSpace(c.VietnameseText)))
            throw new InvalidOperationException("Cần có lời Việt cho tất cả câu trước khi lưu SRT Việt.");
        var directory = _application.Config.OutputDirectory;
        Directory.CreateDirectory(directory);
        var name = FileNamePolicy.Sanitize(Path.GetFileNameWithoutExtension(_subtitleSource.Path) + ".vi.srt", "BiliSub.vi.srt");
        if (!name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) name += ".srt";
        var output = FileNamePolicy.UniquePath(Path.Combine(directory, name), _subtitleSource.Path);
        await RewriteVietnameseSrtAsync(output, _subtitleSource.Cues);
        if (_project is not null)
        {
            AttachSubtitleToProject(output);
            await SaveProjectNowAsync();
        }
        _subtitleManualDirty = false;
        RefreshEditorActions();
        SubtitleCueEditorStatus.Text = "Đã lưu SRT Việt; số thứ tự và timecode giữ nguyên.";
        TranslationStatusText.Text = "Đã lưu SRT Việt: " + output;
    }

    private static async Task RewriteVietnameseSrtAsync(string output, IReadOnlyList<EditorSubtitleCue> cues)
    {
        var absolute = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temp = absolute + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, EditorSubtitleDocument.RenderVietnamese(cues), new UTF8Encoding(false));
            File.Move(temp, absolute, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private void RenderSubtitleCueList()
    {
        _subtitleCueSyncing = true;
        try
        {
            SubtitleCueList.Items.Clear();
            if (_subtitleSource is null) return;
            foreach (var cue in _subtitleSource.Cues)
            {
                var locked = _manualCueStates.TryGetValue(cue.Id, out var state) && state.Locked ? " 🔒" : string.Empty;
                var translated = string.IsNullOrWhiteSpace(cue.VietnameseText) ? "chưa dịch" : "VI";
                SubtitleCueList.Items.Add($"{cue.Number} · {translated}{locked} · {TrimPreview(cue.SourceText)}");
            }
            SubtitleCueList.SelectedIndex = _subtitleCueSelectedIndex;
        }
        finally { _subtitleCueSyncing = false; }
    }

    private void LoadSelectedSubtitleCue()
    {
        _subtitleCueSyncing = true;
        try
        {
            if (_subtitleSource is null || _subtitleCueSelectedIndex < 0 || _subtitleCueSelectedIndex >= _subtitleSource.Cues.Count)
            {
                SubtitleCueHeader.Text = "Chưa chọn câu.";
                SubtitleSourceEdit.Text = SubtitleVietnameseEdit.Text = string.Empty;
                SubtitleLockToggle.IsOn = false;
                return;
            }
            var cue = _subtitleSource.Cues[_subtitleCueSelectedIndex];
            SubtitleCueHeader.Text = $"Câu {cue.Number} · {cue.Timing}";
            SubtitleSourceEdit.Text = cue.SourceText;
            SubtitleVietnameseEdit.Text = cue.VietnameseText;
            SubtitleLockToggle.IsOn = _manualCueStates.TryGetValue(cue.Id, out var state) && state.Locked;
        }
        finally { _subtitleCueSyncing = false; }
    }

    private void RefreshSubtitleCueEditorControls()
    {
        var hasSource = _subtitleSource is not null && _subtitleSource.Cues.Count > 0;
        var selected = hasSource && _subtitleCueSelectedIndex >= 0 && _subtitleCueSelectedIndex < _subtitleSource!.Cues.Count;
        var idle = !EditorBusy && !_playback.IsPreviewMode;
        SubtitleCueList.IsEnabled = hasSource && idle && !_subtitleManualDirty;
        SubtitleSourceEdit.IsEnabled = selected && idle;
        SubtitleVietnameseEdit.IsEnabled = selected && idle;
        SubtitleLockToggle.IsEnabled = selected && idle;
        SubtitleSaveCueButton.IsEnabled = selected && idle;
        var locked = selected && _manualCueStates.TryGetValue(_subtitleSource!.Cues[_subtitleCueSelectedIndex].Id, out var state) && state.Locked;
        SubtitleRetranslateCueButton.IsEnabled = selected && idle && !locked;
        SubtitleSaveSrtButton.IsEnabled = hasSource && idle && !_subtitleManualDirty && _subtitleSource!.Cues.All(c => !string.IsNullOrWhiteSpace(c.VietnameseText));
    }

    private static string TranslationProjectId(string projectId, string scope, string contentHash)
    {
        var safeScope = scope.All(char.IsLetterOrDigit) ? scope : "scope";
        return projectId + "-" + safeScope + "-" + contentHash;
    }

    private static string SourceTextHash(IReadOnlyList<EditorSubtitleCue> cues)
    {
        var text = string.Join('\n', cues.Select(c => c.Id + "\u001f" + c.SourceText));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static string TrimPreview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 42 ? flat : flat[..39] + "...";
    }
}

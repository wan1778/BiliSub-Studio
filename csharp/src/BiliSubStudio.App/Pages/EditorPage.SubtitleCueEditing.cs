using System.Text;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private readonly Dictionary<string, EditorManualCueState> _manualCueStates = new(StringComparer.Ordinal);
    private EditorSubtitleManualStore? _subtitleManualStore;
    private bool _subtitleCueSyncing;
    private int _subtitleCueSelectedIndex = -1;
    private bool _subtitleManualDirty;

    private EditorSubtitleManualStore SubtitleManualStore => _subtitleManualStore ??= new EditorSubtitleManualStore(_application.Paths);

    private async Task SyncSubtitleCueEditorAsync()
    {
        var wasSyncing = _subtitleCueSyncing;
        _subtitleCueSyncing = true;
        try
        {
            _manualCueStates.Clear();
            _subtitleCueSelectedIndex = -1;
            if (_subtitleSource is null)
            {
                RenderSubtitleCueList();
                LoadSelectedSubtitleCue();
                _subtitleManualDirty = false;
                RefreshSubtitleCueEditorControls();
                return;
            }
            var stored = await SubtitleManualStore.LoadAsync(_subtitleSource.Sha256, CancellationToken.None);
            var restoreStoredVietnamese = _subtitleSource.Cues.Any(c => !string.IsNullOrWhiteSpace(c.VietnameseText))
                || !string.IsNullOrWhiteSpace(_project?.Subtitle?.OutputPath);
            foreach (var pair in stored)
            {
                if (restoreStoredVietnamese)
                {
                    _manualCueStates[pair.Key] = pair.Value;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(pair.Value.SourceOverride)) continue;
                _manualCueStates[pair.Key] = pair.Value with { VietnameseOverride = null, Locked = false };
            }
            _subtitleSource = EditorSubtitleManualStore.Apply(_subtitleSource, _manualCueStates);
            _subtitleCueSelectedIndex = _subtitleSource.Cues.Count > 0 ? 0 : -1;
            _subtitleManualDirty = false;
            RenderSubtitleCueList();
            LoadSelectedSubtitleCue();
            UpdateSubtitleSummary();
            RenderOverlays();
            RefreshSubtitleCueEditorControls();
        }
        finally { _subtitleCueSyncing = wasSyncing; }
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
        if (_playback.IsPreviewMode) await _playback.SeekAsync(target);
        else await UpdateFrameAsync();
    }

    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_subtitleCueSyncing || _subtitleSource is null || _subtitleCueSelectedIndex < 0) return;
        var cue = _subtitleSource.Cues[_subtitleCueSelectedIndex];
        _subtitleManualDirty = EditorVietnameseSubtitleWorkflow.HasDraftChange(cue, SubtitleVietnameseEdit.Text);
        SubtitleCueEditorStatus.Text = _subtitleManualDirty
            ? "Câu đang có thay đổi chưa lưu. Preview hiển thị bản nháp; bấm Lưu câu trước khi tạo voice/xuất."
            : "Nội dung khớp câu đã lưu.";
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
            return vietnamese;
        }
        return string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText;
    }

    private async void SubtitleSaveCue_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveCurrentSubtitleCueAsync(); }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không lưu được câu: " + error.Message; }
    }

    private async Task SaveCurrentSubtitleCueAsync()
    {
        if (_subtitleSource is null || _subtitleCueSelectedIndex < 0 || _subtitleCueSelectedIndex >= _subtitleSource.Cues.Count) return;
        EnsureCurrentSubtitleFingerprint();
        var vietnamese = SubtitleVietnameseEdit.Text.Trim();
        if (vietnamese.Length == 0) throw new InvalidDataException("Lời Việt không được để trống.");
        if (vietnamese.Length > EditorSubtitleDocument.MaxCueCharacters)
            throw new InvalidDataException($"Mỗi câu tối đa {EditorSubtitleDocument.MaxCueCharacters} ký tự.");
        var old = _subtitleSource.Cues[_subtitleCueSelectedIndex];
        if (!EditorVietnameseSubtitleWorkflow.HasDraftChange(old, vietnamese))
        {
            _subtitleManualDirty = false;
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
            return;
        }
        var updated = old with { VietnameseText = vietnamese };
        var cues = _subtitleSource.Cues.ToArray();
        cues[_subtitleCueSelectedIndex] = updated;
        _subtitleSource = _subtitleSource with { Cues = cues };
        var previous = _manualCueStates.TryGetValue(old.Id, out var existing)
            ? existing : new EditorManualCueState(null, null, false);
        _manualCueStates[old.Id] = previous with { VietnameseOverride = vietnamese };
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
        SubtitleCueEditorStatus.Text = "Đã lưu lời Việt. Voice cũ đã hết hiệu lực; có thể tạo voice mới.";
    }

    private void MarkTranslatedOutputStale()
    {
        ClearVoiceTrackState();
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
            AttachSubtitleToProject(output, _project.Subtitle?.TranslationPolicyKey);
            await SaveProjectNowAsync();
        }
        _subtitleManualDirty = false;
        RefreshEditorActions();
        SubtitleCueEditorStatus.Text = "Đã lưu SRT Việt; số thứ tự và timecode giữ nguyên.";
        SubtitleStatusText.Text = "Đã lưu SRT Việt: " + output;
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
        var wasSyncing = _subtitleCueSyncing;
        _subtitleCueSyncing = true;
        try
        {
            SubtitleCueList.Items.Clear();
            if (_subtitleSource is null) return;
            for (var index = 0; index < _subtitleSource.Cues.Count; index++)
            {
                var cue = _subtitleSource.Cues[index];
                var hasVietnamese = !string.IsNullOrWhiteSpace(cue.VietnameseText);
                var status = hasVietnamese ? "lời Việt" : "thiếu lời Việt";
                var preview = hasVietnamese ? cue.VietnameseText : cue.SourceText;
                SubtitleCueList.Items.Add($"{cue.Number} · {status} · {TrimPreview(preview)}");
            }
            SubtitleCueList.SelectedIndex = _subtitleCueSelectedIndex;
        }
        finally { _subtitleCueSyncing = wasSyncing; }
    }

    private void LoadSelectedSubtitleCue()
    {
        var wasSyncing = _subtitleCueSyncing;
        _subtitleCueSyncing = true;
        try
        {
            if (_subtitleSource is null || _subtitleCueSelectedIndex < 0 || _subtitleCueSelectedIndex >= _subtitleSource.Cues.Count)
            {
                SubtitleCueHeader.Text = "Chưa chọn câu.";
                SubtitleVietnameseEdit.Text = string.Empty;
                return;
            }
            var cue = _subtitleSource.Cues[_subtitleCueSelectedIndex];
            SubtitleCueHeader.Text = $"Câu {cue.Number} · {cue.Timing}";
            SubtitleVietnameseEdit.Text = cue.VietnameseText;
        }
        finally { _subtitleCueSyncing = wasSyncing; }
    }

    private void RefreshSubtitleCueEditorControls()
    {
        var hasSource = _subtitleSource is not null && _subtitleSource.Cues.Count > 0;
        var selected = hasSource && _subtitleCueSelectedIndex >= 0 && _subtitleCueSelectedIndex < _subtitleSource!.Cues.Count;
        var idle = !EditorBusy && !_playback.IsPreviewMode;
        SubtitleCueList.IsEnabled = hasSource && !_subtitleManualDirty && idle;
        SubtitleVietnameseEdit.IsEnabled = selected && idle;
        SubtitleSaveCueButton.IsEnabled = selected && idle && _subtitleManualDirty;
        SubtitleSaveSrtButton.IsEnabled = hasSource && idle && !_subtitleManualDirty
            && _subtitleSource!.Cues.All(c => !string.IsNullOrWhiteSpace(c.VietnameseText));
    }

    private static string TrimPreview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 42 ? flat : flat[..39] + "...";
    }
}

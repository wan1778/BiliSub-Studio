#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / 'csharp/src/BiliSubStudio.App/Pages'
CORE = ROOT / 'csharp/src/BiliSubStudio.Core/Editor'
TESTS = ROOT / 'csharp/tests/BiliSubStudio.Core.ContractTests'
VALIDATOR = ROOT / 'csharp/scripts/validate_csharp_migration.py'


def read(p): return p.read_text(encoding='utf-8')
def write(p, s): p.write_text(s, encoding='utf-8', newline='\n')
def rep(s, old, new, label):
    if old not in s: raise RuntimeError('missing marker: ' + label)
    return s.replace(old, new, 1)

def between(s, start, end, replacement, label):
    a=s.find(start)
    if a<0: raise RuntimeError('missing start: '+label)
    b=s.find(end,a+len(start))
    if b<0: raise RuntimeError('missing end: '+label)
    return s[:a]+replacement.rstrip()+'\n\n'+s[b:]

# 1) Core manual cue persistence.
manual_store = r'''using System.Text.Json;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorManualCueState(string? SourceOverride, string? VietnameseOverride, bool Locked);

public sealed class EditorSubtitleManualStore
{
    private const int Schema = 1;
    private const long MaxBytes = 8L * 1024 * 1024;
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed record ManualDocument(int Schema, string SourceSha256, Dictionary<string, EditorManualCueState> Cues);

    public EditorSubtitleManualStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Data, "Projects", "SubtitleManual");
    }

    public async Task<IReadOnlyDictionary<string, EditorManualCueState>> LoadAsync(string sourceSha256, CancellationToken cancellationToken)
    {
        ValidateSha(sourceSha256);
        var path = StatePath(sourceSha256);
        if (!File.Exists(path)) return new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxBytes) throw new InvalidDataException("Manual subtitle state có kích thước không hợp lệ.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ManualDocument>(stream, _json, cancellationToken)
                ?? throw new InvalidDataException("Manual subtitle state rỗng.");
            if (document.Schema != Schema || !string.Equals(document.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manual subtitle state không khớp SRT nguồn.");
            var result = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
            foreach (var pair in document.Cues ?? new Dictionary<string, EditorManualCueState>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
                ValidateText(pair.Value.SourceOverride);
                ValidateText(pair.Value.VietnameseOverride);
                if (pair.Value.SourceOverride is null && pair.Value.VietnameseOverride is null && !pair.Value.Locked) continue;
                result[pair.Key] = pair.Value;
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Quarantine(path);
            return new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        }
    }

    public async Task SaveAsync(string sourceSha256, IReadOnlyDictionary<string, EditorManualCueState> states, CancellationToken cancellationToken)
    {
        ValidateSha(sourceSha256);
        ArgumentNullException.ThrowIfNull(states);
        Directory.CreateDirectory(_directory);
        var path = StatePath(sourceSha256);
        var normalized = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal);
        foreach (var pair in states)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            ValidateText(pair.Value.SourceOverride);
            ValidateText(pair.Value.VietnameseOverride);
            if (pair.Value.SourceOverride is null && pair.Value.VietnameseOverride is null && !pair.Value.Locked) continue;
            normalized[pair.Key] = pair.Value;
        }
        if (normalized.Count == 0)
        {
            TryDelete(path);
            return;
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, new ManualDocument(Schema, sourceSha256.ToLowerInvariant(), normalized), _json, cancellationToken);
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    public static EditorSubtitleSource Apply(EditorSubtitleSource source, IReadOnlyDictionary<string, EditorManualCueState> states)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(states);
        var cues = source.Cues.Select(cue =>
        {
            if (!states.TryGetValue(cue.Id, out var state)) return cue;
            var sourceText = string.IsNullOrWhiteSpace(state.SourceOverride) ? cue.SourceText : state.SourceOverride!.Trim();
            var vietnamese = state.VietnameseOverride is null ? cue.VietnameseText : state.VietnameseOverride.Trim();
            ValidateText(sourceText);
            ValidateText(vietnamese);
            return cue with { SourceText = sourceText, VietnameseText = vietnamese };
        }).ToArray();
        EditorSubtitleDocument.ValidateUnchangedTimeline(source.Cues, cues);
        return source with { Cues = cues };
    }

    private string StatePath(string sha) => Path.Combine(_directory, sha.ToLowerInvariant() + ".json");

    private static void ValidateSha(string sha)
    {
        if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("SHA-256 SRT không hợp lệ.", nameof(sha));
    }

    private static void ValidateText(string? value)
    {
        if (value is not null && value.Length > EditorSubtitleDocument.MaxCueCharacters)
            throw new InvalidDataException($"Manual subtitle vượt {EditorSubtitleDocument.MaxCueCharacters} ký tự mỗi cue.");
    }

    private static void Quarantine(string path)
    {
        try { File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true); } catch { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
'''
write(CORE/'EditorSubtitleManualStore.cs', manual_store)

# 2) Force-fresh translation request contract.
trans_path = CORE/'LocalSubtitleTranslationService.cs'
trans = read(trans_path)
trans = rep(trans,
'''public sealed record EditorTranslationRequest(\n    string ProjectId,\n    EditorSubtitleSource Source,\n    string OutputDirectory,\n    string OutputFileName);''',
'''public sealed record EditorTranslationRequest(\n    string ProjectId,\n    EditorSubtitleSource Source,\n    string OutputDirectory,\n    string OutputFileName,\n    bool ForceFresh = false);''', 'translation request force fresh')
trans = rep(trans,
'''        var checkpointPath = Path.Combine(CheckpointDirectory, request.ProjectId + ".json");\n        var checkpoint = await LoadCheckpointAsync(checkpointPath, request.Source.Sha256, job.CancellationToken);''',
'''        var checkpointPath = Path.Combine(CheckpointDirectory, request.ProjectId + ".json");\n        if (request.ForceFresh) TryDelete(checkpointPath);\n        var checkpoint = await LoadCheckpointAsync(checkpointPath, request.Source.Sha256, job.CancellationToken);''', 'force fresh checkpoint reset')
write(trans_path, trans)

# 3) Static XAML cue editor.
xaml_path=APP/'EditorPage.xaml'
x=read(xaml_path)
marker='''                                <TextBlock x:Name="TranslationStatusText" Style="{StaticResource MutedTextStyle}" Text="Chưa chuẩn bị AI local." TextWrapping="Wrap" AutomationProperties.LiveSetting="Polite" />'''
insert=marker+r'''
                                <Rectangle Height="1" Fill="{ThemeResource BorderBrush}" Margin="0,2" />
                                <TextBlock FontSize="14" FontWeight="SemiBold" Text="Chỉnh từng câu SRT" />
                                <TextBlock Style="{StaticResource MutedTextStyle}" Text="Chọn câu để đưa Player tới đúng timecode. Có thể sửa lời Trung/Việt, khóa câu hoặc dịch lại riêng câu bằng AI local." TextWrapping="Wrap" />
                                <ListView x:Name="SubtitleCueList" MinHeight="150" MaxHeight="230" IsEnabled="False" SelectionChanged="SubtitleCueList_SelectionChanged" AutomationProperties.Name="Danh sách câu phụ đề" />
                                <TextBlock x:Name="SubtitleCueHeader" FontWeight="SemiBold" Text="Chưa chọn câu." TextWrapping="Wrap" />
                                <TextBox x:Name="SubtitleSourceEdit" Header="Lời Trung / nguồn" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="62" IsEnabled="False" TextChanged="SubtitleManualText_TextChanged" />
                                <TextBox x:Name="SubtitleVietnameseEdit" Header="Lời Việt" AcceptsReturn="True" TextWrapping="Wrap" MinHeight="62" IsEnabled="False" TextChanged="SubtitleManualText_TextChanged" />
                                <ToggleSwitch x:Name="SubtitleLockToggle" Header="Khóa câu — Vietsub toàn bộ không ghi đè" IsEnabled="False" Toggled="SubtitleLock_Toggled" />
                                <Grid ColumnSpacing="6"><Grid.ColumnDefinitions><ColumnDefinition /><ColumnDefinition /></Grid.ColumnDefinitions>
                                    <Button x:Name="SubtitleSaveCueButton" Click="SubtitleSaveCue_Click" Content="Lưu câu" IsEnabled="False" Style="{StaticResource SecondaryButtonStyle}" />
                                    <Button Grid.Column="1" x:Name="SubtitleRetranslateCueButton" Click="SubtitleRetranslateCue_Click" Content="Dịch lại câu" IsEnabled="False" />
                                </Grid>
                                <Button x:Name="SubtitleSaveSrtButton" Click="SubtitleSaveSrt_Click" Content="Lưu SRT Việt" IsEnabled="False" Style="{StaticResource SecondaryButtonStyle}" />
                                <TextBlock x:Name="SubtitleCueEditorStatus" Style="{StaticResource MutedTextStyle}" Text="Chọn SRT để mở danh sách câu." TextWrapping="Wrap" AutomationProperties.LiveSetting="Polite" />'''
x=rep(x, marker, insert, 'cue editor XAML')
write(xaml_path,x)

# 4) Clean cue editor partial.
cue_partial = r'''using System.Security.Cryptography;
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
            if (_playerMode) await SetPlaybackModeAsync(false, false);
            var cue = _subtitleSource.Cues[index];
            if (_media is not null)
            {
                _syncingTimeline = true;
                try { Timeline.Value = Math.Clamp(cue.Start, Timeline.Minimum, Timeline.Maximum); }
                finally { _syncingTimeline = false; }
                await UpdateFrameAsync();
            }
            LoadSelectedSubtitleCue();
            RenderOverlays();
            UpdateCurrentCueVoiceUi();
            RefreshSubtitleCueEditorControls();
        }
        catch (Exception error) { SubtitleCueEditorStatus.Text = "Không chuyển được tới câu: " + error.Message; }
    }

    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_subtitleCueSyncing || _subtitleSource is null || _subtitleCueSelectedIndex < 0) return;
        _subtitleManualDirty = true;
        SubtitleCueEditorStatus.Text = "Câu đang có thay đổi chưa lưu. Render/TTS/SRT Việt tạm khóa để tránh dùng bản cũ.";
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
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
        var idle = !EditorBusy && !_playerMode;
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
'''
write(APP/'EditorPage.SubtitleCueEditing.cs', cue_partial)

# 5) Wire explicit sync points and clean Translate handler; gate stale outputs while editing.
editor_path=APP/'EditorPage.xaml.cs'
e=read(editor_path)
e=rep(e,
'''        else await RestoreSubtitleAsync(_project.Subtitle);\n        await RestoreSpeechAndVoiceAsync();''',
'''        else await RestoreSubtitleAsync(_project.Subtitle);\n        await SyncSubtitleCueEditorAsync();\n        await RestoreSpeechAndVoiceAsync();''', 'video load cue sync')
e=rep(e,
'''        SrtPathText.Text = source.Path;\n        AsrStatusText.Text = _project?.Speech is { Status: "complete" }''',
'''        await SyncSubtitleCueEditorAsync();\n        SrtPathText.Text = source.Path;\n        AsrStatusText.Text = _project?.Speech is { Status: "complete" }''', 'import cue sync')
e=rep(e,
'''                        TranslationStatusText.Text = "Video chưa có SRT nên đã giữ thêm SRT Trung do Whisper tạo; mục chính vẫn là word timing/nhịp thoại.";\n                    }\n                    _voiceTrack = null;''',
'''                        TranslationStatusText.Text = "Video chưa có SRT nên đã giữ thêm SRT Trung do Whisper tạo; mục chính vẫn là word timing/nhịp thoại.";\n                        await SyncSubtitleCueEditorAsync();\n                    }\n                    _voiceTrack = null;''', 'ASR cue sync')
e=between(e,
'''    private async void Translate_Click(object sender, RoutedEventArgs e)\n''',
'''    private void CancelTranslation_Click(object sender, RoutedEventArgs e)''',
'''    private async void Translate_Click(object sender, RoutedEventArgs e)\n    {\n        try { await TranslateAllWithManualStateAsync(); }\n        catch (Exception error)\n        {\n            _translationJobId = null;\n            TranslationStatusText.Text = error.Message;\n            RefreshEditorActions();\n            RefreshSubtitleCueEditorControls();\n        }\n    }''', 'clean Translate handler')
e=rep(e,
'''        RenderButton.IsEnabled = editable && _path is not null\n            && (_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages)\n            && !string.IsNullOrWhiteSpace(FileNameBox.Text);''',
'''        RenderButton.IsEnabled = editable && !_subtitleManualDirty && _path is not null\n            && (_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages)\n            && !string.IsNullOrWhiteSpace(FileNameBox.Text);''', 'render stale guard')
e=rep(e,
'''        OpenTranslatedSrtButton.IsEnabled = idle && !_playerMode && File.Exists(_project?.Subtitle?.OutputPath);''',
'''        OpenTranslatedSrtButton.IsEnabled = idle && !_playerMode && !_subtitleManualDirty && File.Exists(_project?.Subtitle?.OutputPath);''', 'open stale SRT guard')
e=rep(e,
'''        SaveKaraokeAssButton.IsEnabled = editable && subtitleReady && KaraokeToggle.IsOn && _cueSpeechTiming.Count > 0;''',
'''        SaveKaraokeAssButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady && KaraokeToggle.IsOn && _cueSpeechTiming.Count > 0;''', 'karaoke stale guard')
e=rep(e,
'''        GenerateTtsButton.IsEnabled = editable && subtitleReady && _project?.Speech is { Status: "complete" };''',
'''        GenerateTtsButton.IsEnabled = editable && !_subtitleManualDirty && subtitleReady && _project?.Speech is { Status: "complete" };''', 'tts stale guard')
e=rep(e,
'''        RefreshImageControls();\n        RefreshEditorParityControls();''',
'''        RefreshImageControls();\n        RefreshEditorParityControls();\n        RefreshSubtitleCueEditorControls();''', 'cue state refresh')
write(editor_path,e)

# 6) Core contract.
contract = r'''using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorSubtitleManualContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-manual-sub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var store = new EditorSubtitleManualStore(paths);
            var sha = new string('a', 64);
            var source = new EditorSubtitleSource(
                Path.Combine(root, "source.srt"), 123, 456, sha,
                [
                    new EditorSubtitleCue("c1", "1", "00:00:01,000 --> 00:00:02,000", 1, 2, "师尊", "Sư tôn"),
                    new EditorSubtitleCue("c2", "2", "00:00:03,000 --> 00:00:04,000", 3, 4, "弟子", "Đệ tử"),
                ]);
            var state = new Dictionary<string, EditorManualCueState>(StringComparer.Ordinal)
            {
                ["c1"] = new("师尊大人", "Sư tôn đại nhân", true),
            };
            await store.SaveAsync(sha, state, CancellationToken.None);
            var loaded = await store.LoadAsync(sha, CancellationToken.None);
            if (!loaded.TryGetValue("c1", out var cue) || !cue.Locked || cue.SourceOverride != "师尊大人" || cue.VietnameseOverride != "Sư tôn đại nhân")
                throw new InvalidOperationException("manual cue state did not round-trip");
            var applied = EditorSubtitleManualStore.Apply(source, loaded);
            if (applied.Cues[0].SourceText != "师尊大人" || applied.Cues[0].VietnameseText != "Sư tôn đại nhân")
                throw new InvalidOperationException("manual cue overrides were not applied");
            if (applied.Cues[0].Timing != source.Cues[0].Timing || applied.Cues[1] != source.Cues[1])
                throw new InvalidOperationException("manual cue state changed untouched timeline/cues");
            await store.SaveAsync(sha, new Dictionary<string, EditorManualCueState>(), CancellationToken.None);
            if ((await store.LoadAsync(sha, CancellationToken.None)).Count != 0)
                throw new InvalidOperationException("empty manual cue state was not removed");
            var fresh = new EditorTranslationRequest("p", source, root, "out.srt", ForceFresh: true);
            if (!fresh.ForceFresh) throw new InvalidOperationException("force-fresh translation request contract missing");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
'''
write(TESTS/'EditorSubtitleManualContract.cs',contract)
prog_path=TESTS/'Program.cs'
p=read(prog_path)
p=rep(p,
'''        ("editor SRT keeps exact blocks order and timecodes", EditorSubtitleDocumentContractAsync),''',
'''        ("editor SRT keeps exact blocks order and timecodes", EditorSubtitleDocumentContractAsync),\n        ("editor manual cue state persists locks and preserves timeline", EditorSubtitleManualContract.RunAsync),''', 'manual cue contract registration')
write(prog_path,p)

# 7) Static regression contract: force-fresh + no LayoutUpdated cue sync + static XAML controls.
v=read(VALIDATOR)
anchor='''require("EnsureEditorParityInitialized();" in editor_partials and "EnsureImageFeatureInitialized();" in editor_partials,\n        "Editor must initialize parity and image tools from one lifecycle owner")'''
addition=anchor+'''\nrequire("SubtitleCueList" in editor and "SubtitleRetranslateCueButton" in editor and "SubtitleSaveSrtButton" in editor,\n        "Editor static subtitle cue editor controls missing")\nrequire("ForceFresh = false" in read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs")\n        and "if (request.ForceFresh) TryDelete(checkpointPath);" in read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"),\n        "Editor force-fresh cue translation checkpoint reset missing")\nrequire("LayoutUpdated += Subtitle" not in editor_partials and "SubtitleRetranslateCue_Click(sender" not in editor_partials,\n        "Editor cue editor must not sync from LayoutUpdated or call one event handler from another")'''
v=rep(v,anchor,addition,'validator cue contracts')
write(VALIDATOR,v)

print('Applied pre-SOURCE completion: clean cue editor, persistence, stale-output guards, force-fresh retranslation and tests')

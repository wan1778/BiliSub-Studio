#!/usr/bin/env python3
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml"
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
CUES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs"
VALIDATE = ROOT / "csharp/scripts/validate_csharp_migration.py"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one occurrence, found {count}")
    return text.replace(old, new, 1)


# SUB-01: one XAML handler -> one awaitable business method.
xaml = XAML.read_text(encoding="utf-8")
xaml = replace_once(xaml, 'Click="ImportSrt_Click"', 'Click="ImportSubtitle_Click"', "SUB-01 XAML handler")
XAML.write_text(xaml, encoding="utf-8")

page = PAGE.read_text(encoding="utf-8")
pattern = re.compile(
    r"    private async void ImportSrt_Click\(object sender, RoutedEventArgs e\)\n"
    r"    \{.*?\n"
    r"    \}\n\n"
    r"    private async Task ImportSrtAsync\(\)\n"
    r"    \{.*?\n"
    r"    \}\n\n"
    r"    private void AttachSubtitleToProject",
    re.DOTALL,
)
replacement = '''    private async void ImportSubtitle_Click(object sender, RoutedEventArgs e)
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

        _subtitleSource = candidate;
        _subtitlePlacement = EditorSubtitlePlacement.Default;
        if (_project is not null)
        {
            // SUB-03: attach the validated SRT to the already-open project and invalidate stale voice output.
            _voiceTrack = null;
            _project = _project with { Tts = null };
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

    private void AttachSubtitleToProject'''
page, count = pattern.subn(replacement, page, count=1)
if count != 1:
    raise SystemExit(f"SUB-01..05 import block: expected one replacement, found {count}")
PAGE.write_text(page, encoding="utf-8")

# SUB-06: cue list owns a single seek method and keeps processed Player mode when active.
cues = CUES.read_text(encoding="utf-8")
cue_pattern = re.compile(
    r"    private async void SubtitleCueList_SelectionChanged\(object sender, SelectionChangedEventArgs e\)\n"
    r"    \{.*?\n"
    r"    \}\n\n"
    r"    private void SubtitleManualText_TextChanged",
    re.DOTALL,
)
cue_replacement = '''    private async void SubtitleCueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        if (_playerMode) await SeekProcessedPreviewAsync(target);
        else await UpdateFrameAsync();
    }

    private void SubtitleManualText_TextChanged'''
cues, count = cue_pattern.subn(cue_replacement, cues, count=1)
if count != 1:
    raise SystemExit(f"SUB-06 cue handler: expected one replacement, found {count}")
CUES.write_text(cues, encoding="utf-8")

# Strengthen static regression gates for SUB-01..06.
validate = VALIDATE.read_text(encoding="utf-8")
anchor = '''require("LayoutUpdated += Subtitle" not in editor_partials and "SubtitleRetranslateCue_Click(sender" not in editor_partials,
        "Editor cue editor must not sync from LayoutUpdated or call one event handler from another")
'''
insert = anchor + '''require('Click="ImportSubtitle_Click"' in editor and "private async void ImportSubtitle_Click" in editor_partials
        and "await ImportSubtitleAsync();" in editor_partials,
        "SUB-01 requires one Import Subtitle handler calling one ImportSubtitleAsync method")
require("ImportSrt_Click" not in editor_partials and "ImportSrtAsync" not in editor_partials,
        "SUB-01 legacy Import SRT handler/method returned")
require("var path = await _picker.PickSubtitleAsync();" in editor_partials
        and "if (string.IsNullOrWhiteSpace(path)) return;" in editor_partials,
        "SUB-02/SUB-04 subtitle picker cancel-safe path missing")
require("candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);" in editor_partials
        and editor_partials.index("candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);")
            < editor_partials.index("_subtitleSource = candidate;"),
        "SUB-05 must validate candidate SRT before replacing current subtitle state")
require("SRT không hợp lệ:" in editor_partials and "AttachSubtitleToProject(string.Empty);" in editor_partials
        and "if (_project is not null) await SaveProjectNowAsync();" in editor_partials,
        "SUB-03/SUB-05 project attach or semantic invalid-SRT error contract missing")
require("SeekEditorToSubtitleCueAsync" in editor_partials and "await SeekEditorToSubtitleCueAsync(cue.Start);" in editor_partials
        and "if (_playerMode) await SeekProcessedPreviewAsync(target);" in editor_partials,
        "SUB-06 cue selection must seek the compact Player to cue start")
require("if (_playerMode) await SetPlaybackModeAsync(false, false);" not in editor_partials,
        "SUB-06 cue selection regressed to leaving processed Player mode before seek")
'''
validate = replace_once(validate, anchor, insert, "SUB static contracts")
VALIDATE.write_text(validate, encoding="utf-8")

# Extend the existing SRT contract with malformed/empty/timecode rejection.
tests = TESTS.read_text(encoding="utf-8")
test_anchor = '''        Equal(2, EditorSubtitleDocument.Parse(sourceRendered).Count);
        return Task.CompletedTask;
    }
'''
test_insert = '''        Equal(2, EditorSubtitleDocument.Parse(sourceRendered).Count);
        foreach (var invalid in new[]
        {
            string.Empty,
            "1\\nnot-a-timecode\\n你好\\n",
            "1\\n00:00:02,000 --> 00:00:01,000\\n你好\\n",
            "1\\n00:00:01,000 --> 00:00:02,000\\n\\n",
        })
        {
            var rejected = false;
            try { _ = EditorSubtitleDocument.Parse(invalid); }
            catch (InvalidDataException) { rejected = true; }
            True(rejected, "invalid Editor SRT was accepted");
        }
        return Task.CompletedTask;
    }
'''
tests = replace_once(tests, test_anchor, test_insert, "SUB-05 invalid SRT contract")
TESTS.write_text(tests, encoding="utf-8")

print("APPLIED SUB-01 through SUB-06")

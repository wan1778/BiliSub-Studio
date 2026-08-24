#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
CUES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs"
VALIDATE = ROOT / "csharp/scripts/validate_csharp_migration.py"
MANUAL_TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorSubtitleManualContract.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one occurrence, found {count}")
    return text.replace(old, new, 1)

# SUB-07/SUB-11: Preview overlay must use the current editor draft text while a cue is being edited.
page = PAGE.read_text(encoding="utf-8")
old = '''        var cue = CurrentSubtitleCue();
        var text = cue is null ? "Kéo để đặt vị trí phụ đề" :
            string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText;
'''
new = '''        var cue = CurrentSubtitleCue();
        var text = cue is null ? "Kéo để đặt vị trí phụ đề" : SubtitlePreviewText(cue);
'''
page = replace_once(page, old, new, "SUB-07/SUB-11 overlay text")
PAGE.write_text(page, encoding="utf-8")

cues = CUES.read_text(encoding="utf-8")
old = '''    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_subtitleCueSyncing || _subtitleSource is null || _subtitleCueSelectedIndex < 0) return;
        _subtitleManualDirty = true;
        SubtitleCueEditorStatus.Text = "Câu đang có thay đổi chưa lưu. Render/TTS/SRT Việt tạm khóa để tránh dùng bản cũ.";
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
    }
'''
new = '''    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
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
'''
cues = replace_once(cues, old, new, "SUB-11 live draft preview")

# SUB-15: cleanup-aware cancellation is not an error. Keep the last atomic full-translation checkpoint,
# clear the UI job owner only after the core job reaches terminal cancelled state, and never attach stale output.
old = '''                TranslationProgress.Value = snapshot.Progress;
                TranslationStatusText.Text = snapshot.Message;
                if (!snapshot.Done) { await Task.Delay(350); continue; }
                if (snapshot.Result is not EditorTranslationResult result)
                    throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
'''
new = '''                TranslationProgress.Value = snapshot.Progress;
                TranslationStatusText.Text = snapshot.Message;
                if (!snapshot.Done) { await Task.Delay(350); continue; }
                if (string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    TranslationStatusText.Text = "Đã hủy Vietsub an toàn. Chỉ checkpoint của các batch hoàn tất được giữ để có thể tiếp tục sau; output cũ vẫn bị khóa.";
                    return;
                }
                if (snapshot.Result is not EditorTranslationResult result)
                    throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
'''
cues = replace_once(cues, old, new, "SUB-15 full translation cancel")

old = '''            SubtitleCueEditorStatus.Text = snapshot.Message;
            if (!snapshot.Done) { await Task.Delay(300); continue; }
            if (snapshot.Result is not EditorTranslationResult result || result.Cues.Count != 1)
                throw new InvalidOperationException(snapshot.Error ?? snapshot.Message);
'''
new = '''            SubtitleCueEditorStatus.Text = snapshot.Message;
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
'''
cues = replace_once(cues, old, new, "SUB-15 cue translation cancel")
CUES.write_text(cues, encoding="utf-8")

# Strengthen existing manual subtitle contract: latest edits render into Vietnamese SRT and timeline stays exact.
test = MANUAL_TEST.read_text(encoding="utf-8")
old = '''            if (applied.Cues[0].Timing != source.Cues[0].Timing || applied.Cues[1] != source.Cues[1])
                throw new InvalidOperationException("manual cue state changed untouched timeline/cues");
            await store.SaveAsync(sha, new Dictionary<string, EditorManualCueState>(), CancellationToken.None);
'''
new = '''            if (applied.Cues[0].Timing != source.Cues[0].Timing || applied.Cues[1] != source.Cues[1])
                throw new InvalidOperationException("manual cue state changed untouched timeline/cues");
            EditorSubtitleDocument.ValidateUnchangedTimeline(source.Cues, applied.Cues);
            var renderedLatest = EditorSubtitleDocument.RenderVietnamese(applied.Cues);
            if (!renderedLatest.Contains("Sư tôn đại nhân", StringComparison.Ordinal) || renderedLatest.Contains("Sư tôn\\r\\n", StringComparison.Ordinal))
                throw new InvalidOperationException("latest manual Vietnamese edit was not used for SRT output");
            await store.SaveAsync(sha, new Dictionary<string, EditorManualCueState>(), CancellationToken.None);
'''
test = replace_once(test, old, new, "SUB-16 latest edit contract")
MANUAL_TEST.write_text(test, encoding="utf-8")

# Static regression coverage for SUB-07..18. These are architecture/ownership gates; runtime correctness remains
# covered by WinUI startup/layout + Core contracts + final field QA.
validate = VALIDATE.read_text(encoding="utf-8")
anchor = '''require("if (_playerMode) await SetPlaybackModeAsync(false, false);" not in editor_partials,
        "SUB-06 cue selection regressed to leaving processed Player mode before seek")
'''
addition = anchor + '''require("RenderSubtitlePlacement" in editor_partials and "SubtitlePreviewText(cue)" in editor_partials
        and "PreviewSubtitleBurn()" in editor_partials,
        "SUB-07 subtitle caption must render on edit-frame and processed Preview paths")
require("_subtitleDrag = true;" in editor_partials and "HitTestSubtitle(point)" in editor_partials
        and "ResizeOrMove(_subtitleDragOriginal" in editor_partials,
        "SUB-08 subtitle drag ownership missing")
for direction in ("North", "South", "East", "West", "NorthEast", "NorthWest", "SouthEast", "SouthWest"):
    require(f"DragKind.{direction}" in editor_partials, f"SUB-09 subtitle resize direction missing: {direction}")
require("SourceOverride" in editor_partials and "SubtitleSourceEdit.Text.Trim()" in editor_partials,
        "SUB-10 Chinese cue edit state missing")
require("Preview hiển thị bản nháp" in editor_partials and "RenderOverlays();" in editor_partials
        and "SubtitleVietnameseEdit.Text.Trim()" in editor_partials,
        "SUB-11 Vietnamese cue edit must update Preview draft immediately")
require("state.Locked" in editor_partials and "locked.TryGetValue(c.Id, out var keep)" in editor_partials,
        "SUB-12 locked cue protection missing from full Vietsub merge")
require("await RetranslateSelectedCueAsync();" in editor_partials and "ForceFresh: true" in editor_partials
        and "SubtitleRetranslateCue_Click(sender" not in editor_partials,
        "SUB-13 clean force-fresh cue retranslation contract missing")
translation_source = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs")
require("TranslationSkillBundle.Load" in translation_source and "ValidateUnchangedTimeline(source, translated)" in translation_source
        and "Qwen3-8B" in translation_source,
        "SUB-14 local AI + skill + exact timeline Vietsub contract missing")
require('string.Equals(snapshot.Status, "cancelled"' in editor_partials
        and "finally { TryDelete(temporary); }" in translation_source
        and "cleanupAwareCancel: true" in composition,
        "SUB-15 translation cancellation/checkpoint cleanup contract missing")
require("await SaveCurrentSubtitleCueAsync();" in editor_partials and "EditorSubtitleDocument.RenderVietnamese(cues)" in editor_partials,
        "SUB-16 save Vietnamese SRT must include latest cue edit")
require("MarkTranslatedOutputStale();" in editor_partials and "OutputPath = string.Empty" in editor_partials
        and "File.Exists(_project?.Subtitle?.OutputPath)" in editor_partials,
        "SUB-17 stale Vietnamese SRT protection missing")
require("RestoreSubtitleAsync(_project.Subtitle)" in editor_partials and "SubtitleManualStore.LoadAsync" in editor_partials
        and "EditorSubtitleManualStore.Apply" in editor_partials,
        "SUB-18 project reopen must restore translation/edit/lock state")
'''
validate = replace_once(validate, anchor, addition, "SUB-07..18 static contracts")
VALIDATE.write_text(validate, encoding="utf-8")

print("APPLIED SUB-07 through SUB-18 completion")

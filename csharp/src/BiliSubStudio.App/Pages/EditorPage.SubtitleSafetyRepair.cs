using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _subtitleSafetyRepairInitialized;

    private void EnsureSubtitleSafetyRepairInitialized()
    {
        if (_subtitleSafetyRepairInitialized) return;
        _subtitleSafetyRepairInitialized = true;
        if (_subtitleSourceEdit is not null) _subtitleSourceEdit.TextChanged += SubtitleManualText_TextChanged;
        if (_subtitleVietnameseEdit is not null) _subtitleVietnameseEdit.TextChanged += SubtitleManualText_TextChanged;
        if (_subtitleRetranslateCueButton is not null)
        {
            _subtitleRetranslateCueButton.Click -= SubtitleRetranslateCue_Click;
            _subtitleRetranslateCueButton.Click += SubtitleRetranslateFresh_Click;
        }
    }

    private void SubtitleManualText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_subtitleCueEditorSyncing) return;
        MarkSubtitleOutputDirty();
    }

    private void MarkSubtitleOutputDirty()
    {
        if (_project?.Subtitle is not { } subtitle || string.IsNullOrWhiteSpace(subtitle.OutputPath)) return;
        _project = _project with { Subtitle = subtitle with { OutputPath = string.Empty }, Tts = null };
        _voiceTrack = null;
        OpenTranslatedSrtButton.IsEnabled = false;
        TranslationStatusText.Text = "Phụ đề đã thay đổi; hãy Lưu SRT Việt hoặc Vietsub lại để tạo file đầu ra mới.";
    }

    private async void SubtitleRetranslateFresh_Click(object sender, RoutedEventArgs e)
    {
        if (_translationJobId is not null || _project is null || _subtitleSource is null ||
            _subtitleCueEditorSelectedIndex < 0 || _subtitleCueEditorSelectedIndex >= _subtitleSource.Cues.Count) return;
        var index = _subtitleCueEditorSelectedIndex;
        var cue = _subtitleSource.Cues[index];
        var before = cue.VietnameseText;
        var projectId = TranslationProjectId(_project.Id, "cue", ShortSourceTextHash([cue]));
        var checkpoint = Path.Combine(_application.Paths.Data, "Projects", "Translation", projectId + ".json");
        try { File.Delete(checkpoint); } catch { }

        SubtitleRetranslateCue_Click(sender, e);
        await Task.Yield();
        while (_translationJobId is not null) await Task.Delay(200);
        if (_subtitleSource is null || index >= _subtitleSource.Cues.Count) return;
        if (string.Equals(before, _subtitleSource.Cues[index].VietnameseText, StringComparison.Ordinal)) return;
        MarkSubtitleOutputDirty();
        await SaveProjectNowAsync();
        RefreshEditorActions();
        RefreshSubtitleCueEditorControls();
        if (_subtitleCueEditorStatus is not null)
            _subtitleCueEditorStatus.Text = $"Đã dịch lại câu {_subtitleSource.Cues[index].Number}. File SRT cũ đã được đánh dấu hết hạn; bấm Lưu SRT Việt để xuất bản mới.";
    }
}

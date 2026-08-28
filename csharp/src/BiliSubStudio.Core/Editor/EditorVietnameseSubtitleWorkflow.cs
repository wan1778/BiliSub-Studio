namespace BiliSubStudio.Core.Editor;

/// <summary>One readiness policy for externally translated SRT, editing and Voice UI.</summary>
public static class EditorVietnameseSubtitleWorkflow
{
    public static bool HasDraftChange(EditorSubtitleCue cue, string draft) =>
        !string.Equals(Normalize(draft), Normalize(cue.VietnameseText), StringComparison.Ordinal);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    public static string? VoiceBlockReason(bool hasMedia, bool busy, bool preview, bool dirty, EditorSubtitleSource? subtitle)
    {
        if (busy) return "Đang xử lý tác vụ; chờ hoàn tất hoặc hủy trước khi tạo voice.";
        if (preview) return "Dừng chế độ preview trước khi tạo voice.";
        if (!hasMedia) return "Mở video và thêm SRT Việt để tạo voice. Nghe thử không cần video.";
        if (subtitle is null || subtitle.Cues.Count == 0) return "Thêm SRT Việt đã dịch để tạo voice.";
        if (dirty) return "Lời Việt đang có thay đổi chưa lưu. Bấm Lưu câu trong Văn bản.";
        var missing = subtitle.Cues.Count(cue => string.IsNullOrWhiteSpace(cue.VietnameseText));
        return missing > 0 ? $"Còn {missing} câu thiếu lời Việt. Hãy thêm SRT Việt hoặc điền đủ lời đọc." : null;
    }
}

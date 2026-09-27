using System.Text.Json;

namespace BiliSubStudio.Core.Presence;

// Nội dung presence Discord (thuần logic, có contract test): kind job ->
// chữ details tiếng Việt + payload activity JSON (assets logo, timestamps).
// IPC + vòng poll nằm ở App (DiscordPresenceService) để Core không dính IO.
public static class DiscordPresenceContent
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string ActivityName(string kind) => kind switch
    {
        "ocrscan" => "Quét OCR",
        "ocr-listen" => "Nghe thoại",
        "editor-asr" => "Phân tích thoại",
        "editor-tts" => "Tạo voice",
        "translation" => "Dịch sub",
        "translation-prepare" => "Chuẩn bị dịch",
        "subtitle" => "Tải sub",
        "editor" => "Xuất Edit",
        _ => kind,
    };

    public static string BuildActivityJson(string details, string state, long? startUnix, string largeImageKey = "logo")
    {
        var timestamps = startUnix is { } start ? $"\"timestamps\":{{\"start\":{start}}}," : string.Empty;
        return "{\"details\":" + JsonSerializer.Serialize(details, Json)
            + ",\"state\":" + JsonSerializer.Serialize(state, Json)
            + "," + timestamps + "\"assets\":{\"large_image\":"
            + JsonSerializer.Serialize(largeImageKey, Json)
            + ",\"large_text\":\"BiliSub Studio\"}}";
    }

    // Pure (có contract test): rút gọn tên file dài cho vừa khung state Discord.
    public static string ShortName(string? name, int maxLength = 40)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length <= maxLength) return text;
        return "..." + text[^Math.Max(1, maxLength - 3)..];
    }

    // Pure: state khi có job chạy (job thắng mọi context).
    public static string ComposeJobState(double progress, string status, bool pauseRequested, string? video) =>
        $"{progress:0}%"
        + (string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase) ? " · đang dừng"
            : string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase) || pauseRequested ? " · tạm dừng" : string.Empty)
        + (string.IsNullOrWhiteSpace(video) ? string.Empty : " · " + ShortName(video));

    // Pure: state khi rảnh (ưu tiên tên project Edit, rồi tên video).
    public static string ComposeIdleState(string? video, string? project) =>
        !string.IsNullOrWhiteSpace(project) ? ShortName(project)
        : !string.IsNullOrWhiteSpace(video) ? ShortName(video) : "Đang xem";
}

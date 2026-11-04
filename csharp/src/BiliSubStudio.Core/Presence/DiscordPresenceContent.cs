using System.Text.Json;

namespace BiliSubStudio.Core.Presence;

// Discord presence content (pure logic, covered by contract tests): job kind ->
// Vietnamese details text + the activity JSON payload (logo assets, timestamps).
// IPC and the polling loop live in App (DiscordPresenceService) so Core stays
// free of IO.
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

    // Pure (covered by a contract test): shortens a long file name to fit Discord's state field.
    public static string ShortName(string? name, int maxLength = 40)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length <= maxLength) return text;
        return "..." + text[^Math.Max(1, maxLength - 3)..];
    }

    // Pure: state while a job is running (the job wins over every context).
    public static string ComposeJobState(double progress, string status, bool pauseRequested, string? video) =>
        $"{progress:0}%"
        + (string.Equals(status, "cancelling", StringComparison.OrdinalIgnoreCase) ? " · đang dừng"
            : string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase) || pauseRequested ? " · tạm dừng" : string.Empty)
        + (string.IsNullOrWhiteSpace(video) ? string.Empty : " · " + ShortName(video));

    // Pure: idle state (Edit project name first, then the video name).
    public static string ComposeIdleState(string? video, string? project) =>
        !string.IsNullOrWhiteSpace(project) ? ShortName(project)
        : !string.IsNullOrWhiteSpace(video) ? ShortName(video) : "Đang xem";
}

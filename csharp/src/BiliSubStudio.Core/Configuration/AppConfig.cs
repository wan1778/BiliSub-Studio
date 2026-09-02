using System.Text.Json.Serialization;

namespace BiliSubStudio.Core.Configuration;

public sealed record AppConfig
{
    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "dark";

    [JsonPropertyName("output_dir")]
    public string OutputDirectory { get; init; } = string.Empty;

    [JsonPropertyName("ocr_output_dir")]
    public string OcrOutputDirectory { get; init; } = string.Empty;

    [JsonPropertyName("sub_format")]
    public string SubtitleFormat { get; init; } = "srt";

    [JsonPropertyName("video_speed")]
    public string VideoSpeed { get; init; } = "fast";

    [JsonPropertyName("video_container")]
    public string VideoContainer { get; init; } = "mp4";

    [JsonPropertyName("video_mode")]
    public string VideoMode { get; init; } = "video+audio";

    [JsonPropertyName("check_updates")]
    public bool CheckUpdates { get; init; } = true;

    [JsonPropertyName("asr_execution_mode")]
    public string AsrExecutionMode { get; init; } = "gpu";

    [JsonPropertyName("ocr_device")]
    public string OcrDevice { get; init; } = "auto";

    [JsonPropertyName("ocr_top")]
    public int OcrTop { get; init; } = 65;

    [JsonPropertyName("ocr_bottom")]
    public int OcrBottom { get; init; } = 94;

    [JsonPropertyName("ocr_left")]
    public int OcrLeft { get; init; } = 5;

    [JsonPropertyName("ocr_right")]
    public int OcrRight { get; init; } = 95;
}

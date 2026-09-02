namespace BiliSubStudio.Core.Configuration;

public static class AppConfigNormalizer
{
    public static AppConfig Normalize(AppConfig? config, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        config ??= new AppConfig();

        var theme = config.Theme == "light" ? "light" : "dark";
        var outputDirectory = string.IsNullOrWhiteSpace(config.OutputDirectory)
            ? paths.DefaultDownloads
            : config.OutputDirectory;
        var ocrOutputDirectory = string.IsNullOrWhiteSpace(config.OcrOutputDirectory)
            ? outputDirectory
            : config.OcrOutputDirectory;
        var subtitleFormat = config.SubtitleFormat is "srt" or "txt" or "json"
            ? config.SubtitleFormat
            : "srt";
        var videoSpeed = config.VideoSpeed is "stable" or "fast" or "turbo"
            ? config.VideoSpeed
            : "fast";
        var videoContainer = config.VideoContainer == "mkv" ? "mkv" : "mp4";
        var videoMode = config.VideoMode is "video+audio" or "video-only" or "audio-only"
            ? config.VideoMode
            : "video+audio";

        var ocrDevice = (config.OcrDevice ?? string.Empty).Trim().ToLowerInvariant();
        if (ocrDevice is not ("cpu" or "gpu" or "hybrid"))
        {
            ocrDevice = "auto";
        }

        return config with
        {
            Theme = theme,
            OutputDirectory = outputDirectory,
            OcrOutputDirectory = ocrOutputDirectory,
            SubtitleFormat = subtitleFormat,
            VideoSpeed = videoSpeed,
            VideoContainer = videoContainer,
            VideoMode = videoMode,
            OcrDevice = ocrDevice,
            AsrExecutionMode = config.AsrExecutionMode is "cpu" or "hybrid" ? config.AsrExecutionMode : "gpu",
            OcrTop = config.OcrTop <= 0 ? 65 : config.OcrTop,
            OcrBottom = config.OcrBottom <= 0 ? 94 : config.OcrBottom,
            OcrLeft = config.OcrLeft < 0 ? 5 : config.OcrLeft,
            OcrRight = config.OcrRight <= 0 ? 95 : config.OcrRight,
        };
    }
}

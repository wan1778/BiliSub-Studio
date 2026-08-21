from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]

def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")

def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")

models = read("csharp/src/BiliSubStudio.Core/Video/VideoModels.cs")
resolver = read("csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs")
download = read("csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs")
range_downloader = read("csharp/src/BiliSubStudio.Core/Video/RangeDownloader.cs")
application = read("csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs")
subtitle = read("csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs")
page = read("csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs")
xaml = read("csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml")

require('string ThumbnailUrl = ""' in models, "VideoMetadata must carry thumbnail URL")
require("bool BundleSubtitleIfAvailable = false" in models, "media request must support optional subtitles")
require("bool BundleThumbnail = false" in models, "media request must support thumbnail bundling")
require("info.Thumbnail" in resolver and '[JsonPropertyName("thumbnail")]' in resolver, "resolver must expose yt-dlp thumbnail")

choose_start = resolver.index("private static YtDlpFormat? ChooseVideo")
choose_end = resolver.index("private static YtDlpFormat? ChooseAudio", choose_start)
choose = resolver[choose_start:choose_end]
order_tokens = [
    ".OrderByDescending(x => x.Height.GetValueOrDefault())",
    ".ThenByDescending(x => x.Fps.GetValueOrDefault())",
    ".ThenByDescending(x => x.Quality.GetValueOrDefault())",
    ".ThenByDescending(x => x.TotalBitrate.GetValueOrDefault())",
    ".ThenBy(x => preferAvc ? CodecRank(x.VideoCodec) : 0)",
]
positions = [choose.find(token) for token in order_tokens]
require(all(position >= 0 for position in positions), "highest-quality ordering is incomplete")
require(positions == sorted(positions), "codec preference must not outrank resolution/FPS/quality/bitrate")

require('Path.Combine(outputDirectory, ".BiliSubStudio")' in download, "long-media work files must live on the selected output drive")
require("estimatedStreams * 2 + reserve" in download and "AvailableFreeSpace" in download, "large-media free-space preflight is missing")
require("public const long DefaultChunkSize" in range_downloader, "Range chunk size must remain 64-bit")
require("private sealed record Segment(int Index, long Start, long End)" in range_downloader, "Range segment offsets must remain 64-bit")
require("private sealed record ResumeManifest(int Version, long Total, long ChunkSize" in range_downloader, "resume manifest sizes must remain 64-bit")

cleanup_match = re.search(r"private static void CleanupFallbackTemporary\(string prefix\)(.*?)private static void TryDelete", download, re.S)
require(cleanup_match is not None, "fallback cleanup owner is missing")
cleanup = cleanup_match.group(1)
require(".tmp" in cleanup, "fallback cleanup must still remove temporary scratch files")
require(".part" not in cleanup and ".ytdl" not in cleanup, "fallback cleanup must preserve yt-dlp resume state")
require('"--continue"' in download and '"--retries", "20"' in download, "yt-dlp fallback resume/retry flags are missing")

require("BundleThumbnail: true" in page, "Tải media must request thumbnail")
require("BundleSubtitleIfAvailable: true" in page, "Tải media must request subtitle only when available")
require("TrackBox.SelectedItem as SubtitleTrack" in page, "subtitle selection must be optional")
require("StartButton.IsEnabled = QualityBox.SelectedItem is not null && hasOutput" in page, "missing subtitle must not disable media download")
require('Content="Tải media"' in xaml, "primary CTA must represent the complete media bundle")
require("video + thumbnail + phụ đề nếu nguồn có" in xaml, "UI must explain optional subtitle behavior")

require("request.BundleThumbnail || bundledSubtitle" in application, "parent media job must own thumbnail/subtitle bundle")
require("metadata.ThumbnailUrl" in application, "parent media job must download the resolved thumbnail")
require("source không có track phù hợp; video vẫn hoàn tất bình thường" in application, "no-subtitle path must remain non-fatal")
require("media phụ có cảnh báo" in application, "thumbnail/subtitle failure must not discard a completed long video")

require("128L * 1024 * 1024" in subtitle, "long-video subtitle cap must remain 128 MiB")
require("for (var attempt = 1; attempt <= 4; attempt++)" in subtitle, "subtitle HTTP retry contract is missing")
require("value.TotalHours" in subtitle, "SRT rendering must support durations beyond 24 hours")

print("PASS: long-media/highest-quality/thumbnail/optional-subtitle/resume contracts")

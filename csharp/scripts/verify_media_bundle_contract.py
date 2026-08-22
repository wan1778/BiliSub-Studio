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
application_lower = application.lower()
xaml_lower = xaml.lower()

require('string ThumbnailUrl = ""' in models, "VideoMetadata must carry thumbnail URL")
require("bool BundleSubtitleIfAvailable = false" in models, "media request must support optional subtitles")
require("bool BundleThumbnail = false" in models, "media request must support thumbnail bundling")
require("bool MediaBundle = false" in models, "request must distinguish media-bundle orchestration from legacy video callers")
require("bool BundleVideo = true" in models, "legacy video callers must keep video enabled by default")
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
require("public const long DefaultChunkSize = 4L * 1024 * 1024" in range_downloader, "field-proven Range chunk size must remain 4 MiB")
require("private const int MaxSegmentAttempts = 32" in range_downloader, "short-read continuation must keep the proven 32-attempt ceiling")
require("private const int MaxProbeAttempts = 6" in range_downloader, "transient CDN probe recovery must have a bounded retry budget")
require("private sealed record Segment(int Index, long Start, long End)" in range_downloader, "Range segment offsets must remain 64-bit")
require("private sealed record ResumeManifest(int Version, long Total, long ChunkSize" in range_downloader, "resume manifest sizes must remain 64-bit")
require("requestStart = segment.Start + existing" in range_downloader, "short-read retry must restart at the missing byte, not segment start")
require("FileMode.OpenOrCreate" in range_downloader and "file.Position = existing" in range_downloader, "partial Range bytes must be preserved between retries")
require("partialAfter - partialBefore" in range_downloader and "gained >= meaningfulProgress" in range_downloader, "tiny-read progress must be distinguished from healthy continuation")
require("weakProgressFailures % 2 == 0" in range_downloader, "repeated tiny reads must refresh the signed stream URL/CDN")
require("immediateRefresh = error is HttpRequestException" in range_downloader, "explicit HTTP transport failures must refresh the signed URL immediately")
require("CDN tạm thời trả HTTP" in range_downloader and "response.IsSuccessStatusCode" in range_downloader, "HTTP 503/429/403 must not be misclassified as permanent no-Range support")
require("Probe HTTP Range thất bại sau" in range_downloader and "await refresh(current.Generation" in range_downloader, "transient probe failures must refresh before fallback")
require("Version = HttpVersion.Version11" in range_downloader and "RequestVersionExact" in range_downloader, "Range worker pool must force exact HTTP/1.1")
require("end = Math.Min(total - 1, start + chunkSize - 1)" in range_downloader, "Range end must include the final byte without off-by-one loss")
require("private static int TransferConnections" in download and '"turbo" => 8' in download, "effective transport budget must retain field-proven Stable/Fast/Turbo 1/4/8 behavior")
require("var budget = TransferConnections(request.Speed)" in download, "video path must use field-stable transfer budget")
require("connections > 1" in download and "tự hạ 1 kết nối trước fallback" in download, "multi-connection Range failures must degrade to one connection before yt-dlp fallback")
require("đã phục hồi ở chế độ 1 kết nối" in download, "successful adaptive degradation must be visible in the diagnostic log")
require("thất bại sau các bước phục hồi" in download and "GetBaseException().Message" in download, "final Range failure log must expose the root transport cause")

cleanup_match = re.search(r"private static void CleanupFallbackTemporary\(string prefix\)(.*?)private static void TryDelete", download, re.S)
require(cleanup_match is not None, "fallback cleanup owner is missing")
cleanup = cleanup_match.group(1)
require(".tmp" in cleanup, "fallback cleanup must still remove temporary scratch files")
require(".part" not in cleanup and ".ytdl" not in cleanup, "fallback cleanup must preserve yt-dlp resume state")
require('"--continue"' in download and '"--retries", "20"' in download, "yt-dlp fallback resume/retry flags are missing")
require('"--http-chunk-size", "4M"' in download, "yt-dlp fallback must avoid one giant response body and use 4 MiB HTTP chunks")

for control in ("VideoAssetCheckBox", "ThumbnailAssetCheckBox", "SubtitleAssetCheckBox"):
    require(f'x:Name="{control}"' in xaml, f"missing separate-download control: {control}")
require("hasExplicitSelection = videoSelected || thumbnailSelected || subtitleSelected" in page, "UI must detect whether the user selected separate assets")
require("downloadVideo = !hasExplicitSelection || videoSelected" in page, "no selection must default to video")
require("downloadThumbnail = !hasExplicitSelection || thumbnailSelected" in page, "no selection must default to thumbnail")
require("downloadSubtitle = !hasExplicitSelection || subtitleSelected" in page, "no selection must default to subtitle-if-available")
require("MediaBundle: true" in page and "BundleVideo: downloadVideo" in page, "UI must send explicit bundle/video selection to core")
require("BundleThumbnail: downloadThumbnail" in page, "UI must honor separate thumbnail selection")
require("BundleSubtitleIfAvailable: downloadSubtitle" in page, "UI must honor separate subtitle selection")
require("TrackBox.SelectedItem as SubtitleTrack" in page, "subtitle selection must remain optional")
require("track.Language.IndexOf(':')" in page, "subtitle language ranking must strip official/AI prefix")
require('Content="Tải media"' in xaml, "primary CTA must remain Tải media")
require("không chọn mục nào" in xaml_lower and "chỉ tải đúng các mục đã chọn" in xaml_lower, "UI must explain default-all versus separate-download behavior")
require("thumbnail" in xaml_lower and "phụ đề nếu nguồn có" in xaml_lower, "UI must explain thumbnail + optional subtitle behavior")
require("1. nguồn bilibili" in xaml_lower and "4. nơi lưu và bắt đầu" in xaml_lower, "media page must keep the clearer staged visual hierarchy")

require("var bundledMedia = request.MediaBundle" in application, "parent job must use explicit media-bundle mode")
require("var bundledVideo = !bundledMedia || request.BundleVideo" in application, "legacy video behavior and separate video selection must coexist")
require("var bundledThumbnail = bundledMedia && request.BundleThumbnail" in application, "thumbnail must be optional in bundle mode")
require("if (bundledVideo)" in application and '"bundle-video"' in application, "video phase must run only when video is selected")
require("if (bundledThumbnail)" in application and "metadata.ThumbnailUrl" in application, "thumbnail phase must run only when selected")
require("if (bundledSubtitle)" in application and "subtitleTrack is null" in application, "subtitle phase must run only when selected and tolerate missing tracks")
require("không có track phù hợp; đã bỏ qua" in application_lower, "missing subtitle must remain a non-fatal skip")
require("nguồn không cung cấp thumbnail; bỏ qua" in application_lower, "missing thumbnail must remain a non-fatal skip")
require("tác vụ đã hoàn tất; có mục bị bỏ qua/cảnh báo" in application_lower, "partial media completion must be explicit")

require("128L * 1024 * 1024" in subtitle, "long-video subtitle cap must remain 128 MiB")
require("for (var attempt = 1; attempt <= 4; attempt++)" in subtitle, "subtitle HTTP retry contract is missing")
require("value.TotalHours" in subtitle, "SRT rendering must support durations beyond 24 hours")

print("PASS: long-media/highest-quality/default-all/separate-assets/short-read/HTTP503/adaptive-range/thumbnail/optional-subtitle/resume contracts")

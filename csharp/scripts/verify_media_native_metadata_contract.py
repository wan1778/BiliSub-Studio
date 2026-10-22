from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
RESOLVER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "YtDlpResolver.cs"
PLAYURL = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "BilibiliPlayurlClient.cs"
SUBTITLES = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "BilibiliSubtitleClient.cs"
TOOLMANAGER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Tools" / "ToolManager.cs"
VERIFY = ROOT / "csharp" / "scripts" / "verify.ps1"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL media native metadata contract: " + message)


def slice_between(text: str, start_marker: str, end_marker: str, label: str) -> str:
    start = text.find(start_marker)
    require(start >= 0, f"missing {label} start marker: {start_marker}")
    end = text.find(end_marker, start)
    require(end > start, f"missing {label} end marker: {end_marker}")
    return text[start:end]


def main() -> int:
    resolver = RESOLVER.read_text(encoding="utf-8")
    playurl = PLAYURL.read_text(encoding="utf-8")
    subtitles = SUBTITLES.read_text(encoding="utf-8")
    toolmanager = TOOLMANAGER.read_text(encoding="utf-8")
    verify = VERIFY.read_text(encoding="utf-8")

    # yt-dlp phải giữ vai trò nguồn chính: fallback chỉ chạy trong catch sau khi
    # LoadInfoAsync thất bại, và chỉ cho URL có BVID (không đụng site khác).
    fallback = slice_between(
        resolver,
        "private async Task<(YtDlpInfo Info, bool Native, string Warning)> LoadInfoWithFallbackAsync",
        "private static bool IsNativeFallbackEligible",
        "LoadInfoWithFallbackAsync",
    )
    require("return (await LoadInfoAsync(url, cookieFile, cancellationToken), false, string.Empty);" in fallback,
            "yt-dlp is no longer the primary metadata source")
    require("if (!IsNativeFallbackEligible(url, error)) throw;" in fallback,
            "fallback is not gated by the extractor-error classifier")
    require("await LoadNativeInfoAsync(url, cookieFile, cancellationToken)" in fallback,
            "fallback does not call the native Bilibili resolver")
    require("API Bilibili" in fallback,
            "fallback warning does not tell the user metadata came from the Bilibili API")

    classifier = slice_between(
        resolver,
        "private static bool IsNativeFallbackEligible",
        "private async Task<YtDlpInfo> LoadNativeInfoAsync",
        "IsNativeFallbackEligible",
    )
    require("BilibiliPlayurlClient.ExtractBvid(url) is null" in classifier,
            "native fallback must stay Bilibili/BVID-only")
    for keyword in ("Unable to extract", "No video formats found", "Unable to download webpage",
                    "Unable to download JSON metadata", "Unable to process JSON"):
        require(keyword in classifier, f"extractor-failure keyword missing: {keyword}")

    native = slice_between(
        resolver,
        "private async Task<YtDlpInfo> LoadNativeInfoAsync",
        "private static string NormalizeCodec",
        "LoadNativeInfoAsync",
    )
    require("await _playurl.GetNativeInfoAsync(url, bvid, cookieFile, cancellationToken)" in native,
            "native metadata does not read view/detail + playurl through the playurl client")
    require("BackupUrls = stream.BackupUrls" in native and
            '["Referer"] = "https://www.bilibili.com/"' in native and
            '["User-Agent"] = BilibiliPlayurlClient.BrowserUserAgent' in native,
            "native formats must carry backup CDN urls plus the Bilibili Referer + browser User-Agent "
            "(Bilibili CDN answers 403 without a User-Agent)")
    require("Id = native.Bvid" in native and "Title = native.Title" in native and
            "Thumbnail = native.ThumbnailUrl" in native,
            "native metadata does not map view/detail summary fields into the resolver info")
    require("native.DurationSeconds" in native,
            "native metadata ignores the view/detail duration for size estimates")
    require("if (formats.Count == 0)" in native,
            "native fallback can silently resolve zero streams")

    # Cả đường tải media lẫn trang metadata đều phải đi qua fallback.
    resolve_method = slice_between(
        resolver,
        "public async Task<StreamSelection> ResolveAsync",
        "public async Task<VideoMetadata> GetMetadataAsync",
        "ResolveAsync",
    )
    metadata_method = slice_between(
        resolver,
        "public async Task<VideoMetadata> GetMetadataAsync",
        "private const int MaxResolveAttempts",
        "GetMetadataAsync",
    )
    require("await LoadInfoWithFallbackAsync(request.Url, request.CookieFile, cancellationToken)" in resolve_method,
            "download resolve does not use the native metadata fallback")
    require("await LoadInfoWithFallbackAsync(url, cookieFile, cancellationToken)" in metadata_method,
            "metadata page does not use the native metadata fallback")
    require("loaded.Native" in resolve_method and "NativeEndpointMap(info)" in resolve_method,
            "native downloads do not feed backup CDN endpoints without a second playurl round-trip")
    require("loaded.Warning" in metadata_method,
            "metadata page drops the native fallback warning")
    require("public IReadOnlyList<string> BackupUrls { get; init; } = [];" in resolver,
            "YtDlpFormat lost the native backup-url carrier")

    # Playurl client: cả view/detail lẫn playurl phải ký qua cùng một signer WBI;
    # view thường (không ký) bị 412 nên không quay lại endpoint đó.
    require("public async Task<BilibiliNativeInfo> GetNativeInfoAsync" in playurl,
            "BilibiliPlayurlClient has no native view/detail + stream method")
    require("internal const string BrowserUserAgent" in playurl,
            "browser User-Agent constant is missing (CDN rejects UA-less downloads)")
    require("private async Task<NativeView> LoadViewDetailAsync" in playurl,
            "native summary does not use the signed view/detail endpoint")
    require('SignedWbiUri("/x/web-interface/wbi/view/detail"' in playurl,
            "view/detail is not signed with the shared WBI signer")
    require('SignedWbiUri("/x/player/wbi/playurl"' in playurl,
            "playurl is not signed with the shared WBI signer")
    require('["platform"] = "web"' in playurl,
            "view/detail query must carry platform=web like yt-dlp")
    require("private static Uri SignedWbiUri(" in playurl,
            "WBI signing is duplicated instead of one shared signer")
    require("private async Task<JsonDocument> LoadPlayurlAsync" in playurl,
            "playurl loader is missing")
    require("LoadPlayurlAsync(bvid, cid, cookieHeader, cancellationToken)" in playurl and
            "LoadPlayurlAsync(bvid, view.Cid, cookieHeader, cancellationToken, mixinKey)" in playurl,
            "endpoint map and native info must share LoadPlayurlAsync without re-fetching the mixin key")
    require("internal static string? ExtractBvid(string? value)" in playurl,
            "ExtractBvid must be reusable by the resolver fallback")
    require("public sealed record BilibiliNativeStream(" in playurl,
            "native stream carrier record is missing")
    require("CollectNativeArray(dash, \"video\", \"video\"" in playurl and
            "CollectNativeArray(dash, \"audio\", \"audio\"" in playurl,
            "native stream parsing does not cover DASH video + audio")
    require("\"backupUrl\", \"backup_url\"" in playurl,
            "native streams do not capture Bilibili backup CDN urls")

    # Subtitle client: không được quay lại tự lấy summary (native summary giờ
    # thuộc playurl client để đi chung một signer WBI đã kiểm chứng).
    require("BilibiliVideoSummary" not in subtitles and "GetSummaryAsync" not in subtitles,
            "subtitle client must not own native summary anymore")
    require("BilibiliPlayurlClient.BrowserUserAgent)" in subtitles,
            "subtitle requests drifted away from the shared browser User-Agent")

    # The browser-facing identity and error copy must stay aligned with the real Chrome
    # profile, otherwise Bilibili answers 412 without a useful explanation.
    require("Chrome/155.0.0.0" in playurl and "(KHTML, like Gecko)" in playurl,
            "native browser User-Agent is not a complete Chrome 155 identity")
    require("DescribeResolveFailure" in resolver and "Đồng bộ lại từ Chrome" in resolver,
            "HTTP 412 does not point the user back to the Chrome session sync")
    require("bị xóa" in resolver and "giới hạn khu vực" in resolver,
            "permanent video failures are not mapped to actionable Vietnamese messages")

    # An owned yt-dlp binary must not stay frozen forever; refresh is bounded so a
    # GitHub outage cannot block download jobs.
    require("YtDlpMaxAge = TimeSpan.FromDays(7)" in toolmanager and
            "ShouldRefreshYtDlp(" in toolmanager and
            "DownloadAtomicAsync(YtDlpUrl, YtDlpPath, cancellationToken)" in toolmanager,
            "app-owned yt-dlp is never refreshed")
    require("YtDlpRefreshRetry" in toolmanager and "catch (OperationCanceledException)" in toolmanager,
            "yt-dlp refresh is not bounded by a retry cooldown/cancellation path")

    require("verify_media_native_metadata_contract.py" in verify,
            "verify.ps1 does not run this contract")

    print("PASS media native metadata: yt-dlp stays primary and refreshes when stale; extractor failures on BVID-only URLs fall back to signed view/detail + playurl DASH streams; Chrome-155 UA, 412/deleted error copy, and native preview warnings stay aligned")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

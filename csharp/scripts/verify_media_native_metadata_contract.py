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

    # The direct Bilibili API is the primary source for every link with a BVID;
    # yt-dlp is only the last resort for pages the API cannot read.
    fallback = slice_between(
        resolver,
        "private async Task<(YtDlpInfo Info, bool Native, string Warning)> LoadInfoWithFallbackAsync",
        "internal static string DescribeResolveFailure",
        "LoadInfoWithFallbackAsync",
    )
    require("BilibiliPlayurlClient.ExtractBvid(url) is not null" in fallback,
            "native metadata must stay Bilibili/BVID-only")
    require("await LoadNativeInfoAsync(url, cookieFile, cancellationToken)" in fallback,
            "native Bilibili resolver is not tried first")
    require("await LoadInfoAsync(url, cookieFile, cancellationToken)" in fallback,
            "yt-dlp fallback is missing")
    require("DescribeResolveFailure(ytError, nativeError!)" in fallback,
            "resolver does not report the native cause when both paths fail")
    require("API Bilibili" in fallback,
            "fallback warning does not tell the user metadata came from the Bilibili API")
    require("IsNativeFallbackEligible" not in resolver,
            "the extractor-error keyword gate is dead code after the native-first switch")

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

    # Both the media download path and the metadata page must go through the
    # fallback.
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

    # Playurl client: both view/detail and playurl must be signed with the same
    # WBI signer; the plain (unsigned) view gets 412, so that endpoint is never
    # returned to.
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

    # Subtitle client: it must not go back to fetching the summary itself (the
    # native summary now belongs to the playurl client so both share one
    # verified WBI signer).
    require("BilibiliVideoSummary" not in subtitles and "GetSummaryAsync" not in subtitles,
            "subtitle client must not own native summary anymore")
    require("BilibiliPlayurlClient.BrowserUserAgent)" in subtitles,
            "subtitle requests drifted away from the shared browser User-Agent")

    # 22/10 field: the unsigned web-interface/view is blocked by Bilibili with
    # 412, so the app wrongly reported "login session expired" and lost the AI
    # (zh) subtitle track. Identity must go through the WBI-signed view/detail
    # of the playurl client and must not die when the AID cannot be fetched
    # (player/v2 only needs the cid).
    require("public async Task<(long Aid, long Cid)?> ResolveSignedIdentityAsync" in playurl,
            "playurl client has no signed view/detail identity resolver")
    require('SignedWbiUri("/x/web-interface/wbi/view/detail"' in playurl,
            "signed identity must reuse the shared WBI signer")
    require("ResolveSignedIdentityAsync" in subtitles,
            "subtitle client does not reuse the signed view/detail identity")
    require("identity.Aid > 0" in subtitles,
            "AI subtitle lookup must be skipped when the aid is unknown")

    # The browser-facing identity and error copy must stay aligned with the real Chrome
    # profile, otherwise Bilibili answers 412 without a useful explanation.
    require("Chrome/155.0.0.0" in playurl and "(KHTML, like Gecko)" in playurl,
            "native browser User-Agent is not a complete Chrome 155 identity")
    require("DescribeResolveFailure" in resolver and "đăng nhập lại bằng QR" in resolver,
            "HTTP 412 does not point the user back to a fresh Bilibili login")
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

    print("PASS media native metadata: the signed Bilibili API is the primary metadata source for BVID links, yt-dlp stays the last-resort fallback, device cookies are merged into every API call, and 412/deleted error copy stays actionable")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

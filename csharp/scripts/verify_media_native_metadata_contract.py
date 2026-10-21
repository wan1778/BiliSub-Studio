from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
RESOLVER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "YtDlpResolver.cs"
PLAYURL = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "BilibiliPlayurlClient.cs"
SUBTITLES = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Video" / "BilibiliSubtitleClient.cs"
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
    require("await _subtitles.GetSummaryAsync(url, bvid, cookieFile, cancellationToken)" in native,
            "native metadata does not reuse the hardened subtitle client for view data")
    require("await _playurl.GetNativeStreamsAsync(url, bvid, cookieFile, cancellationToken)" in native,
            "native metadata does not read DASH streams from playurl")
    require("BackupUrls = stream.BackupUrls" in native and
            '["Referer"] = "https://www.bilibili.com/"' in native,
            "native formats must carry backup CDN urls and the Bilibili Referer header")
    require("Id = summary.Bvid" in native and "Title = summary.Title" in native and
            "Thumbnail = summary.ThumbnailUrl" in native,
            "native metadata does not map view summary fields into the resolver info")
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

    # Playurl client: WBI/cookie/playurl signing phải dùng chung một loader,
    # không nhân đôi logic ký giữa endpoint map và native streams.
    require("public async Task<IReadOnlyList<BilibiliNativeStream>> GetNativeStreamsAsync" in playurl,
            "BilibiliPlayurlClient has no native stream method")
    require("private async Task<JsonDocument> LoadPlayurlAsync" in playurl,
            "playurl signing is duplicated instead of a shared loader")
    require(playurl.count("LoadPlayurlAsync(bvid, cid, cookieHeader, cancellationToken)") >= 2,
            "endpoint map and native streams must share LoadPlayurlAsync")
    require("internal static string? ExtractBvid(string? value)" in playurl,
            "ExtractBvid must be reusable by the resolver fallback")
    require("public sealed record BilibiliNativeStream(" in playurl,
            "native stream carrier record is missing")
    require("CollectNativeArray(dash, \"video\", \"video\"" in playurl and
            "CollectNativeArray(dash, \"audio\", \"audio\"" in playurl,
            "native stream parsing does not cover DASH video + audio")
    require("\"backupUrl\", \"backup_url\"" in playurl,
            "native streams do not capture Bilibili backup CDN urls")

    # Subtitle client: summary đi đúng đường view API đã có fingerprint 412.
    summary = slice_between(
        subtitles,
        "public async Task<BilibiliVideoSummary> GetSummaryAsync",
        "private async Task<(long Aid, long Cid)> ResolveVideoIdentityFallbackAsync",
        "GetSummaryAsync",
    )
    require("https://api.bilibili.com/x/web-interface/view?bvid=" in summary,
            "native summary does not use the Bilibili view API")
    require("await WithFingerprintAsync(cookieHeader, bvid, cancellationToken)" in summary,
            "native summary dropped the buvid fingerprint path (412 hardening)")
    require("duration" in summary and "pic" in summary,
            "native summary does not map duration/thumbnail")

    require("verify_media_native_metadata_contract.py" in verify,
            "verify.ps1 does not run this contract")

    print("PASS media native metadata: yt-dlp stays primary; extractor failures on BVID-only URLs fall back to view summary + signed playurl DASH streams with backup CDNs, Referer header, metadata-page warning, and shared WBI loader")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

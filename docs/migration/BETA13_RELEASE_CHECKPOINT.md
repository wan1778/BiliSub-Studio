# BiliSub Studio 4.0.0-beta.13-csharp-p5 release checkpoint

## Scope

Public-beta update following the real-machine long-media failure and subsequent UI/subtitle requests.

## Call map changes

### WBI / CDN recovery

```text
VideoPage -> BiliSubApplication -> YtDlpResolver
  -> yt-dlp selects exact video/audio format
  -> BilibiliPlayurlClient
       /x/web-interface/nav
         code 0    -> use data.wbi_img
         code -101 -> anonymous session; still use valid data.wbi_img
       signed /x/player/wbi/playurl
       -> baseUrl + backupUrl[] for selected format
  -> VideoDownloadService
       current primary -> cached backup endpoints -> refetch playurl -> 1 connection -> yt-dlp final fallback
  -> RangeDownloader preserves exact missing byte across endpoint rotation
```

### Subtitle priority

```text
VideoPage.LoadMetadata_Click
  -> YtDlpResolver.GetMetadataAsync
       -> BilibiliSubtitleClient
            /x/player/v2 normal/player subtitle metadata
            authenticated /x/v2/subtitle/web/view binary endpoint for AI fallback
       -> yt-dlp subtitle/caption compatibility sources
       -> SubtitleTrackPolicy
            any available/platform subtitle -> use normal class
            otherwise -> Bilibili AI class
  -> TrackBox
  -> SubtitleService -> download -> normalize -> SRT/TXT/JSON
```

The product contract is source-class first: an available/platform subtitle always outranks an AI-generated subtitle. AI is used only when no normal subtitle is available.

### UI fixes

```text
Global log footer
  error count = 0 -> healthy/success badge
  error count > 0 -> danger/red badge + auto-open log drawer

Media header
  technical `Resume + Range` badge -> user-facing `Tải tiếp an toàn`
```

### Update distribution

`csharp/Directory.Build.props` is the single version source. `verify.ps1`, build identity, package filenames, installer filenames, GitHub release tag/title, portable update filename and `update/beta.json` derive from that informational version.

For beta.13 the expected version is `4.0.0-beta.13-csharp-p5`.

Existing beta.12 installations update through:

```text
Cài đặt -> Cập nhật & hỗ trợ -> Kiểm tra cập nhật -> chuẩn bị cập nhật -> đóng ứng dụng
  -> verified GitHub Release portable ZIP
  -> SHA-256 + size validation
  -> transactional Runtime replacement + rollback protection
  -> protected Data/Tools/Temp/Cache/Downloads remain outside Runtime
```

## Mandatory CI gates before publication

- anonymous WBI `code=-101` still yields valid primary + backup CDN endpoints;
- authenticated WBI `code=0` remains valid;
- normal subtitle outranks Bilibili AI;
- no normal subtitle -> authenticated Bilibili AI Protobuf fallback;
- primary short-read -> backup CDN -> exact Range offset -> byte-for-byte final output;
- full C# static/migration contract and generated code map;
- Core contracts + Range regression;
- WinUI compile, startup and layout smoke;
- root launcher + installer install/migration/uninstall smoke.

## Field gate after in-app update

Retest the same ~2-hour Bilibili source from the failing `application.log`. If it fails, preserve and inspect `Data/Logs/application.log`; do not infer success from CI alone.

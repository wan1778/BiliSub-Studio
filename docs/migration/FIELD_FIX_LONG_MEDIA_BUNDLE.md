# Long-media bundle checkpoint

Status: source checkpoint only. No release/promotion is authorized until exact Windows field QA passes.

## Product contract

`Tải media` owns one URL and one parent job:

1. choose explicit user output folder;
2. inspect Bilibili metadata;
3. choose video quality (`best` by default);
4. download thumbnail when the source exposes one;
5. download the selected/highest video and audio streams;
6. mux without re-encoding;
7. download subtitle when a track exists;
8. finish even when the source has no subtitle;
9. report ancillary thumbnail/subtitle failure without discarding a completed long video.

All final media and long-video resume/cache state use the user-selected output drive.

## Call map

```text
VideoPage.LoadMetadata_Click
  -> BiliSubApplication.GetMetadataAsync
  -> SessionStore.WriteNetscapeFileAsync
  -> YtDlpResolver.GetMetadataAsync
      -> yt-dlp -J
      -> VideoMetadata
          title
          quality heights
          preferred subtitle candidates
          thumbnail URL

VideoPage.Start_Click
  -> VideoDownloadRequest
      BundleThumbnail = true
      BundleSubtitleIfAvailable = true
      BundleSubtitleTrack = selected track or empty
  -> BiliSubApplication.StartVideo
      -> JobManager.Create("media")
      -> fresh YtDlpResolver.GetMetadataAsync
      -> optional thumbnail phase
          -> HttpClient GET thumbnail
          -> selected output directory
      -> video phase
          -> VideoDownloadService.RunAsync
              -> YtDlpResolver.ResolveAsync
                  -> highest resolution
                  -> highest FPS
                  -> yt-dlp quality score
                  -> bitrate
                  -> codec preference only as final tie-breaker
              -> output-drive .BiliSubStudio/Cache/video/<resume-key>
              -> disk-space preflight when stream sizes are known
              -> RangeDownloader
                  -> 64-bit offsets/sizes
                  -> 32 MiB chunks
                  -> resume manifest
                  -> URL refresh on repeated segment failure
              -> yt-dlp fallback when Range is unavailable
                  -> --continue
                  -> retries
                  -> preserve .part/.ytdl across error/cancel
              -> FFmpeg stream-copy mux
              -> final user-selected output directory
      -> optional subtitle phase
          -> SubtitleService.RunAsync
              -> fresh metadata resolve
              -> 4 HTTP attempts
              -> up to 128 MiB subtitle payload
              -> SRT/TXT/JSON export
      -> parent Finish once
```

## Why video duration is not a direct limit

The downloader is byte/segment based, not duration based. Range totals, offsets, segment lengths and resume totals are `long` (64-bit). A six-hour source therefore does not hit a duration counter or 32-bit byte offset by design.

Reliability for long media depends instead on:

- sufficient disk space;
- resume state surviving interruption;
- signed stream URL refresh;
- CDN Range support or yt-dlp fallback;
- FFmpeg being able to mux the selected codecs/container.

## Changed ownership

Changed:

- `csharp/src/BiliSubStudio.Core/Video/VideoModels.cs`
- `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs`
- `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs`
- `csharp/src/BiliSubStudio.Core/Subtitle/SubtitleService.cs`
- `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs`
- `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs`
- `csharp/scripts/verify_media_bundle_contract.py`
- `.github/workflows/csharp-p5-windows-x64-installer.yml`

Not changed:

- `RangeDownloader.cs` algorithms/chunk geometry;
- OCR;
- Editor;
- auth/session format;
- updater safety/promotion policy.

## Exact Windows field gate for long media

Do not call this release-ready from CI alone.

### A. Metadata / highest-quality selection

Use a Bilibili source that exposes multiple resolutions.

- `Kiểm tra` succeeds.
- `best` is selected by default.
- log after start records the chosen video format ID and height.
- verify the downloaded video resolution with a media inspector/player.
- if the source exposes a higher-FPS stream at the same resolution, the higher-FPS stream must win before codec preference.

### B. No-subtitle source

Use a source with no subtitle track.

- `Tải media` remains enabled after metadata check when output folder is selected.
- video downloads.
- thumbnail downloads when available.
- log explicitly says no subtitle was available.
- parent job finishes normally.

### C. Thumbnail

Use a source with a visible Bilibili cover image.

- one `[thumbnail]` image appears in the selected output folder;
- file opens as an image;
- thumbnail failure must be visible as a warning, not silently swallowed.

### D. Long source (>6 hours)

Use an actual source longer than six hours.

Before starting:

- select an output drive with enough free space;
- use `best`, `video+audio`, and `fast` first;
- confirm `.BiliSubStudio` resume/cache is created on that selected output drive, not beside the installed EXE.

During download:

- progress continues over time;
- no signed-URL expiry causes permanent stall;
- if a stream URL refresh occurs, job continues;
- cancellation leaves reusable Range segments or yt-dlp `.part/.ytdl` state;
- restarting the same URL/quality/output resumes rather than discarding valid partial state.

After completion:

- final video exists and plays;
- duration matches the source;
- resolution matches highest selected quality;
- audio is present for `video+audio`;
- thumbnail exists when source provides one;
- subtitle exists when source provides one;
- no subtitle source still completes successfully;
- temporary mux file does not remain;
- completed per-job resume directory is removed.

### E. Interrupted fallback path

When practical, test a source/CDN path that falls back from HTTP Range to yt-dlp.

- cancel after `.part`/`.ytdl` appears;
- start the exact same URL/quality/output again;
- fallback resume state must still exist and be reusable.

## CI guard

`csharp/scripts/verify_media_bundle_contract.py` blocks packaging if the source loses any of these static contracts:

- thumbnail metadata/bundle flag;
- optional subtitle behavior;
- quality ordering;
- output-drive cache ownership;
- 64-bit Range offsets/totals;
- preserved yt-dlp fallback resume files;
- long subtitle cap/retry;
- UI contract for `Tải media`.

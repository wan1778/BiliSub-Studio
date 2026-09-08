---
name: gitnexus-area-video
description: "Skill for the Video area of BiliSub-Studio. 95 symbols across 8 files."
---

# Video

95 symbols | 8 files | Cohesion: 81%

## When to Use

- Working with code in `csharp/`
- Understanding how GetTracksAsync, Preferred, GetEndpointMapAsync work
- Modifying video-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Video/BilibiliPlayurlClient.cs` | BuildQuery, BvidRegex, Compact, ContainsCookie, Encode (+17) |
| `csharp/src/BiliSubStudio.Core/Video/BilibiliSubtitleClient.cs` | AddLegacyTracks, AddTrack, BvidRegex, Compact, ContainsCookie (+14) |
| `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | DownloadOneAsync, EndpointUrls, Fingerprint, Host, RefreshAsync (+12) |
| `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs` | AddTracks, ChooseAudio, ChooseVideo, CodecRank, Compact (+10) |
| `csharp/src/BiliSubStudio.Core/Video/RangeDownloader.cs` | WorkerAsync, AssembleAsync, BuildSegments, CreateRequest, DeleteFiles (+9) |
| `csharp/src/BiliSubStudio.Core/Video/SubtitleTrackPolicy.cs` | Preferred, Priority, RawLanguage |
| `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | AddLog, Error, Warn |
| `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | VerifyNormalBeatsAiAsync, VerifyProtobufAiFallbackAsync |

## Entry Points

Start here when exploring this area:

- **`GetTracksAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Video/BilibiliSubtitleClient.cs:30`
- **`Preferred`** (Method) — `csharp/src/BiliSubStudio.Core/Video/SubtitleTrackPolicy.cs:19`
- **`GetEndpointMapAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Video/BilibiliPlayurlClient.cs:30`
- **`GetMetadataAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs:82`
- **`ResolveAsync`** (Method) — `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs:28`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `GetTracksAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/BilibiliSubtitleClient.cs` | 30 |
| `Preferred` | Method | `csharp/src/BiliSubStudio.Core/Video/SubtitleTrackPolicy.cs` | 19 |
| `GetEndpointMapAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/BilibiliPlayurlClient.cs` | 30 |
| `GetMetadataAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs` | 82 |
| `ResolveAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/YtDlpResolver.cs` | 28 |
| `DownloadAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/RangeDownloader.cs` | 25 |
| `Error` | Method | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | 141 |
| `Warn` | Method | `csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs` | 139 |
| `RunAsync` | Method | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 51 |
| `Priority` | Method | `csharp/src/BiliSubStudio.Core/Video/SubtitleTrackPolicy.cs` | 4 |
| `RawLanguage` | Method | `csharp/src/BiliSubStudio.Core/Video/SubtitleTrackPolicy.cs` | 24 |
| `VerifyNormalBeatsAiAsync` | Function | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 20 |
| `VerifyProtobufAiFallbackAsync` | Function | `csharp/tests/BiliSubStudio.SubtitleRegression/Program.cs` | 45 |
| `WorkerAsync` | Function | `csharp/src/BiliSubStudio.Core/Video/RangeDownloader.cs` | 128 |
| `DownloadOneAsync` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 234 |
| `EndpointUrls` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 81 |
| `Fingerprint` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 73 |
| `Host` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 67 |
| `RefreshAsync` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 142 |
| `EndpointCount` | Function | `csharp/src/BiliSubStudio.Core/Video/VideoDownloadService.cs` | 79 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `DownloadOneAsync → Kill` | cross_community | 7 |
| `DownloadOneAsync → Lease` | cross_community | 7 |
| `DownloadOneAsync → TryDelete` | cross_community | 7 |
| `StartSubtitle → BvidRegex` | cross_community | 6 |
| `RunAsync → SendBytesAsync` | cross_community | 6 |
| `DownloadOneAsync → IsOwnedTool` | cross_community | 6 |
| `RunAsync → PartRegex` | cross_community | 5 |
| `RunAsync → Compact` | cross_community | 5 |
| `RefreshAsync → ProcessResult` | cross_community | 5 |
| `RefreshAsync → Kill` | cross_community | 5 |

## How to Explore

1. `context({name: "GetTracksAsync"})` — see callers and callees
2. `query({search_query: "video"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`

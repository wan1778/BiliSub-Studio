# Long-media CDN failure RCA — 2026-08-22

Status: **root cause identified; corrective implementation required before another field candidate**.

This document records the evidence behind the repeated real-machine Bilibili download failures. It intentionally separates the network trigger from the application defect so subsequent changes do not devolve into status-code-specific patches.

## Field evidence

Two rejected/superseded C# field candidates reproduced the same transport family on real Windows/Bilibili traffic:

1. Short body while downloading the selected video stream:

```text
[download] Got error: 379 bytes read, 1435396856 more expected. Giving up after 20 retries
```

2. A later build showed the preceding Range failure explicitly:

```text
Range Video thất bại; chuyển yt-dlp fallback: CDN trả HTTP 503, cần 206.
yt-dlp fallback Video: ERROR: [download] Got error: 985 bytes read, 4144194 more expected. Giving up after 20 retries
```

The second trace is important: a transient CDN failure in the custom Range path was followed by a short-body failure in the nominally independent yt-dlp fallback path.

## Trigger vs root cause

### Network trigger

The Bilibili media endpoint may transiently:

- return HTTP 403/408/429/5xx, including the observed 503;
- terminate a Range response before the advertised Content-Length is satisfied.

Long media is not itself a byte-size or duration overflow condition in BiliSub. Longer transfers simply have more opportunity to encounter CDN throttling, endpoint instability, signed-URL expiry/refresh, or route degradation.

### Application root cause

The current C# media pipeline collapses a selected Bilibili stream to **one URL**:

```text
YtDlpResolver
  -> yt-dlp -J
  -> YtDlpFormat.Url
  -> ResolvedStream.Url
  -> RangeDownloader
```

`ResolvedStream` has one `Url`, not an ordered set of CDN candidates.

The C# `RefreshAsync` path reruns the same yt-dlp metadata resolve. It increments generation, but it does not prove that the refreshed endpoint is a different CDN route. A new generation may therefore point back to the same primary endpoint.

The yt-dlp fallback is also not an independent CDN failover path. It resolves the same Bilibili page and the same format ID through the same Bilibili extractor.

## Why yt-dlp metadata does not provide the missing redundancy

Current yt-dlp `BilibiliBaseIE.extract_formats()` maps Bilibili DASH entries as follows:

```python
'url': traverse_obj(video, 'baseUrl', 'base_url', 'url')
```

and similarly for audio. The normalized format handed to BiliSub exposes the primary `baseUrl`; the Bilibili playurl response can contain `backupUrl` / `backup_url` arrays, but those candidates are not retained in BiliSub's `YtDlpFormat` / `ResolvedStream` model.

Therefore the current effective recovery topology is:

```text
primary CDN becomes unhealthy
  -> Range refresh through yt-dlp
       -> may select the same primary CDN again
  -> custom Range eventually fails
  -> yt-dlp fallback for the same format
       -> may use the same primary CDN family again
  -> same short-body / HTTP failure family can recur
```

This explains why adding retries, handling HTTP 503 specially, or lowering connection count can improve resilience but cannot by itself provide a guaranteed alternate route.

## Regression history

Older release evidence shows two known downloader fixes that must remain preserved:

- v3.9.2: retry a short body from the exact missing byte, restore 4 MiB chunks, effective Fast/Turbo 4/8, up to 32 continuation attempts.
- v3.9.4: fix inclusive Range end/off-by-one and complete the final missing byte instead of repeatedly changing route and failing.

The later frozen beta.12 Go source already regressed part of that behavior:

- `internal/video/downloader.go` defines `DefaultChunkSize = 32 << 20`;
- failed segment work is uncommitted and retried from the segment start;
- the source comment explicitly says CDN candidate ranking/rotation belongs to a separately tested "Stage B" rather than the implemented path.

The C# migration therefore inherited a later behavioral baseline that was not equivalent to the known stable v3.9.2/v3.9.4 transport behavior. The C# branch has since restored 4 MiB chunks, missing-byte continuation, exact HTTP/1.1 and the inclusive end calculation; those fixes must not be removed.

## Corrective architecture

The next implementation must address the root cause rather than one HTTP status:

```text
selected Bilibili format
  -> obtain raw playurl data for that exact stream
  -> StreamEndpoint[] = primary baseUrl + all backupUrl values
  -> deduplicate candidates
  -> RangeDownloader uses one endpoint at a time
       healthy short body with meaningful progress
         -> continue same endpoint from exact missing byte
       repeated pathological tiny read / 403 / 408 / 429 / 5xx
         -> mark endpoint degraded
         -> rotate to next untried endpoint
         -> preserve exact partial byte offset
       all current endpoints exhausted
         -> refetch playurl for a fresh candidate set
         -> continue from the same offset
       multi-connection route still unstable
         -> adaptive one-connection Range using candidate rotation
       all Range recovery exhausted
         -> yt-dlp fallback as last resort only
```

### Required model changes

A stream must retain endpoint candidates rather than a single URL. The concrete model can vary, but it must represent at minimum:

- URL;
- sanitized host identity for diagnostics;
- endpoint order / active index;
- generation or candidate-set generation.

Full signed URLs must never be written to logs.

### Required diagnostics

The shared application log must capture enough sanitized transport data to diagnose a future field failure:

- stream kind and format ID;
- CDN host only, never signed query parameters;
- endpoint index / candidate count;
- Range start/end;
- HTTP status or short-body bytes read/expected;
- whether recovery stayed on the same endpoint, rotated to a backup endpoint, or refetched playurl;
- whether a fresh candidate set actually changed host;
- attempt number and adaptive connection count.

## Source-of-truth rule for endpoint discovery

Do **not** construct alternate CDN URLs by string replacement or hostname guessing.

The alternate endpoints must come from Bilibili's actual playurl response (`baseUrl` plus `backupUrl` / snake_case equivalents) for the selected stream.

Before implementation, choose and document a stable owner for obtaining the raw playurl response. yt-dlp may remain the owner for general metadata / format selection, but `formats[].url` alone is insufficient because it discards the backup candidates needed for failover.

## Acceptance gate before another installer is handed to the user

A replacement candidate is blocked until deterministic tests prove all of the following:

1. primary endpoint 503 -> backup endpoint is used without losing partial bytes;
2. primary endpoint short-reads -> next request starts at the exact missing byte;
3. repeated pathological short reads -> backup endpoint rotation while preserving offset;
4. all initial endpoints fail -> fresh candidate set is obtained and used;
5. a refreshed candidate set that returns the same failed endpoint is detected rather than treated as meaningful failover;
6. completed segments survive endpoint rotation;
7. multi-connection failure can downshift to one connection without restarting the completed work;
8. only after endpoint rotation + refresh + adaptive Range are exhausted may yt-dlp fallback start;
9. no signed URL/query/token is written to the shared log;
10. the existing 379-byte, 503 probe, 503 segment, 32/32 Core, WinUI and installer gates remain green.

The exact ~2-hour real Bilibili reproduction URL must then pass on the user's Windows machine before testing >6-hour media.

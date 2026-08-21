# Plan: Bilibili download performance without retry regressions

Baseline: `1ab851c46a281254b44d8f1bcad2859b67c16410`
Category: performance + concurrency
Status: Stage A implemented and statically verified; Stage B remains deferred until Windows/Bilibili field testing of Stage A.

## 1. Goal

Increase real Bilibili download throughput while preserving the beta.5 contracts for short-body rejection, Range validation, cancellation, resume, signed-URL refresh, fallback, and final media verification.

## 2. Verified current behavior

- `Service.Run` owns the global connection budget: stable=1, fast=4, turbo=8.
- Video+audio with budget>1 reserves one connection for audio and gives the rest to video.
- Beta.5 baseline used 4 MiB part files and up to 16 attempts per failed segment.
- Beta.5 baseline synced each completed 4 MiB part and copied the whole stream again through `concatParts`. Stage A replaces this with a 32 MiB shared preallocated work file plus an atomic completion manifest.
- A short/over body, invalid Content-Range/Content-Length, or 403/412/416 triggers an immediate full resolver refresh through yt-dlp `-J`.
- The resolver serializes `Resolve` with a mutex and exposes one URL per selected format.
- yt-dlp's Bilibili extractor emits `baseUrl/base_url/url` as the format URL but does not expose the Bilibili `backupUrl` list in the public format object. Therefore beta.5 cannot rotate among true backup CDN URLs without adding a separate Bilibili playurl source.
- There is no throughput-based slow-CDN detection in beta.5. A valid but slow CDN can be used for the entire job.

## 3. Reference design facts to preserve conceptually

The studied Bilibili downloader reference uses:

- CDN pre-selection before segmented download;
- filtering/deprioritizing `mcdn` when non-P2P alternatives exist;
- bounded latency probes;
- 32 MiB segments;
- configurable segment parallelism up to 8;
- direct writes into one preallocated output file rather than hundreds of checkpoint files plus a full concat pass;
- throughput checks and CDN rotation when a segment stays below its minimum speed threshold;
- separate HTTP-retry and CDN-rotation budgets.

These are design inputs, not code to copy blindly.

## 4. Key technical decision

Do **not** solve performance by only increasing worker counts.

Implement in two independently testable stages:

### Stage A — local I/O + segment efficiency (low architectural risk)

Target symbols:
- `internal/video.DefaultChunkSize`
- `internal/video.DownloadStream`
- `internal/video.downloadSegment`
- resume/checkpoint helpers/tests

Changes:
1. Raise the default segment size from 4 MiB to 32 MiB.
2. Stop creating one synced file per segment plus a second full concat pass.
3. Preallocate one stream work file to the exact probed size.
4. Workers write only their owned byte range with random-access writes.
5. Persist a compact completion bitmap/manifest so resume still knows which segments are valid.
6. On failed segment, overwrite/retry only that segment; never mark complete until exact byte count and Range validation pass.
7. Final verification checks completed bitmap + exact file size before promoting the work file.

Why first: it removes hundreds of file opens/fsyncs and a complete second copy without changing how URLs are resolved.

### Stage B — CDN intelligence (higher architectural risk)

Target symbols/modules:
- `internal/video/resolver.go`
- new Bilibili playurl/CDN candidate type under `internal/video`
- `DownloadStream` retry selection

Changes:
1. Obtain primary + backup CDN URLs from Bilibili playurl data rather than relying on yt-dlp's single emitted URL.
2. Keep yt-dlp for metadata/format compatibility and robust fallback until the direct resolver path is proven.
3. Filter obvious `mcdn` candidates only when a non-mcdn candidate exists.
4. Probe candidates with a small bounded concurrency and rank usable candidates before transfer.
5. Track per-segment throughput; rotate candidate on short-body/Range corruption immediately and on sustained low throughput after a bounded observation window.
6. Keep HTTP retry and CDN rotation counters separate.
7. Re-fetch signed playurl only after candidate rotation budget is exhausted or URL expiry is indicated.


## 4A. Stage A implementation evidence

Implemented only in `internal/video/downloader.go` plus downloader regression tests and architecture documentation. Connection budgets, retry count, URL resolver, CDN refresh policy, fallback, API, OCR, updater, cookie, and job lifecycle are unchanged.

For a representative 1.77 GiB stream, the deterministic geometry changes from approximately 454 x 4 MiB segments to 57 x 32 MiB segments (about 87.4% fewer segment boundaries). Under the durability contract used here, the structural sync count drops from roughly 455 data-file/concat syncs to 114 work-file + manifest syncs, and the previous extra full-stream concat rewrite (about 1.77 GiB) is eliminated. These are operation-count facts, not a claim about real Windows throughput; field timing remains required.

Graph re-index after the change: 222 symbols, 502 call edges, 28 HTTP routes. The only production call-path replacement is `DownloadStream -> concatParts` becoming `DownloadStream -> openResumeWork/resumeState`; callers above `DownloadStream` are unchanged.

## 5. Files allowed to change

Stage A:
- `internal/video/downloader.go`
- `internal/video/downloader_test.go`
- `internal/video/service.go` only if resume-path plumbing requires it
- `ARCHITECTURE.md` / this plan documentation

Stage B (later, after Stage A field test):
- `internal/video/resolver.go`
- `internal/video/resolver_test.go`
- `internal/video/downloader.go`
- `internal/video/downloader_test.go`
- possibly one new `internal/video` source file dedicated to Bilibili playurl/CDN selection

Do not touch OCR, updater, cookie persistence, job lifecycle, or UI for this performance change unless a verified interface contract requires it.

## 6. Required regression scenarios

Stage A must cover:

1. exact 206 segment -> committed and marked complete;
2. short body -> segment remains incomplete and is retried only for that range;
3. oversized chunked body -> segment remains incomplete;
4. wrong Content-Range -> segment remains incomplete;
5. cancellation -> completed segments survive; partial segment is not marked complete;
6. resume -> completed ranges are not downloaded again;
7. final segment smaller than 32 MiB -> exact inclusive Range handled;
8. Range unsupported -> sequential fallback contract unchanged;
9. refreshed generation -> next retry uses the refreshed stream;
10. output file exact size -> only then considered complete.

Stage B must additionally cover:

1. non-mcdn candidates rank ahead of mcdn when available;
2. all-mcdn list falls back to original candidates rather than failing;
3. primary short body rotates to backup without invoking a full yt-dlp resolve first;
4. sustained slow CDN rotates after threshold window;
5. healthy fast CDN does not rotate;
6. candidate rotation exhaustion triggers signed-url refresh/fallback;
7. cookie/auth headers remain attached to probe and segment requests.

## 7. Verification commands

```text
go test ./...
go vet ./...
go test -race ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -H=windowsgui" -o <exe> ./cmd/bilisub
python scripts/validate_release.py <exe>
```

For performance changes, also run deterministic local HTTP benchmarks that compare request count, bytes rewritten, and elapsed time between beta.5 and the candidate implementation. Real Bilibili field testing remains mandatory before calling the change stable.

## 8. Risks

- Larger segments increase the amount retransmitted for one failed segment; this is offset by far fewer requests and checkpoints. Short-body tests must prove correctness.
- A preallocated shared file introduces concurrent random writes; every worker must own a disjoint range and no code path may truncate the shared file after workers start.
- Resume metadata must be crash-safe; write completion state atomically after the data range is durable enough for the chosen contract.
- Direct Bilibili playurl resolution introduces WBI/cookie/API compatibility risk and therefore belongs in Stage B, not mixed into Stage A.

## 9. Assumptions / open questions

- Exact Windows disk/antivirus benefit from removing per-4-MiB fsyncs must be field-tested; Linux test timing is not authoritative for Windows.
- The direct Bilibili API path must be validated against logged-out, cookie-authenticated, DASH and durl responses before replacing any yt-dlp responsibility.
- Do not infer backup CDN URLs by string substitution; only use candidates returned by a verified source.

## 10. Avoid

- Do not raise Turbo above 8 in Stage A.
- Do not change OCR/updater/cookie code as collateral cleanup.
- Do not remove yt-dlp fallback.
- Do not sacrifice resolution for codec.
- Do not delete resume data on recoverable failure/cancel.
- Do not publish beta.6 before the Stage A regression suite and Windows static release gate pass.

## 11. Implementation context

```yaml
implementation_context:
  task_summary: "Speed up Bilibili downloads without reintroducing CDN short-read corruption"
  baseline_commit: "1ab851c46a281254b44d8f1bcad2859b67c16410"
  acceptance_criteria:
    - "Existing short-body/oversized-body/Range validation remains strict"
    - "Cancel/resume preserves completed work"
    - "No worker-only concurrency increase as the sole optimization"
    - "Stage A changes only local segment I/O/checkpoint architecture"
    - "All release gates pass"
  primary_symbols:
    - "internal/video.DownloadStream"
    - "internal/video.downloadSegment"
    - "internal/video.Service.Run"
  execution_path:
    - "/api/video/download"
    - "api.Server.videoDownloadHandler"
    - "video.Service.Run"
    - "video.YTDLPResolver.Resolve"
    - "video.DownloadStream"
    - "video.downloadSegment"
    - "video.Service.remux/singleTrack"
  files_to_modify_stage_a:
    - "internal/video/downloader.go"
    - "internal/video/downloader_test.go"
  tests:
    - "short/over/wrong-range never mark a segment complete"
    - "cancel then resume reuses completed segments"
    - "final short segment works with 32 MiB segmentation"
    - "Range unsupported fallback remains unchanged"
  assumptions:
    - "Windows field test is required for actual throughput conclusion"
  open_questions:
    - "Stage B direct playurl/WBI implementation details"
  avoid:
    - "No OCR/updater/cookie changes"
    - "No worker count > 8"
    - "No synthesized CDN backup URLs"
```

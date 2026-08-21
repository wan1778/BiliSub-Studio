# Windows field checklist — CSharp P4

> Archived checkpoint: P4 was never promoted and is superseded by P5 installer delivery. Use `WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md` with `.github/workflows/csharp-p5-windows-x64-installer.yml`.

Candidate status starts as **BLOCKED**. Record results for one exact source revision, publish directory and `BiliSubStudio.exe` SHA-256 only.

## Build identity

- [ ] Historical only: current scripts validate P5 and must not be used to label a P4 artifact.
- [ ] Preserve `BUILD_IDENTITY.json`, `SOURCE_SHA256SUMS.txt`, `PUBLISH_SHA256SUMS.txt`, `CANDIDATE_SHA256SUMS.txt`, Windows version and workflow run identity.
- [ ] Confirm unpackaged, self-contained `win-x64`; `BiliSubStudio.exe` is PE32+ AMD64 and `Assets/worker.py` matches the frozen worker.
- [ ] Confirm every manifest says `release_candidate=false`, `promotion_allowed=false` and `field_qa_complete=false` before field QA.
- [ ] Confirm no browser, local listener, console window, Go backend or second app backend appears.

## Native shell, configuration and login

- [ ] Launch from a writable Unicode/space-containing portable path on Windows 10 1809+ and Windows 11.
- [ ] Verify navigation selection, keyboard focus and the visible “Cập nhật & hỗ trợ” destination.
- [ ] Reopen after changing theme, output directory, subtitle format, video settings, OCR device and ROI; verify all twelve JSON fields.
- [ ] Validate native file/folder pickers and Explorer open.
- [ ] Test QR success/pending/expired, manual cookie, DPAPI reopen and logout; plaintext Temp cookie must be removed on close.
- [ ] Start with invalid `Data/session.bin`; verify non-fatal quarantine to `session.bin.invalid` and successful startup.
- [ ] Close or force a progress/parser failure during every active job; confirm no ffmpeg/ffprobe/yt-dlp/python child survives.

## Visual, accessibility and playback matrix

- [ ] Inspect all pages at 1600×900 and 1365×768, 100/125/150/200% DPI, Dark and Light.
- [ ] Check overlap, clipping, scrolling, narrow layout, keyboard order, screen-reader names, live announcements and Vietnamese text.
- [ ] Check disabled/loading/empty/error/success states and repeated-click protection on Account, Hardware, Support, Video and Subtitle.
- [ ] Play H.264 and HEVC MP4; seek, pause/resume, audio and full-window enter/exit.
- [ ] Verify AV1/VP9 where the OS codec exists; unsupported/MKV sources must retain FFmpeg frame fallback without crashing.
- [ ] Draw OCR and Editor ROI without blocking transport controls; also enter Editor ROI by keyboard and verify invalid ranges disable consuming actions.
- [ ] Select OCR cues to seek timeline/player; scrub/play through cues and confirm active selection follows playback.

## Video and subtitle

- [ ] Range-supported and Range-broken CDNs; observe true Stable 1 / Fast 8 / Turbo 16 global budgets and speed.
- [ ] Resume an already complete Range payload and verify terminal progress is shown immediately.
- [ ] Expired URL refresh, yt-dlp fallback, cancel during probe/body/remux, reopen/resume and complete-output collision.
- [ ] Confirm video+audio fallback preserves aggregate connection telemetry instead of reverting to a single-stream value.
- [ ] Test video+audio, video-only and audio-only; MP4/MKV; requested resolution must not be lowered only for AVC.
- [ ] Distinct official and AI tracks; JSON3, VTT and SRT; export SRT/TXT/JSON; cancel and Unicode output path.

## OCR

- [ ] CPU, GPU, Hybrid and Auto; confirm exact PP-OCRv6 Small Ready model/device response.
- [ ] Auto probes 1→2→4→8→16 and stops on resource/duration/throughput rules before Commit.
- [ ] NVDEC success and forced software fallback; manual frame enhanced retry.
- [ ] Pause, close, reopen and resume schema 4; corrupt/null/topology-inconsistent checkpoint must be ignored without a crash.
- [ ] Kill a worker during concurrent requests and replace the pool; waiting calls must fail/recover and a retired pool must not overwrite the new state.
- [ ] Audit Chinese-only SRT, boundary reconciliation and cue timing on short and long videos.

## Editor, update and report

- [ ] Draw multiple Blur/Mosaic/Cover regions, whole/timed scope, MP4/MKV audio behavior, cancel, collision and source preservation.
- [ ] Reject legacy Go, non-PE, PE32, wrong-architecture, zero-section, short-optional-header and non-executable update payloads.
- [ ] Force a post-swap validation failure; verify rollback/relaunch with Data/Tools/Temp/Cache/Downloads preserved.
- [ ] Apply one valid staging WinUI portable ZIP; verify size/SHA/PE32+ x64, breakaway swap, restart and obsolete-runtime cleanup.
- [ ] Send a report fixture; verify cookie/token/user-path redaction and bounded payload.

Only after every applicable item passes: re-hash the exact publish, attach native visual evidence, set field-QA state for that exact SHA-256, read back every promoted artifact, then update the approved release location.

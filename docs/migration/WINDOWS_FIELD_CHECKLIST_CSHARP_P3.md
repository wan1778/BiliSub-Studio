# Windows field checklist — CSharp P3

> Archived checkpoint: P3 was never promoted. Use `WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md` and the matching P5 installer workflow for the current source; do not field-test or promote mixed checkpoints.

Candidate status starts as **BLOCKED**. Record results only for one exact publish directory and `BiliSubStudio.exe` SHA-256.

## Build identity

- [ ] Historical only: an exact P3 source checkout would require its matching P3 verification scripts and SDK `10.0.400`; current scripts validate P4 and must not be used to label a P3 artifact.
- [ ] Preserve `BUILD_IDENTITY.json`, `SOURCE_SHA256SUMS.txt`, `SHA256SUMS.txt`, Windows version, Git revision/source-tree SHA-256 and the complete publish-directory hash list.
- [ ] Confirm unpackaged, self-contained `win-x64`; `Assets/worker.py` matches the frozen worker.
- [ ] Confirm no browser, local listener, console window, Go backend or second app backend appears.
- [ ] Run `csharp/scripts/package_windows_candidate.ps1`; verify the candidate ZIP readback passes and preserve the exact source ZIP, `CANDIDATE_GATE_STATUS.json` and `CANDIDATE_SHA256SUMS.txt`.
- [ ] Confirm both manifests still say `release_candidate=false`, `promotion_allowed=false` and `field_qa_complete=false` before field testing. A GitHub Actions success or uploaded artifact is not release approval.

## Native shell, configuration and login

- [ ] Launch from a writable Unicode/space-containing portable path on Windows 10 1809+ and Windows 11.
- [ ] Reopen after changing theme, output directory, subtitle format, video settings, OCR device and ROI; verify all twelve JSON fields.
- [ ] Validate native file/folder pickers and Explorer open.
- [ ] Test QR success/pending/expired, manual cookie, DPAPI reopen and logout; plaintext Temp cookie must be removed on close.
- [ ] Start with a deliberately invalid `Data/session.bin`; verify non-fatal quarantine to `session.bin.invalid` and successful startup.
- [ ] Close during every active job; confirm no ffmpeg/ffprobe/yt-dlp/python child survives.

## Visual and playback matrix

- [ ] Inspect all pages at 1600×900 and 1365×768, 100/125/150/200% DPI, Dark and Light.
- [ ] Check overlap, clipping, keyboard focus, screen-reader names, disabled/loading/error/success states and Vietnamese text.
- [ ] Play H.264 and HEVC MP4 with native transport controls; seek, pause/resume, audio and full-window enter/exit.
- [ ] Verify AV1/VP9 support where the OS codec exists; MKV/unsupported sources must retain FFmpeg frame fallback without crashing.
- [ ] In OCR, select cues to seek timeline/player; scrub/play through cues and confirm active cue selection follows playback.

## Video and subtitle

- [ ] Range-supported and Range-broken CDNs; observe true Stable 1 / Fast 8 / Turbo 16 global connection budgets and speed.
- [ ] Expired URL refresh, yt-dlp fallback, cancel during probe/body/remux, reopen/resume and complete-output collision.
- [ ] Video+audio, video-only and audio-only; MP4/MKV; requested resolution must not be lowered only for AVC.
- [ ] Distinct official and AI tracks; JSON3, VTT and SRT sources; export SRT/TXT/JSON; cancel and Unicode output path.

## OCR

- [ ] CPU, GPU, Hybrid and Auto; confirm exact PP-OCRv6 Small Ready model/device response.
- [ ] Auto probes 1→2→4→8→16 and stops on resource/duration/throughput rules before Commit.
- [ ] NVDEC success and forced software fallback; manual frame enhanced retry.
- [ ] Pause, close, reopen and resume schema 4 without topology drift; cancel must remove unfinished artifacts only.
- [ ] Kill one OCR worker during concurrent requests; waiting calls must fail/recover rather than hang.
- [ ] Audit Chinese-only SRT, boundary reconciliation and cue timing on short and long videos.

## Editor, update and report

- [ ] Draw multiple Blur/Mosaic/Cover regions, whole/timed scope, MP4/MKV audio behavior, cancel, collision and source preservation.
- [ ] Reject a legacy Go update manifest, non-PE/PE32 payload, wrong architecture and payload containing protected roots.
- [ ] Force a post-swap validation failure; verify old runtime rollback and automatic relaunch with Data/Tools/Temp/Cache/Downloads preserved.
- [ ] Apply one valid staging WinUI portable ZIP; verify size/SHA/PE32+ x64, breakaway swap, restart and removal of obsolete runtime files.
- [ ] Send a report fixture; verify cookie/token/user-path redaction and bounded payload.

Only after every applicable item passes: update the gate status for that exact executable SHA-256, re-hash the exact publish, package candidate/source/sums/manifest/visual evidence, read back every promoted file, then update the approved channel. Never reuse a field report from another workflow run, commit or executable hash.

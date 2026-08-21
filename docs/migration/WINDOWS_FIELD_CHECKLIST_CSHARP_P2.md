# Windows field checklist — CSharp P2

Candidate status starts as **BLOCKED**. Record results only for one exact publish directory and `BiliSubStudio.exe` SHA-256.

## Build identity

- [ ] Run `powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1` without warnings/errors.
- [ ] Record .NET SDK, Windows App SDK, Windows version and EXE SHA-256.
- [ ] Confirm the publish is unpackaged, self-contained `win-x64`, and `Assets/worker.py` matches `internal/ocr/worker.py`.
- [ ] Confirm no browser, local listener, console window or second backend process appears.

## Native shell, configuration and login

- [ ] Launch from a writable Unicode/space-containing portable path on Windows 10 1809+ and Windows 11.
- [ ] Reopen after changing theme, output directory, subtitle format, video settings, OCR device and ROI; verify all twelve JSON fields.
- [ ] Validate native file/folder pickers and Explorer open.
- [ ] QR login, expiry and manual cookie paths; reopen DPAPI session; log out; confirm plaintext Temp cookie is removed on close.
- [ ] Close during every active job; confirm no ffmpeg/ffprobe/yt-dlp/python child survives.

## Visual matrix

- [ ] 1600×900 and 1365×768 at 100/125/150/200% DPI, Dark and Light.
- [ ] Inspect every page for overlap, clipping, keyboard focus, disabled/loading/error/success state and readable Vietnamese text.

## Video and subtitle

- [ ] Preview H.264, HEVC, AV1, VP9 and MKV sources.
- [ ] Range-supported and Range-broken CDNs; observe real Stable 1 / Fast 8 / Turbo 16 budgets and speed.
- [ ] Expired URL refresh, yt-dlp fallback, cancel during probe/body/remux, reopen/resume, complete-output collision.
- [ ] Video+audio, video-only and audio-only; MP4/MKV; verify requested resolution is not lowered only for AVC.
- [ ] Distinct official and AI tracks; JSON3, VTT and SRT sources; export SRT/TXT/JSON; cancel and Unicode output path.

## OCR

- [ ] CPU, GPU, Hybrid and Auto; inspect exact PP-OCRv6 Small Ready model/device response.
- [ ] Auto probes 1→2→4→8→16 and stops on resource/duration/throughput rules before Commit.
- [ ] NVDEC success and forced software fallback; manual frame enhanced retry.
- [ ] Pause, close, reopen and resume schema 4 without topology drift; cancel removes unfinished artifacts only.
- [ ] Audit Chinese-only SRT, boundary reconciliation and cue timing on short and long videos.

## Editor, update and report

- [ ] Draw multiple Blur/Mosaic/Cover regions, whole/timed scope, MP4/MKV audio behavior, cancel, collision and source preservation.
- [ ] Reject a legacy Go update manifest and any payload containing protected portable data roots.
- [ ] Apply a staging WinUI portable ZIP: size/SHA/PE verification, breakaway swap, restart and preserved Data/Tools/Temp/Cache/Downloads.
- [ ] Send a report fixture; verify cookie/token/user-path redaction and bounded payload.

Only after every applicable item passes: re-hash the exact publish, package candidate/source/sums/manifest/visual evidence, read back every promoted file, then update the approved channel.

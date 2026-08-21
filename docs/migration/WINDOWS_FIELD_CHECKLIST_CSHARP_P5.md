# Windows field checklist — CSharp P5 installer

Status starts as **BLOCKED**. Record one exact source revision, app EXE SHA-256 and installer SHA-256. No merge, GitHub Release or promotion is allowed until the exact installer passes this checklist on the real Windows machine.

## Rejected field candidates — never reuse

- `b7d0f438280c6461f6d82f9ec1c0ea9de48a4df3c3afdb764a3097823dd81883` — rejected 2026-08-21: installed app exited during startup and installer UX exposed runtime-tree placement problems.
- `d2c3db9c00dd613696fc7077db30dd2a2f902d6bde288af79dcc008c7e03e361` — rejected 2026-08-22: real-machine WinUI `Layout cycle detected` on Settings.
- `662356fe304b8a2d45339c8aa8cc998eb813d6a720adb178d8ddb173f36efaf5` — rejected 2026-08-22 as a release candidate despite passing basic real-machine install/startup/navigation/resize/restart checks. Migration regression: the C# shell exposed separate `Phụ đề` and `Tải video` tabs instead of the required single `Tải media` workflow that downloads video + subtitle together. Functional field QA stopped at this point.

A replacement candidate must have a different SHA-256 and must restore the unified media workflow before field testing continues.

## Build and installer identity

- [ ] Windows workflow runs from the intended `csharp-p5-installer` source revision with SDK `10.0.400`.
- [ ] Preserve `BUILD_IDENTITY.json`, source/publish/candidate checksum inventories, `INSTALLER_GATE_STATUS.json` and `CANDIDATE_GATE_STATUS.json`.
- [ ] Confirm app and Setup are PE32+ x64 and hashes match every manifest.
- [ ] Confirm all promotion flags remain false before real-machine QA.

## Clean install / shell gate

- [ ] Install as standard user without UAC/admin prompt.
- [ ] Default path is `%LOCALAPPDATA%\Programs\BiliSub Studio`; custom parent/drive still creates a dedicated `BiliSub Studio` child directory.
- [ ] App starts without separately installing .NET, Python, FFmpeg, yt-dlp or PaddleOCR.
- [ ] Startup failure must show a visible Vietnamese dialog and write `%LOCALAPPDATA%\BiliSub Studio\Logs\startup.log`.
- [ ] Seven production navigation items render: `Tải media`, `OCR phụ đề`, `Chỉnh video`, `Hiệu năng`, `Đăng nhập`, `Cập nhật & hỗ trợ`, `Cài đặt`.
- [ ] There is no separate production `Phụ đề` tab and no separate production `Tải video` tab.
- [ ] Open every routed page at 800×600, 1000×700 and 1500×900; Settings must not cause a layout cycle.
- [ ] Resize/minimize/restore, close, reopen and Start-menu launch are stable.

## Unified `Tải media` end-to-end — BLOCKER

- [ ] One URL field owns video and subtitle metadata together.
- [ ] Before `Kiểm tra`, `Tải video + phụ đề` is disabled.
- [ ] `Kiểm tra` returns title, quality list and subtitle-track list on the same page.
- [ ] Default subtitle preference is official Chinese when available.
- [ ] Change URL A to URL B: quality and subtitle track from A disappear immediately and Start becomes disabled.
- [ ] If an old metadata request finishes after the URL was changed, its result is not applied.
- [ ] Select quality/mode/speed/container plus subtitle track/format, then start once.
- [ ] One parent media progress/log surface shows phase `bundle-video` followed by `bundle-subtitle`.
- [ ] Video completes with valid non-empty media output and subtitle completes with valid SRT/TXT/JSON output in the same configured output directory.
- [ ] Stable 1 / Fast 8 / Turbo 16 transport behavior remains correct; Range/fallback telemetry remains visible during video phase.
- [ ] Cancel during video phase stops the media parent job and leaves no orphan yt-dlp/FFmpeg.
- [ ] Cancel during subtitle phase also finishes as cancelled without a hanging parent job.
- [ ] Invalid link/network error clears stale metadata and reports a visible error.

## Player / OCR / editor — BLOCKER

- [ ] H.264/HEVC plus fallback preview, play/pause/mute/seek/fullscreen.
- [ ] OCR ROI strict validation; invalid ROI disables Test/Start and is never silently clamped.
- [ ] OCR Auto preflight plus manual 1/2/4/8; 16 only when resource headroom is sufficient.
- [ ] CPU/GPU/RAM/VRAM and lane/decoder/timing telemetry update while scanning.
- [ ] Pause reaches safe schema-4 checkpoint; active lanes become zero; Resume preserves topology and progress.
- [ ] Close during OCR waits for safe checkpoint or refuses unsafe close.
- [ ] Completed cue list/timeline synchronization works and Chinese SRT rejects foreign standalone OCR garbage.
- [ ] Editor multi-region Blur/Mosaic/Cover validation, export and cancellation work end-to-end.

## Login / settings / maintenance — BLOCKER

- [ ] Cookie/SESSDATA DPAPI save/validate/delete flow.
- [ ] Native QR start/poll/success/expire/retry without browser UI.
- [ ] Dark/Light persists across restart.
- [ ] Default output directory propagates into `Tải media`, OCR and Editor.
- [ ] Storage cleanup, Reset Tools and Remove OCR confirmations/states are correct.
- [ ] Update check and bug-report success/error states are visible and safe.

## Process lifecycle / uninstall — BLOCKER

- [ ] No system Python/FFmpeg/yt-dlp dependency is required.
- [ ] App-owned child processes die on Cancel/Finish/Exit/crash; updater is the only intentional breakaway owner.
- [ ] Install over an existing location preserves `Data/Tools/Temp/Cache/Downloads`.
- [ ] Uninstall removes runtime/shortcuts while protected user roots remain by default.

Only after every applicable BLOCKER passes may the exact Setup EXE be marked as a release candidate or promoted.

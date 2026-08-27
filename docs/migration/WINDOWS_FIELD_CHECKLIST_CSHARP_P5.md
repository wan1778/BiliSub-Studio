# Windows field checklist — CSharp P5 installer

Status starts as **BLOCKED**. Record one exact branch source revision, workflow artifact source revision, app EXE SHA-256 and installer SHA-256. No merge, GitHub Release, update-channel publication or promotion is allowed until the exact installer passes this checklist on the real Windows machine.

## Rejected / superseded field candidates — never promote

- `b7d0f438280c6461f6d82f9ec1c0ea9de48a4df3c3afdb764a3097823dd81883` — rejected 2026-08-21: installed app exited during startup and installer UX exposed runtime-tree placement problems.
- `d2c3db9c00dd613696fc7077db30dd2a2f902d6bde288af79dcc008c7e03e361` — rejected 2026-08-22: real-machine WinUI `Layout cycle detected` on Settings.
- `662356fe304b8a2d45339c8aa8cc998eb813d6a720adb178d8ddb173f36efaf5` — rejected 2026-08-22: migration exposed separate `Phụ đề` and `Tải video` tabs instead of the required single `Tải media` workflow.
- `1b42da4d312096b519465379b488220e7806307d1a3e30aa8f5843442b2aadba` — rejected: successive candidates reused the same installer filename, so stale installer execution could not be distinguished reliably.
- `3028882b7dda590b3ede7e1c3766b73c0da01685494b5b7646310ef5495bc333` — rejected/superseded: real-machine metadata check exposed strict `System.Int64` parsing of yt-dlp `formats[].filesize`.
- `79995e296197fb0f451d15a40a7bd9feff45ca2fb99ec7a61d36c9472ac78b8c` — superseded: long-media hardening passed CI, but explicit asset selection / revised Media UI was not complete.
- `51260889e3b62ab3264aaaa38b32149aa008c3d2334660aa7c3716dfd6445b07` — rejected/superseded: real-machine ~2-hour Bilibili test reproduced `379 bytes read ... more expected`, Range segment 0 failure and yt-dlp fallback exhaustion.
- `bbf956e07bcd4f44d6855bb97ddaff369a96161930fba449153f59531058ee99` — superseded after downloader run 61 passed the dedicated 379-byte continuation fixture, because the subsequent shell/UI requirement replaced per-page logs with one global diagnostic log, consolidated Settings navigation and changed Media layout.
- `8d47d3fd25947ed5dcbf55ac167e512e9d7102028fe4bb42596f3ebed0be980e` — superseded: shared-log/consolidated-shell candidate passed CI, but real-machine inspection showed the installer still unpacked the complete self-contained WinUI/.NET runtime directly into the user-visible install root. The replacement must use the reviewed `Runtime\` layout and migrate the old flat runtime safely.
- `7f37dcb5235f0446d8da1b0ea8e49fa0beaf3e0a3297321993f1dd5715548be0` — rejected 2026-08-22: real-machine long-video transfer still treated a transient CDN HTTP `503` as permanent Range failure, immediately switched to yt-dlp fallback, then yt-dlp failed with `985 bytes read, 4144194 more expected` after 20 retries. Never promote this installer.

Any replacement candidate must have a different SHA-256 and must preserve the 379-byte continuation fix, transient-CDN recovery, adaptive Range degradation, shared-log/consolidated-shell UI contract and tidy installed-runtime layout.

## Build and installer identity

- [ ] Windows workflow runs from the intended `csharp-p5-installer` source revision with SDK `10.0.400`.
- [ ] `verify_global_log_ui_contract.py`, `verify_installer_runtime_layout_contract.py`, media/short-read/HTTP-503 contracts, generated code map, Core contracts, WinUI startup/layout smoke and installer packaging all pass on the same source.
- [ ] Preserve `BUILD_IDENTITY.json`, source/publish/candidate checksum inventories, `INSTALLER_GATE_STATUS.json` and `CANDIDATE_GATE_STATUS.json`.
- [ ] Confirm app and Setup are PE32+ x64 and hashes match every manifest.
- [ ] Confirm `INSTALLER_GATE_STATUS.json` records `runtime_subdirectory=Runtime` and `legacy_flat_runtime_migration_smoke=true`.
- [ ] Confirm all promotion flags remain false before real-machine QA.

## Installer directory layout / migration — BLOCKER

- [ ] Default path is `%LOCALAPPDATA%\Programs\BiliSub Studio`; a custom parent/drive still appends one dedicated `BiliSub Studio` product directory.
- [ ] User-visible product root is organized as `Runtime`, `Data`, `Tools`, `Temp`, `Cache`, `Downloads` plus installer/uninstaller/user-owned files; hundreds of DLL/locale/XBF/PRI runtime entries are **not** scattered directly in the root.
- [ ] `Runtime\BiliSubStudio.exe` is the installed executable and all self-contained DLL/XBF/PRI/locale folders live below `Runtime\`.
- [ ] Start Menu shortcut, optional desktop shortcut, uninstall display icon and post-install launch all point to `Runtime\BiliSubStudio.exe`.
- [ ] Running from `Runtime\` still resolves persistent `Data/Tools/Temp/Cache/Downloads` to the parent `BiliSub Studio` root; it must not create duplicate `Runtime\Data`, `Runtime\Tools`, `Runtime\Cache`, `Runtime\Downloads` roots.
- [ ] Install the replacement candidate directly over one of the old flat-layout field builds. Old root `BiliSubStudio.exe`, DLLs and locale/resource directories listed by the old `SHA256SUMS.txt` are removed/migrated without requiring a manual uninstall.
- [ ] Existing `Data/Tools/Temp/Cache/Downloads` contents survive that migration unchanged.
- [ ] A deliberately created unknown file in the product root survives migration; cleanup is checksum-owned and must not broadly delete unknown/user files.
- [ ] Uninstall removes `Runtime\` and shortcuts while protected data roots remain by default.

## Clean install / shell / visual gate — BLOCKER

- [ ] Install as standard user without UAC/admin prompt.
- [ ] App starts without separately installing .NET, Python, FFmpeg, yt-dlp or PaddleOCR.
- [ ] Startup failure must show a visible Vietnamese dialog and write `%LOCALAPPDATA%\BiliSub Studio\Logs\startup.log`.
- [ ] Exactly four top-level navigation destinations render: `Tải media`, `OCR phụ đề`, `Chỉnh video`, `Cài đặt`.
- [ ] There is no separate top-level `Hiệu năng`, `Đăng nhập`, `Cập nhật & hỗ trợ`, `Phụ đề` or `Tải video` item.
- [ ] Open `Cài đặt` and verify four internal sections: `Chung`, `Hiệu năng`, `Đăng nhập`, `Cập nhật & hỗ trợ`.
- [ ] Media, OCR, Editor and all Settings sections use the same card/spacing/status-color visual language; no obvious legacy/plain page remains.
- [ ] Open every top-level page and every Settings section at 800×600, 1000×700 and 1500×900; no overlap, clipped action, unusable horizontal overflow or layout cycle.
- [ ] Resize/minimize/restore, switch pages repeatedly, close, reopen and Start-menu launch are stable.

## One shared application log — BLOCKER

- [ ] A shell control named `Nhật ký toàn ứng dụng` is available regardless of the active page.
- [ ] Media no longer renders its own independent log box; switching Media/OCR/Editor/Settings does not switch to another diagnostic history.
- [ ] Normal/info entries render green; recoverable warnings render yellow; actual errors render red.
- [ ] Trigger one harmless visible validation/error case and verify a red error entry appears and the global drawer opens automatically.
- [ ] Error counter increases for error entries and does not increase for normal entries.
- [ ] `Xóa màn hình` clears the visible list/counter only; it does not delete the persistent diagnostic file.
- [ ] `Mở file log` locates the persistent `Data\Logs\application.log` file.
- [ ] Close and reopen the app: the persistent file still contains the previous diagnostic trail even though the live in-memory view starts a new session.
- [ ] Run a Media job and verify its job start/progress milestones/final error or success are recorded in the same shared diagnostic stream.
- [ ] Run an OCR scan or Editor render and verify AppJob failures appear red in that same global stream.
- [ ] No Cookie/SESSDATA value is printed in the log.
- [ ] Bug-report flow includes shared diagnostic entries and still sanitizes secrets/user paths before submission.

## Unified `Tải media` layout / metadata — BLOCKER

- [ ] Media uses the available desktop width: Source full width, then left Content + Quality/format and right Output + action, then compact full-width Progress.
- [ ] At 1500×900 there is no large unused right-side area while controls are unnecessarily pushed far below the fold.
- [ ] At 800×600 the page remains usable through vertical scrolling without horizontal clipping.
- [ ] One URL field owns video, thumbnail and subtitle metadata together.
- [ ] Before `Kiểm tra`, `Tải media` is disabled until both metadata and a writable output directory are available.
- [ ] `Kiểm tra` returns title, quality list, thumbnail availability and subtitle-track list on the same page.
- [ ] Default subtitle preference is official Chinese when available.
- [ ] Change URL A to URL B: quality and subtitle track from A disappear immediately and Start becomes disabled.
- [ ] If an old metadata request finishes after the URL was changed, its result is not applied.
- [ ] No asset checkbox selected => Video + Thumbnail + Subtitle-if-available.
- [ ] One or more Video/Thumbnail/Phụ đề boxes selected => only those selected assets are requested.
- [ ] Missing optional subtitle or thumbnail produces an explicit non-fatal skip/warning rather than failing unrelated selected assets.

## Long-media / Range short-read / transient CDN transport — BLOCKER

- [ ] Retest the **same long Bilibili URL** that reproduced HTTP `503` then yt-dlp `985 bytes read ... more expected` on rejected SHA `7f37dcb5...`.
- [ ] Also retain the earlier ~2-hour reproduction coverage for `379 bytes read ... more expected` from rejected SHA `512608...`.
- [ ] Range requests use the restored 4 MiB segment contract and effective Stable/Fast/Turbo transport budgets 1/4/8.
- [ ] A short body preserves bytes already received and the next request continues from the exact missing byte rather than restarting the segment.
- [ ] Repeated pathological tiny reads may refresh the signed URL/CDN while preserving the partial segment.
- [ ] A transient probe or segment HTTP failure such as `403`, `408`, `429`, `500`, `502`, `503` or `504` is **not** treated as immediate proof that Range is unsupported; refresh the signed stream/CDN and retry first.
- [ ] A healthy large short-read recovery must not trigger unnecessary URL refresh merely because the weak-failure counter is zero.
- [ ] If multi-connection Range still exhausts its bounded recovery, automatically retry that stream at **1 connection** before entering yt-dlp fallback.
- [ ] The global log clearly reports adaptive degradation and, if all Range recovery fails, includes the root transport cause before fallback.
- [ ] Range worker transport remains exact HTTP/1.1.
- [ ] Dedicated Windows regression executable passes all three cases on the same source: `379-byte short-read`, `segment HTTP 503`, and `probe HTTP 503`.
- [ ] yt-dlp fallback remains the final fallback only; it keeps resume state and uses 4 MiB HTTP chunks rather than depending on one giant response body.
- [ ] Progress continues through short-read/transient-CDN recovery; no false terminal 90% state while the actual video stream is dead.
- [ ] Completed video is non-empty, opens/plays, and matches the selected mode/container.
- [ ] If video + subtitle are requested, subtitle output is valid and stored in the same configured output directory.
- [ ] Cancel during active video transfer stops the media parent job and leaves no orphan yt-dlp/FFmpeg.
- [ ] After the same real-machine reproduction cases pass, test a >6-hour Bilibili video before declaring the long-media gate complete.

## GitHub update channel — BLOCKER

- [ ] No production update request uses Google Drive or a Drive file ID.
- [ ] Beta build reads `update/beta.json` from repository `main`; stable build reads `update/stable.json`.
- [ ] While `channel_ready=false`, update check reports the GitHub channel as unpublished and Prepare/Update cannot download anything.
- [ ] Before promotion, test a temporary QA manifest with `channel_ready=true` pointing to an exact GitHub Release asset from `wan1778/BiliSub-Studio`.
- [ ] A non-GitHub Release URL is rejected before download.
- [ ] Wrong `payload_kind`, size or SHA-256 is rejected without replacing runtime.
- [ ] On the installed layout, update target is the current `Runtime\` directory only; parent `Data/Tools/Cache/Downloads` are outside the replacement target.
- [ ] Nested-runtime rollback staging is written under the parent protected `Temp\Update` tree, not inside user data or an arbitrary folder.
- [ ] A valid GitHub Release payload stages, verifies PE32+ x64 and applies transactionally while preserving `Data/Tools/Temp/Cache/Downloads`.
- [ ] Forced apply failure restores the previous runtime and the previous app can relaunch.
- [ ] No GitHub Release or channel manifest is promoted automatically by CI.

## Player / OCR / editor — BLOCKER

- [ ] H.264/HEVC plus fallback preview, play/pause/mute/seek/fullscreen.
- [ ] OCR ROI strict validation; invalid ROI disables Test/Start and is never silently clamped.
- [ ] OCR Auto visibly evaluates the base ladder 1 → 2 → 4 → 8 → 16 before real scan progress begins. If a higher base level is unsafe or not useful, it restores the last PASS topology then tests the descending intermediate levels; manual 1/2/4/8/16 either probes the exact request or reports the safety rejection.
- [ ] CPU/GPU/RAM/VRAM and lane/decoder/timing telemetry update while scanning.
- [ ] Auto topology evaluates actual machine/video capability before raising lanes/batch; UI must not remain `measuring` while CPU/GPU/RAM have fallen idle and the benchmark has hung.
- [ ] Auto reads live physical RAM and NVIDIA VRAM before expansion, preserves the documented reserves, and never allocates an unsafe candidate. If candidate N passes preflight, it must create exactly N Python workers and run repeated N-way FFmpeg + OCR rounds on distinct real frames.
- [ ] Candidate N advances only when the exact N-worker topology stays alive and timed throughput is at least 10% above the previous PASS level; otherwise Auto restores the previous PASS level.
- [ ] If 1/2/4 PASS and 8 fails/OOM/times out, telemetry reports the 8 failure, Core restores exactly 4 workers, then tests 7 → 6 → 5. It commits the first exact, safe and useful fallback; if all fail, it commits 4. Level 16 is not attempted after the failed base level.
- [ ] If 1/2/4/8 all PASS, Auto must evaluate 16. If 16 fails RAM/VRAM preflight, lacks 10% throughput gain, errors/OOM/times out, it restores 8 then tests 15 → 14 → 13 → 12 → 11 → 10 → 9. Only a safe, exact and useful topology may commit.
- [ ] A committed four-lane scan shows four concurrent FFmpeg segment processes and four Python worker processes in Task Manager; telemetry reports `4 FFmpeg lane · 4 worker`.
- [ ] Pause reaches safe schema-4 checkpoint; active lanes become zero; Resume preserves topology and progress.
- [ ] During Running and Pausing, Cancel remains enabled. It shows cleanup in progress and does not publish terminal Cancelled until the owned FFmpeg process group and Python worker pool both report zero and checkpoint deletion is verified.
- [ ] After Pause reaches `paused`, both Continue and Hủy và xóa are available; pressing Hủy và xóa clears partial cues/progress and the next action is explicitly Quét từ đầu.
- [ ] Close during OCR waits for safe checkpoint or refuses unsafe close.
- [ ] Completed cue list/timeline synchronization works and Chinese SRT rejects foreign standalone OCR garbage such as the previously observed `A N...` family.
- [ ] Editor multi-region Blur/Mosaic/Cover validation, export and cancellation work end-to-end.

## Login / Settings / maintenance — BLOCKER

- [ ] `Cài đặt > Đăng nhập`: Cookie/SESSDATA DPAPI save/validate/delete flow.
- [ ] `Cài đặt > Đăng nhập`: native QR start/poll/success/expire/retry without browser UI.
- [ ] `Cài đặt > Hiệu năng`: hardware probe/tools/OCR prepare/benchmark controls remain functional after being embedded.
- [ ] `Cài đặt > Chung`: Dark/Light persists across restart.
- [ ] Default output directory propagates into `Tải media`, OCR and Editor.
- [ ] Storage cleanup, Reset Tools and Remove OCR confirmations/states are correct.
- [ ] `Cài đặt > Cập nhật & hỗ trợ`: update check and bug-report success/error states are visible and safe.

## Process lifecycle / uninstall — BLOCKER

- [ ] No system Python/FFmpeg/yt-dlp dependency is required.
- [ ] App-owned child processes die on Cancel/Finish/Exit/crash; updater is the only intentional breakaway owner.
- [ ] Install over an existing location preserves `Data/Tools/Temp/Cache/Downloads`.
- [ ] Uninstall removes runtime/shortcuts while protected user roots remain by default.

Only after every applicable BLOCKER passes may the exact Setup EXE be marked as a release candidate or promoted.

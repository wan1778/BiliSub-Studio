# Windows field checklist — CSharp P5 installer

Status starts as **BLOCKED**. Record one exact source revision, app EXE SHA-256 and installer SHA-256.

## Build and installer identity

- [ ] Run `.github/workflows/csharp-p5-windows-x64-installer.yml` or matching local scripts with SDK `10.0.400` and verified Inno Setup 7.0.2 x64.
- [ ] Preserve `BUILD_IDENTITY.json`, source/publish/candidate checksum inventories, `INSTALLER_GATE_STATUS.json` and `CANDIDATE_GATE_STATUS.json`.
- [ ] Confirm the app and Setup are PE32+ x64 and the Setup hash matches every manifest.
- [ ] Confirm the primary artifact is exactly `BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64.exe` and all promotion flags remain false before QA.

## Clean install

- [ ] Install as a standard user with no UAC/admin prompt.
- [ ] Confirm default path `%LOCALAPPDATA%\Programs\BiliSub Studio` and Start-menu shortcut; test optional desktop shortcut.
- [ ] Confirm the app launches without separately installing .NET, Python, FFmpeg, yt-dlp or PaddleOCR.
- [ ] Confirm `Data/Tools/Temp/Cache/Downloads` are created beside the installed EXE and are writable.
- [ ] Verify Add/Remove Programs entry, icon, displayed version and uninstaller.

## Upgrade and uninstall preservation

- [ ] Create config/session/tool/OCR/cache/download fixtures, then install the same and a newer build over the existing location.
- [ ] Confirm runtime files update while every protected fixture remains byte-identical.
- [ ] Uninstall and confirm installed program files/shortcuts are removed while protected user roots remain by default.
- [ ] Reinstall and confirm preserved configuration and tools are reused safely.
- [ ] Attempt install while the app and one child helper are running; verify safe close and no orphan child.

## Application field matrix

- [ ] Complete all P4 native shell, login/DPAPI, picker, visual, Dark/Light and 100/125/150/200% DPI checks.
- [ ] Test H.264/HEVC playback, fallback formats, transport controls, fullscreen, OCR cue/timeline sync and ROI mouse/keyboard input.
- [ ] Test Stable 1 / Fast 8 / Turbo 16 downloads, Range resume, video+audio aggregate telemetry, fallback and cancellation.
- [ ] Test CPU/GPU/Hybrid/Auto OCR, CUDA selection, worker replacement, corrupt checkpoint recovery and Chinese-only export.
- [ ] Test subtitles, editor, updater rollback/preservation and bug-report redaction.

Only after every applicable item passes may the exact Setup EXE be marked as the release candidate and copied with its exact source, checksums, manifest and visual evidence to the approved release location.

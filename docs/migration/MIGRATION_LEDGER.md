# BiliSub Studio C# migration ledger

## Frozen reference identity

- Direction: C# + .NET 10 + WinUI 3; XAML + C# owns new UI/features.
- Go + Win32 remains the frozen executable oracle until the C# candidate passes every release gate.
- Exact baseline source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Exact source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- Exact frozen EXE SHA-256: `849dcdeac778287d3004aafd7a661323f342e8bc552a950180f66306c7f7f5f8`.
- RC12/RC13/HF archives are behavior evidence only, never the migration base.
- The supplied archive has no `.git`; identity is the archive SHA-256 plus ZIP comment, not a claim of a clean Git working tree.

## Checkpoint history

### `CSharp-P1-Settings`

Settings/Config source slice completed; Windows build and visual gate were not run.

### `CSharp-P2-SourceComplete`

Status: all planned production ownership slices have C# source/UI implementations; exact Windows compile, runtime parity, visual QA and field gates are still mandatory and have not run in the Linux work runtime.

Implemented source owners:

1. Settings/Config and portable paths.
2. Native media probe/frame preview.
3. Tool lifecycle, yt-dlp resolution, true HTTP Range download with Stable 1 / Fast 8 / Turbo 16 global budgets, strict bodies, durable completed segments, fallback, immediate cancel and FFmpeg remux.
4. Hardware/CUDA probe and benchmark.
5. Private PaddleOCR install/manifest/worker protocol, manual frame OCR, multi-lane scan, Auto Predict→Probe→Commit, NVDEC fallback, Chinese cue validation, topology-preserving schema-4 safe pause/resume and export.
6. Subtitle metadata/fetch/parse/export.
7. Native direct-manipulation Video Editor and non-destructive FFmpeg render.
8. Bilibili QR login, native QR matrix, nav validation, Windows DPAPI session and Netscape cookie handoff.
9. Kill-on-close Job Object, shared job cancellation, safe-close pause handshake, guarded cleanup/reset.
10. Update/report owners. The updater requires `payload_kind=winui3-portable-zip`, strict size/SHA-256 and a breakaway staging runtime, so it refuses legacy Go payloads.

Generated/current mapping:

- `docs/migration/CSHARP_WINUI3_CALL_MAP.md`
- `docs/migration/CSHARP_CODE_MAP.generated.md`

### `CSharp-P3-LinuxCompileVerified`

Status: source checkpoint only; not a release candidate.

- Installed and SHA-512-verified the official .NET SDK `10.0.400`; pinned it exactly in `global.json`.
- Built `BiliSubStudio.Core` Release with zero warnings/errors.
- Compiled the complete WinUI code-behind surface against Windows App SDK `2.4.0` with zero warnings/errors.
- Ran the package-free Core runner: **28/28 PASS**.
- Closed build defects, updater ordering/swap/rollback defects, corrupt-session startup handling and OCR worker-pool hang risks.
- Added native H.264/HEVC `MediaPlayerElement` transport controls/full-window mode and OCR cue↔timeline synchronization that P2 had omitted.
- Attempted the real WinUI build. It reaches the Microsoft XAML compiler, which cannot run on Linux because it loads Windows native APIs (`kernel32.dll`); later packaging also requires Windows-only `mt.exe`/`MakePri.exe`.

P3 therefore proves C# source and code-behind compilation, not XAML/XBF/PRI, PE publish, runtime, visual or field parity. See `VALIDATION_REPORT_CSHARP_P3.md` and `WINDOWS_FIELD_CHECKLIST_CSHARP_P3.md`.

### `CSharp-P4-IntegrationVerified`

Status: integrated source checkpoint only; not a release candidate.

- Re-audited Core process, Range/fallback telemetry, session validation, OCR checkpoint/pool lifecycle and updater PE validation.
- Added four contract groups; the package-free runner now passes **32/32** repeatedly.
- Completed the native Support route, responsive page layouts, ROI interaction ownership, keyboard ROI entry, live validation and accessibility state.
- Added a fail-fast `windows-2025` pipeline that records exact source/publish inventories, validates PE32+ x64, reads back checksums and creates non-promotable candidate/source archives.
- Kept the frozen Go production oracle byte-identical: 96/96 checked files, zero changed or missing.

P4 still requires a Windows XAML/XBF/PRI publish and the full field matrix for one exact executable SHA-256. See `VALIDATION_REPORT_CSHARP_P4.md` and `WINDOWS_FIELD_CHECKLIST_CSHARP_P4.md`.

### `CSharp-P5-InstallerReady`

Status: installer-ready source checkpoint only; not a release candidate.

- Changed the primary delivery from portable-only to one per-user Setup EXE, following the user's updated requirement.
- Added an Inno Setup 7 x64 definition, native application/setup icon, current-user install path, Start-menu/optional desktop shortcuts and uninstall support.
- Preserved the portable data-root contract across install, upgrade and uninstall without requiring administrator rights.
- Extended the Windows pipeline to verify the Inno Setup release, compile a PE32+ x64 Setup, hash it and include installer evidence in the candidate manifest.

P5 requires the exact Setup EXE to pass `WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md`; installer source alone is not release completion.

## Deliberate compatibility decisions

- `Data/config.json` retains the exact twelve-field Go schema and normalization rules.
- `Data/session.bin` remains Windows DPAPI protected.
- Portable roots stay beside the executable: `Data`, `Tools`, `Tools/OCR`, `Temp`, `Cache`, `Downloads`.
- Video download uses completed per-segment files plus an atomic manifest. Cancellation removes only unfinished `.tmp/.part/.assembling` artifacts; completed segment checkpoints and completed output files survive.
- MP4 selection prefers AVC only at the same selected height; resolution is never reduced only to obtain H.264.
- The update channel is intentionally fail-closed until the Drive manifest identifies a C# WinUI portable ZIP. It cannot install a historical Go EXE.

## Validation completed in this environment

- Static migration contract: PASS.
- XML/XAML/project parsing: PASS.
- Generated C# code map freshness: PASS.
- Forbidden production markers (`localhost`, WebView2, `/api`, secondary backend): PASS.
- Core/UI ownership boundary: PASS.
- Baseline Go production directory byte comparison against the frozen source archive: PASS.
- Official .NET SDK `10.0.400` SHA-512 and exact `global.json` pin: PASS.
- Core Release compile and WinUI code-behind compile-contract: PASS, zero warnings/errors.
- Package-free Core contract runner: 32/32 PASS.

This runtime still has no Windows XAML/XBF/PRI toolchain, WinUI compositor, FFmpeg/yt-dlp/Paddle Windows runtime or Windows field desktop. No published WinUI PE or release candidate is claimed.

## Exact remaining release gate

On the Windows field machine, from repository root:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

For the exact installer and publish-output SHA-256, complete `docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md`. At minimum:

1. Launch from a portable writable directory on Windows 10 1809+ and Windows 11.
2. Run Settings config reopen, DPAPI login reopen, native pickers and safe close.
3. Capture/inspect 1600×900 and 1365×768 at 100/125/150/200% DPI in Dark and Light.
4. Test media preview for H.264, HEVC, AV1, VP9 and MKV fallback.
5. Test Range supported/broken CDN, 1/8/16 observed connections, URL refresh, yt-dlp fallback, immediate cancel and resume.
6. Test CPU/GPU/Hybrid OCR, Auto 1→2→4→8→16 stopping rules, NVDEC fallback, pause/reopen/resume and Chinese-only export.
7. Test Subtitle official/AI tracks and SRT/TXT/JSON.
8. Test Editor regions/timing/audio/container/cancel/collision and source preservation.
9. Test update rejection of a Go manifest and a signed WinUI portable ZIP on a staging channel.
10. Only after the full matrix passes: create candidate archive, exact source archive, `SHA256SUMS`, build manifest and visual QA bundle, then promote that exact set to the approved Google Drive folder.

Any Windows compile/runtime/visual defect reopens this checkpoint: root cause → source fix → call-map refresh → targeted regression → full matrix → new publish SHA-256.

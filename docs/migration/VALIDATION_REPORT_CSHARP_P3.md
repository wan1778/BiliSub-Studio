# C# P3 compile-verified source checkpoint

Checkpoint: `CSharp-P3-LinuxCompileVerified`  
Date: 2026-08-21 (Asia/Ho_Chi_Minh)

## Identity

- Frozen Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Frozen source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- Frozen executable SHA-256: `849dcdeac778287d3004aafd7a661323f342e8bc552a950180f66306c7f7f5f8`.
- The supplied archive has no `.git`; archive hash plus ZIP comment remains the baseline identity gate.
- C# informational version: `4.0.0-beta.12-csharp-p3`.
- Exact SDK: official .NET SDK `10.0.400`, pinned with `global.json`; Linux x64 SDK SHA-512 verified as `1033977dd837150e0814cf0c5d5b17ceb63925fda7ba2158b47258a4bd7c048cf82eac3bc1166f3146f53124a3f5fba09db1de1260d2ce96399860303b404b48`.

## Passed in the authoring runtime

- C# migration static contract, XML/XAML/project parsing and XAML event-handler resolution: PASS.
- Generated C# code-map freshness: PASS.
- Frozen Go production-directory containment against the exact source archive: PASS.
- `BiliSubStudio.Core` Release compile: PASS, zero warnings/errors.
- Full WinUI code-behind compile-contract against .NET 10 and Windows App SDK `2.4.0`: PASS, zero warnings/errors.
- Package-free Core contract runner: **28/28 PASS**.
- Contract coverage includes config parity/durability, combined video+audio connection telemetry, strict parallel Range transport/cancel cleanup, media metadata, JSON3/VTT/SRT, editor filters, Chinese OCR normalization, numeric CUDA wheel selection, QR, session normalization/control-character rejection/corrupt-session quarantine, job cancellation, Windows filenames, updater version ordering, transactional swap, rollback, PE32+ x64 validation, bug-report redaction and public API visibility.
- Static ownership includes native `MediaPlayerElement` transport controls/full-window mode on OCR and Editor pages plus OCR cue↔timeline synchronization.

## Source defects closed after P2

1. Fixed Core compile visibility and local-variable errors found by the first real C# build.
2. Fixed prerelease ordering (`p10 > p2`) and build-metadata precedence.
3. Replaced in-place updater copy with durable transactional swap/rollback, strong PE32+ x64 validation and restored-runtime relaunch on update failure.
4. Quarantined invalid DPAPI session data instead of blocking app startup.
5. Prevented OCR worker-pool waiters from hanging after a worker dies and kept stderr drained for the worker lifetime.
6. Added native H.264/HEVC playback controls, full-window mode, FFmpeg frame fallback and cue↔timeline navigation.
7. Fixed CUDA 12.8+ wheel selection to compare the driver version numerically and retain the Go oracle's `>= 12.6 -> cu126` rule.
8. Fixed video+audio telemetry to report combined progress, throughput and active connections instead of the maximum of one stream.
9. Connected the persisted `check_updates` setting to a bounded, non-blocking startup check and the native Support page.
10. Added compact navigation, work-area-bounded startup sizing and stacked/scrollable OCR/Editor layouts for narrow or high-DPI windows.

## Windows-only gate still blocked here

The real WinUI XAML compiler starts but cannot execute in Linux. Its Windows-hosted compiler requires `kernel32.dll`; bypassing XAML then reaches Windows-only `mt.exe`/`MakePri.exe`. Therefore this checkpoint does **not** claim XBF/PRI generation, a published PE, compositor QA, live FFmpeg/yt-dlp/PaddleOCR, DPAPI, Job Object behavior or Windows field parity.

Run from repository root on the Windows field machine:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

Later checkpoints introduced a Windows runner and installer pipeline. They do not retroactively validate or promote P3; use the matching P5 source, workflow, report and checklist.

P3 remains archived and non-promotable. Use the P4 report, workflow and checklist as one matching set for any current Windows build.

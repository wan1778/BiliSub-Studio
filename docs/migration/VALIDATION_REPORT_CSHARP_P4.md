# C# P4 integration-verified source checkpoint

Checkpoint: `CSharp-P4-IntegrationVerified`  
Date: 2026-08-21 (Asia/Ho_Chi_Minh)

## Identity

- Frozen Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Frozen source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- Frozen executable SHA-256: `849dcdeac778287d3004aafd7a661323f342e8bc552a950180f66306c7f7f5f8`.
- Starting checkpoint: `CSharp-P3-LinuxCompileVerified`.
- C# informational version: `4.0.0-beta.12-csharp-p4`.
- Exact SDK: official .NET SDK `10.0.400`, pinned with `global.json`.

## Passed in the authoring runtime

- C# migration static contract, XML/XAML/project parsing and XAML event-handler resolution: PASS.
- Generated C# code-map freshness: PASS.
- Frozen Go production containment: 96/96 files byte-identical; zero changed or missing.
- `BiliSubStudio.Core` Release compile: PASS, zero warnings/errors.
- Full WinUI code-behind compile-contract against .NET 10 and Windows App SDK `2.4.0`: PASS, zero warnings/errors.
- Package-free Core contract runner: **32/32 PASS**; repeated runs remained clean.
- Windows workflow/pipeline markers and non-promotion guard: PASS.

## P4 defects closed

1. Guaranteed child-process tree termination and reap when progress/parser callbacks fail.
2. Completed Range resume now reports terminal progress instead of remaining at zero.
3. Video/audio fallback no longer overwrites aggregate connection telemetry.
4. Bare-token cookies reject DEL and all control characters.
5. OCR checkpoint loading rejects corrupt, null or topology-inconsistent lane data safely.
6. Retired OCR pools cannot overwrite a replacement pool's state; in-flight worker shutdown is synchronized.
7. Updater PE validation now rejects missing sections, undersized optional headers and non-executable images.
8. Main navigation now exposes Support and preserves native selection/focus routing.
9. OCR/Editor ROI selection no longer blocks media transport controls; live ROI/time validation owns operation enablement.
10. Editor supports keyboard ROI entry, and native pages now expose responsive, loading/error and accessibility state for narrow/high-DPI layouts.
11. Added a fail-fast `windows-2025` build/publish/package workflow with exact source inventory, PE32+ x64 inspection, checksum readback and non-promotable manifests.

## Windows-only gate

No Windows candidate is claimed by this checkpoint. Linux cannot execute the Windows App SDK XAML compiler or the Windows `mt.exe`/`MakePri.exe` packaging chain, and it cannot validate the compositor, DPAPI, Job Object, codecs, native pickers or real PaddleOCR/CUDA behavior.

Run one exact revision through:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
powershell -ExecutionPolicy Bypass -File csharp/scripts/package_windows_candidate.ps1
```

P4 remains archived and non-promotable. P5 later replaced portable-only delivery with the current one-file installer workflow; use the complete P5 source/report/checklist set.

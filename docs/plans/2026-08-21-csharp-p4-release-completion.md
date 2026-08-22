# C# P4 release-completion plan

## Frozen source identity

- Go source commit/oracle: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`
- Baseline archive: `BiliSubStudio_source_v4.0.0-beta.12-NativeUI.zip`
- Baseline archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`
- Frozen reference EXE SHA-256: `849dcdeac778287d3004aafd7a661323f342e8bc552a950180f66306c7f7f5f8`
- Starting C# checkpoint: `4.0.0-beta.12-csharp-p3`

The frozen Go production tree remains the behavior oracle. P4 may change the C# migration lane, its tests, validation scripts, release pipeline, and migration documentation only.

## Workstreams

### 1. Windows build and release pipeline

- Pin .NET SDK `10.0.400` and the package versions already declared by the source.
- Restore, compile, run contract tests, and publish `win-x64` unpackaged/self-contained output.
- Reject a candidate unless `BiliSubStudio.App.exe` is PE32+ for AMD64.
- Reject a candidate containing protected portable-data roots.
- Emit SHA-256 checksums and a machine-readable build identity manifest.
- Upload a CI artifact only. Do not promote or copy it to the release/Drive location.

### 2. WinUI 3 UI/UX v2.1

- Preserve native navigation and visible workflow state.
- Verify narrow-window/DPI behavior for preview, timeline, ROI, inspector, settings, account, update, and support surfaces.
- Check keyboard navigation, accessible names, loading/empty/error states, and destructive-action confirmation.
- Keep Windows visual inspection and DPI screenshots as a Windows-required gate.

### 3. Core/runtime reliability

- Re-audit cancellation and process/resource cleanup.
- Re-audit concurrent range-download accounting and retry/resume invariants.
- Re-audit OCR worker lifecycle, numeric CUDA selection, DPAPI session quarantine, and updater rollback/PE validation.
- Add a regression before or with any behavior change.

### 4. Integration and acceptance

- Regenerate `docs/migration/CSHARP_CODE_MAP.generated.md` after production-code changes.
- Run all C# static, compile-contract, and contract-test gates available on Linux.
- Verify the frozen Go production files remain byte-identical.
- Run `csharp/scripts/verify.ps1` on Windows for the exact candidate SHA-256.
- Complete the P3/P4 Windows field checklist on the target Windows x64 machine.

## Release rule

Source-complete, Linux-verified, or CI-artifact status is not a release. Promotion is allowed only after the exact Windows candidate passes publish validation, PE inspection, native visual QA, real tool execution, and the complete field matrix. Any fix creates a new candidate hash and restarts the affected and full acceptance gates.

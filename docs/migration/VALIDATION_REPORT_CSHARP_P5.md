# C# P5 installer-ready source checkpoint

Checkpoint: `CSharp-P5-InstallerReady`  
Date: 2026-08-21 (Asia/Ho_Chi_Minh)

## Identity and intent

- Frozen Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Frozen source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- Starting integration checkpoint: `CSharp-P4-IntegrationVerified`.
- C# informational version: `4.0.0-beta.12-csharp-p5`.
- Primary final-user artifact target: `BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64.exe`.

P5 replaces the older “portable-only/no-installer” delivery decision. The app remains unpackaged and self-contained internally, but the normal user receives one installer EXE rather than a source or portable ZIP.

## Installer contract

- Inno Setup 7 x64 generates a PE32+ x64 single-file installer.
- Current-user installation below `%LOCALAPPDATA%\Programs\BiliSub Studio`; no administrator elevation is requested.
- Start-menu shortcut is automatic; desktop shortcut is optional.
- `Data`, `Tools`, `Temp`, `Cache` and `Downloads` remain beside the installed EXE and are preserved across upgrades and uninstall by default.
- The verified publish includes the .NET/Windows App SDK runtime and OCR worker; users do not install .NET, Python, FFmpeg or yt-dlp manually.
- Installer, app EXE, source inventory and portable fallback each receive SHA-256 evidence.
- All manifests remain `release_candidate=false`, `promotion_allowed=false` and `field_qa_complete=false` until the exact installer passes Windows QA.

## Authoring gates

- P4 Core/runtime and UI integration gates remain green: Core Release 0 warnings/errors, full code-behind compile-contract PASS and 32/32 contract tests PASS.
- Installer script, application icon, packaging integration, workflow YAML and non-promotion markers: static PASS.
- Frozen Go production containment remains 96/96 byte-identical.

## Windows-only gate

The installer cannot be compiled truthfully on Linux. Run `.github/workflows/csharp-p5-windows-x64-installer.yml` or execute `verify.ps1` followed by `package_windows_candidate.ps1` on Windows. The pipeline verifies the official Inno Setup release before compiling the installer.

Complete `docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md` for the exact installer SHA-256. A source ZIP or an untested Setup EXE is not the final release.

## Live Windows CI findings

- Run 1 exposed that `gh release verify-asset` defaults to the latest release. The workflow now supplies the pinned `is-7_0_2` tag explicitly while retaining release-attestation and Authenticode checks.
- Run 2 exposed that the generated C# code map could include local `bin`/`obj` compiler output. The generator now excludes those directories so a clean Windows checkout and the Linux authoring tree produce the same map.
- These pipeline fixes do not promote a candidate. The Windows compile, package, installer and field-QA gates remain mandatory for the exact resulting SHA-256.

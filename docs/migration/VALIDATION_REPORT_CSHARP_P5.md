# C# P5 installer-ready source checkpoint

Checkpoint: `CSharp-P5-InstallerReady`  
Date: 2026-08-22 (Asia/Ho_Chi_Minh)

## Identity and intent

- Frozen Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Frozen source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- C# informational version: `4.0.0-beta.12-csharp-p5`.
- Primary user artifact target: `BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64.exe`.
- Source of truth: GitHub branch `csharp-p5-installer` until field QA completes.

P5 remains non-promotable. No merge, GitHub Release or update-channel publication is allowed until the exact current installer passes Windows field QA.

## Current migration state

- C# + .NET 10 + WinUI 3 is the production migration path.
- Go is reference-only and is not compiled or invoked by the C# app.
- Production Bilibili download UI is one `Tải media` workflow: one URL, shared metadata, video + subtitle in one parent job.
- The updater no longer uses Google Drive. Discovery is through repository manifests on GitHub and runtime payloads are restricted to this repository's GitHub Releases.

## GitHub update channel

- Stable manifest source after merge: `https://raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/stable.json`.
- Beta manifest source after merge: `https://raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/beta.json`.
- Allowed payload location: `https://github.com/wan1778/BiliSub-Studio/releases/download/...` only.
- Both manifests currently remain `channel_ready=false`; therefore no update can be prepared or downloaded during field QA.
- Existing size/SHA-256 verification, ZIP traversal protection, PE32+ x64 validation, protected-root preservation, breakaway updater, transactional swap and rollback remain mandatory.
- Full publication procedure: `docs/migration/GITHUB_UPDATE_CHANNEL.md`.

## Rejected real-machine candidates

- `b7d0f438280c6461f6d82f9ec1c0ea9de48a4df3c3afdb764a3097823dd81883`: startup/XAML/runtime-tree failure.
- `d2c3db9c00dd613696fc7077db30dd2a2f902d6bde288af79dcc008c7e03e361`: Settings `Layout cycle detected`.
- `662356fe304b8a2d45339c8aa8cc998eb813d6a720adb178d8ddb173f36efaf5`: install/startup/navigation smoke passed, but migration exposed separate `Phụ đề` and `Tải video` tabs instead of the required unified media workflow.

## Last verified pre-GitHub-update candidate

- Source head at that point: `611d83e7905b1d17cbefb1aae99b792cf9e41ae5`.
- Windows workflow run 16: `32533485392`.
- Installer SHA-256: `2a37143e1b5777741e50b84e6df52f3b524ce1a2f81cd203532ff42ecc4722b5`.
- Run 16 passed compile/contracts, publish/layout smoke, custom-directory install, installed-EXE startup, uninstall and artifact upload.

This installer is superseded for further QA because the updater source has subsequently moved from Drive to GitHub. A new exact installer SHA from the latest GitHub head is required before field testing continues.

## Gate

Complete `docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md` for the exact latest installer SHA-256. Only after every applicable blocker passes may a GitHub Release be created and one of the repository update manifests be changed to `channel_ready=true`.

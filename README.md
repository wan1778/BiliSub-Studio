# BiliSub Studio

Native Windows desktop application for Bilibili media download, subtitle OCR and video utilities.

## Current C# migration

The active migration work is on branch `csharp-p5-installer` and remains non-promotable until Windows field QA is complete.

- UI/runtime: C# + .NET 10 + WinUI 3.
- Bilibili download: one unified `Tải media` workflow for video + subtitle.
- Application updates: GitHub-only channel (`update/stable.json`, `update/beta.json`) with payloads restricted to this repository's GitHub Releases.
- Go sources remain reference-only and are not compiled or invoked by the C# app.

See `docs/migration/CSHARP_WINUI3_CALL_MAP.md`, `docs/migration/GITHUB_UPDATE_CHANNEL.md` and `docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md` for the current gates.

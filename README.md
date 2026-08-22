# BiliSub Studio

BiliSub Studio is a native Windows desktop application for Bilibili media download, subtitle OCR and video utilities.

## Production stack

- **C# / .NET 10 / WinUI 3** — application, UI and core services.
- **Python** — only the private PaddleOCR worker embedded at `internal/ocr/worker.py` and managed by the C# OCR subsystem.
- **FFmpeg / ffprobe / yt-dlp** — app-managed helper tools downloaded into the app-owned `Tools` directory.

There is no Go production source or Go build path in the current tree. Legacy Go implementation history remains available through Git history only.

## Repository layout

```text
csharp/                     C# solution, app, core, tests, installer and CI scripts
internal/ocr/worker.py      embedded Python OCR worker used by the C# app
docs/                       architecture, engineering and migration documentation
design-system/              UI design reference
update/                     stable/beta update manifests
.github/workflows/          Windows build/release workflow
```

## Build and verify

The exact .NET SDK is pinned by `global.json`.

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
powershell -ExecutionPolicy Bypass -File csharp/scripts/package_windows_candidate.ps1
```

The Windows workflow compiles and tests the C# solution, verifies WinUI startup/layout, long-media CDN failover and Range resume contracts, then builds the per-user x64 installer.

## Public beta

The active public beta channel is `4.0.0-beta.12-csharp-p5`. The installed root exposes `BiliSubStudio.exe`; the full self-contained runtime lives under `Runtime\`, and user/app data roots remain separate.

See `ARCHITECTURE.md`, `BUILD.md`, `docs/migration/CSHARP_WINUI3_CALL_MAP.md` and `docs/migration/PUBLIC_BETA_RELEASE_POLICY.md` for current engineering and release contracts.

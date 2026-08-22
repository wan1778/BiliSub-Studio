# BiliSub Studio — C# + WinUI 3 production tree

This is the active production implementation of BiliSub Studio.

## Stack

- C# / .NET 10
- WinUI 3 / Windows App SDK 2.4.0
- unpackaged self-contained Windows x64 runtime
- app-managed FFmpeg / ffprobe / yt-dlp helpers
- private Python/PaddleOCR worker embedded from `internal/ocr/worker.py`

No Go toolchain or Go production source is required.

## Solution ownership

- `BiliSubStudio.App` — WinUI application, pages, pickers and visual state.
- `BiliSubStudio.Core` — application/core logic and services.
- `BiliSubStudio.Launcher` — NativeAOT root launcher used by the installed layout.
- `tests/*` — Core, Range, CDN discovery and CDN failover regression executables.

## Verify on Windows

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

The verification gate checks the exact .NET SDK, source/code-map contracts, C# build, Core contracts, Range regressions, self-contained WinUI publish, XBF/PRI resources, PE32+ x64 identity, OCR worker checksum and real startup/layout smoke.

Targeted CDN/media gates also run in the Windows workflow before the full build:

- primary + backup CDN discovery for the selected DASH format;
- primary short-read to backup CDN continuation at the exact missing-byte Range offset;
- long-media/default-all/separate-asset transport contracts.

## Package

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/package_windows_candidate.ps1
```

The installer is per-user, x64, requires no administrator rights and uses this installed layout:

```text
BiliSub Studio\
├─ BiliSubStudio.exe
├─ Runtime\
├─ Data\
├─ Tools\
├─ Temp\
├─ Cache\
├─ Downloads\
└─ Uninstall\
```

The root launcher is a real EXE. The full self-contained WinUI/.NET runtime stays under `Runtime\` to keep the user-visible root clean.

## Development run

```powershell
dotnet run --project csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Debug -p:Platform=x64
```

## Release

Pull requests verify/package without publishing. Main-branch publication creates a GitHub public beta only for a new version and writes the corresponding exact portable payload URL/SHA-256/size into `update/beta.json`.

See the repository root `ARCHITECTURE.md`, `BUILD.md` and `docs/migration/PUBLIC_BETA_RELEASE_POLICY.md` for current contracts.

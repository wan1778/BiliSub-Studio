# Build / Release

## Requirements

- Windows 10 version 1809 or later for the runnable app.
- Exact .NET SDK `10.0.400` pinned by `global.json`.
- Windows App SDK `2.4.0` pinned in `csharp/Directory.Packages.props`.
- Inno Setup 7 for installer packaging. CI verifies the reviewed official compiler before use.

No Go toolchain is required. The current application is C# + WinUI 3.

## Verify

Run from the repository root on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

This gate verifies:

- static migration/source ownership contracts;
- generated C# code map freshness;
- C# restore/build with the pinned SDK;
- Core contract tests;
- long-media Range short-read, HTTP 503 and CDN failover regressions;
- self-contained WinUI publish;
- PE32+ x64 identity;
- real WinUI startup/layout smoke;
- embedded OCR worker checksum;
- full published runtime checksum readback.

## Package installer

Only after `verify.ps1` passes:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/package_windows_candidate.ps1
```

Packaging builds and verifies the per-user Windows x64 installer. The installed layout is:

```text
BiliSub Studio\
├─ BiliSubStudio.exe      root launcher
├─ Runtime\               self-contained C# / WinUI runtime
├─ Data\
├─ Tools\
├─ Temp\
├─ Cache\
├─ Downloads\
└─ Uninstall\
```

`Data`, `Tools`, `Temp`, `Cache` and `Downloads` are protected across upgrade/uninstall according to the installer contract.

## CI / public beta

`.github/workflows/csharp-p5-windows-x64-installer.yml` runs the same Windows gates. Pull requests build and verify artifacts. Main-branch publication creates a GitHub pre-release/public beta and updates `update/beta.json` only for a version that has not already been published.

Stable `4.0.0` remains a separate quality gate from the public beta line.

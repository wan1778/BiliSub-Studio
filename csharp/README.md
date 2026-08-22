# BiliSub Studio — C# + WinUI 3 migration lane

This tree migrates BiliSub Studio incrementally from the frozen Go + Win32 reference into C#/.NET 10 + WinUI 3. It is not a replacement release yet.

Current checkpoint: **CSharp-P5-InstallerReady**.

Implemented source owners:

- Portable Settings/Config with the exact twelve-field Go JSON schema.
- Native WinUI shell, file/folder pickers, DPAPI session storage and Bilibili QR login.
- App-owned FFmpeg/ffprobe/yt-dlp lifecycle, media probe and frame preview.
- Native H.264/HEVC playback with transport controls/full-window mode, FFmpeg frame fallback, and OCR cue↔timeline synchronization.
- Strict HTTP Range video transport with Stable 1 / Fast 8 / Turbo 16 budgets, durable completed segments, URL refresh, fallback and immediate cancellation.
- Official and AI subtitle tracks with JSON3/VTT/SRT normalization and SRT/TXT/JSON export.
- Native hardware/CUDA probe and benchmark.
- Private uv/Python/PaddleOCR runtime, CPU/GPU/Hybrid pools, Auto Predict→Probe→Commit, 1/2/4/8/16 lanes, NVDEC fallback, Chinese-only cues and schema-4 pause/resume.
- Direct-manipulation editor regions and non-destructive FFmpeg render.
- Kill-on-close job containment, safe shutdown, maintenance, sanitized reports and a transactional/rollback-safe WinUI portable updater.

Using the exact official .NET SDK `10.0.400` pinned by `global.json`, Core and the full WinUI code-behind compile-contract pass with zero warnings/errors, and all 32 package-free Core contracts pass on Linux. The real WinUI XAML/XBF/PRI toolchain and one-file installer compiler are Windows-only; `verify.ps1` remains the mandatory Windows compile/test/publish gate.

## Toolchain

- Windows 10 version 1809 or later.
- .NET 10 SDK.
- Visual Studio 2026 with the WinUI application development workload, or the WinUI .NET CLI templates.
- Windows App SDK `2.4.0` stable, pinned centrally in `Directory.Packages.props`.

The app is unpackaged, `win-x64`, and Windows App SDK self-contained. Do not enable single-file publishing: WinUI deployment contains required framework/runtime files.

## Verify on Windows

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

The script fails immediately on any non-zero external command, runs the static migration contract, restores/builds the solution, runs the package-free Core contract suite, and publishes the WinUI app. It then verifies `BiliSubStudio.exe` as PE32+ x64, reads back every published-file checksum, and records the exact source-tree digest and executable hash in `BUILD_IDENTITY.json`.

To package only after that gate passes:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/package_windows_candidate.ps1
```

Packaging creates a read-back-verified portable build ZIP, reconstructs an exact source ZIP, and compiles `BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64.exe` as the primary user artifact. The installer is per-user, requests no administrator rights, installs below `%LOCALAPPDATA%\Programs`, creates Start-menu/optional desktop shortcuts, and preserves `Data/Tools/Temp/Cache/Downloads` across upgrade and uninstall. Every generated manifest remains non-promotable until field QA.

The scripts run on `windows-2025` through `.github/workflows/csharp-p5-windows-x64-installer.yml`. The workflow verifies the official Inno Setup compiler before generating the single installer EXE, then uploads a short-lived CI artifact only. It has no GitHub Release, deployment, Drive upload, or update-channel permission/step.

On a non-Windows authoring host, the narrower code-behind gate is reproducible with:

```bash
python3 csharp/scripts/compile_app_codebehind_contract.py --dotnet /path/to/dotnet
```

That command checks C# APIs and generated XAML fields only; it cannot produce XBF/PRI or a runnable WinUI binary.

## Run during development

```powershell
dotnet run --project csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Debug -p:Platform=x64
```

The published app must still pass native visual QA and Windows runtime field testing before it can replace the Go candidate.

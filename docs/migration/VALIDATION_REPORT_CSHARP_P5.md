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
- Current-user installation defaults to `%LOCALAPPDATA%\Programs\BiliSub Studio`; the user may select another writable drive/folder and no administrator elevation is requested.
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
- Run 4 completed the real WinUI build with 0 warnings/errors and passed 32/32 contracts, then exposed an x64 publish-path mismatch. Verification, packaging and installer scripts now use the actual `bin/x64/Release/.../publish` path produced by `Platform=x64`.
- These pipeline fixes do not promote a candidate. The Windows compile, package, installer and field-QA gates remain mandatory for the exact resulting SHA-256.
- First real-machine field attempt on 2026-08-21 rejected installer SHA-256 `b7d0f438280c6461f6d82f9ec1c0ea9de48a4df3c3afdb764a3097823dd81883`: the installed app exited silently during startup and Setup allowed the user to expose the full self-contained runtime tree in an arbitrary folder.
- The replacement source merges `XamlControlsResources`, records every startup phase under `%LOCALAPPDATA%\BiliSub Studio\Logs`, shows fatal startup errors, runs the exact published EXE through a CI launch sentinel, provides a real Destination Location page with a dedicated product subdirectory and limits satellite resources to Vietnamese/English.
- Run 6 proved the new gate catches the same class of defect before packaging: compile remained 0 warnings/errors and 32/32 contracts passed, but the exact published EXE reported `XamlParseException` from `MainWindow.InitializeComponent`. Packaging was correctly skipped. The handwritten `PathIcon.Data` geometry surface was replaced with stable Segoe MDL2 `FontIcon` glyphs and exception diagnostics now record HRESULT plus runtime-specific properties for the next launch gate.
- Run 7 confirmed HRESULT `0x802B000A` still originated inside the compiled `MainWindow` XAML, so icon geometry was not the sole cause. The shell XAML is now reduced to standard `NavigationView` + `Frame` + status bar controls while preserving all eight existing feature pages and their C# owners. Decorative header/icon/accessibility markup will only be restored incrementally after the exact published shell passes the runtime gate.
- Run 8 reproduced the same `LoadComponent` failure even with the reduced shell. Artifact inspection then confirmed the actual publish defect: the app directory had no application `.xbf` files and no `BiliSubStudio.pri`/`resources.pri`, so the executable could not resolve `ms-appx` XAML resources. The app project now copies compiled XBF files plus its PRI from `OutputPath` to `PublishDir`, and verification fails before launch unless both resource classes exist.
- Run 9 confirmed the publish workaround emitted at least two application XBF files and one PRI. Verification then stopped on a PowerShell collection-shape bug when exactly one PRI matched; the result is now wrapped as an array so the gate can continue to the exact published-EXE launch test.
- Run 10 passed the exact published-EXE startup gate: XAML initialized, the main window activated, all startup services completed and the sentinel was written. The packaging gate now additionally performs a silent current-user install, launches the installed EXE through the same sentinel, checks the installed EXE hash, silently uninstalls it and confirms the five protected data roots survive before an installer artifact may be uploaded.
- Run 11 passed the full installer integration gate, including silent current-user install, installed-EXE hash equality, installed WinUI startup, silent uninstall and protected-root preservation. Real-machine review then exposed a confusing first-install Start-menu group page whose shell browser only lists program groups, not disk drives. Setup now disables that page unconditionally, fixes the Start-menu group to `BiliSub Studio` and keeps only the optional desktop-shortcut task visible.
- Run 12 passed after removing the Start-menu group page. Real-machine UX review then confirmed that disabling the actual Destination Location page also removed the user's legitimate choice of drive/folder. Setup now shows that page unconditionally, preserves the previous app directory on upgrades and uses `AppendDefaultDirName=yes` so selecting a drive or parent folder creates a dedicated `BiliSub Studio` child instead of spilling runtime files into the parent. CI installs the exact candidate into a custom path containing spaces before launch/uninstall validation.

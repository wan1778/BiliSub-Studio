# Shared native video/folder picker repair

## Status

**Supersedes the beta.11 PowerShell/WinForms approach.**

Beta.11 field testing showed that changing the PowerShell process flags was not sufficient: OCR/Editor displayed `Đang mở...` but no selectable dialog appeared. That Windows result invalidated the previous `dialogHost()` hypothesis as a release solution.

## Verified ownership / call paths

Video picker:

```text
OCR #ocrPick ---------+
                      +-> POST /api/pick-video
Editor #editorPick ---+      -> api.Server.pickVideoHandler
                             -> api.pickVideoNative
                             -> Windows GetOpenFileNameW
```

Folder picker:

```text
Subtitle / Video / OCR / Editor output controls
  -> POST /api/pick-folder
  -> api.Server.pickFolderHandler
  -> api.pickFolderNative
  -> runtime.LockOSThread
  -> CoInitializeEx(COINIT_APARTMENTTHREADED)
  -> SHBrowseForFolderW
  -> SHGetPathFromIDListW
  -> CoTaskMemFree + CoUninitialize
```

These pickers are platform integration owned by `internal/api`. They are not OCR scanner code, Video Editor render code, or downloader code.

## Root-cause / field evidence

### beta.10

Interactive pickers were launched through the same hidden child-process policy used for non-interactive helpers. That could hide a user-facing WinForms modal while the HTTP request waited for it.

### beta.11

The picker-specific PowerShell policy removed `HideWindow` and kept `CREATE_NO_WINDOW`, but Windows field testing still produced no visible dialog. Therefore the app must not depend on PowerShell + WinForms for interactive file/folder selection.

## beta.12 RC implementation

- `pickVideoNative` calls the Win32 common file dialog directly from `BiliSubStudio.exe`.
- `pickFolderNative` calls the Windows Shell folder browser directly from `BiliSubStudio.exe`.
- No PowerShell/WinForms helper process is spawned for interactive selection.
- Folder browsing pins the goroutine to one OS thread, initializes apartment-threaded COM for the dialog lifetime, balances successful initialization with `CoUninitialize`, and frees the returned PIDL.
- `hidden()` remains reserved for non-interactive helper processes only.
- OCR and Editor continue sharing exactly one `/api/pick-video` route.

## Do not change while validating this fix

- OCR engine/manager/deterministic scanner
- Video Editor FFmpeg filter/export backend
- Bilibili downloader/resolver/retry/resume/CDN behavior
- updater transfer/hash/self-swap logic
- cookie/QR provider logic

## Automated regression gates

- source/embedded UI byte-identical
- every UI-referenced `/api/*` route registered
- every static product button exercised by Chromium E2E
- shared picker source contains native Windows APIs and no PowerShell/WinForms host dependency
- Go tests/vet/race pass
- Windows amd64 package cross-compile passes
- PE validator passes

## WINDOWS-REQUIRED acceptance

The Linux build environment cannot truthfully prove native Windows dialog visibility/foreground behavior. Before Drive promotion, the **exact RC binary** must be run on Windows and verify:

1. OCR video picker visible, Cancel clean, ASCII and Unicode path selection.
2. Editor video picker same checks.
3. All output folder pickers visible, Cancel clean, Unicode folder selection.
4. All `Mở` controls launch Explorer at the intended folder.

Compile success is not runtime PASS for these rows.

# Plan: persistent desktop lifecycle + startup status bootstrap

## User-visible bugs

1. After BiliSub Studio is left unused (or the browser is suspended), the backend exits and the UI later reports: `Tiến trình nền đã dừng. Hãy mở lại BiliSubStudio.exe.`
2. Sidebar `Ổ ứng dụng` and `Phiên bản` remain `...` until the Settings page is opened.

## Verified call paths

### Backend lifecycle
`cmd/bilisub.main -> api.New -> HTTP Serve -> [old: Server.StartIdleWatch] -> requestExit`

The old five-minute watchdog depended on browser heartbeat timers. Browser/OS suspension can pause those timers longer than the lease, so a healthy desktop app can terminate while the user still expects it to be open.

### Sidebar identity
`refreshAppStatus -> GET /api/status -> statusHandler -> {version, drive, root,...} -> #version/#driveSide/#updateCurrent`

The status refresh was called from `switchPage("settings")` but not during initial app bootstrap.

## Changes

- Remove production idle-watch startup and its unused `lastSeen` state.
- Keep `/api/ping` only as a lightweight health endpoint; the UI no longer needs a periodic heartbeat.
- Rename cryptic global `p()` to `refreshAppStatus()` for ownership clarity.
- Call `refreshAppStatus()` after OCR/Editor UI initialization completes, so sidebar/app identity is populated on first load without opening Settings.
- Refresh status again on `pageshow` only; this updates visible identity after browser page restoration but is not a lifecycle dependency.
- Update architecture/call-map documentation and regression tests.

## Do not change

- OCR scanner or RapidOCR manager
- video editor renderer/FFmpeg filters
- video downloader/resolver/retry/resume
- updater download/verify/self-swap logic
- cookie validation/login behavior
- native file/folder picker policy

# BiliSub Studio — Architecture

> **Timeline V2 retired 2026-09-28.** The Timeline V2 multi-track editor
> (`csharp/src/BiliSubStudio.Core/Timeline`, `TimelineEditorPage`,
> Timeline contract tests and `verify_timeline_*` gates) was removed in full
> because it is no longer used — the classic Editor (`Chỉnh video`) is the
> single editing surface. Top-level navigation is now: Media, OCR, classic
> Editor, Settings.
>
> The previous revision of this document (graph-derived ownership map pinned
> to commit `e3fd178`, `main` 2026-09-08: 8.263 nodes · 21.130 edges ·
> 497 communities) described the Timeline-centric tree and is preserved in Git
> history only. Where any doc and the tree disagree, the tree wins.
>
> The `docs/editor-timeline/` folder was removed together with the subsystem.

## Current ownership

BiliSub Studio is a **C# / .NET 10 / WinUI 3** Windows desktop app:
Bilibili media download, local OCR/ASR/TTS and the classic clip editor,
with verified self-update from GitHub Releases.

- **Launcher** (`csharp/src/BiliSubStudio.Launcher`) — thin root launcher,
  no feature logic.
- **App** (`csharp/src/BiliSubStudio.App`) — WinUI shell, pages
  (`VideoPage`, `OcrPage`, `EditorPage`, `SettingsPage` …), pickers,
  startup. `BiliSubApplication` is constructed once by `MainWindow`.
- **Core** (`csharp/src/BiliSubStudio.Core`) — all services. The single
  composition root is `BiliSubApplication`
  (`Core/Application/BiliSubApplication.cs`).
- **tests** — contract/regression executables.

Single-gate invariants (unchanged):

- **Processes are spawned in exactly one place** (`ProcessRunner`).
- **Jobs in exactly one place** (`JobManager`).
- **FFmpeg is gated in exactly one place** (`ToolManager`).
- Every feature funnels through `JobManager` + the shared application log
  with secret redaction.

## Entry points

- `csharp/BiliSubStudio.sln` for orientation; start from
  `BiliSubStudio.Core` hub types.
- Grep callers before editing — required by `AGENTS.md`.

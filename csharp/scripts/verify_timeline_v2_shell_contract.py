#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
MAINWINDOW = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
MAIN_XAML = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


# G2 shell files must exist and stay in their own Timeline namespace.
for name in (
    "TimelineFeatureFlags.cs",
    "TimelineProjectV2.cs",
    "TimelineProjectStoreV2.cs",
    "TimelineEditorSession.cs",
    "TimelineRenderPlan.cs",
    "TimelinePreviewController.cs",
    "TimelineExportService.cs",
):
    require((CORE / name).exists(), f"G2 timeline shell file missing: {name}")

flag = read(CORE / "TimelineFeatureFlags.cs")
require("BILISUB_TIMELINE_V2" in flag and "public static bool IsEnabled" in flag
        and "public static bool Parse(string? value)" in flag,
        "G2 flag must be env-gated with a testable parser")
require("IsEnabled => Parse(" in flag, "G2 flag must default through the parser")
require("public static bool IsSuppressed" in flag
        and "public static bool IsEntryVisible" in flag
        and '"0"' in flag,
        "G14 kill-switch must hide the Timeline entry only on explicit opt-out (DEC-024)")

store = read(CORE / "TimelineProjectStoreV2.cs")
for token in (
    "TimelineV2",
    "EnsureNotProductionRoot",
    "Data",
    "Projects",
    "Path.GetFullPath(Path.Combine(_root, id + \".json\"))",
    "File.Move(temporary, path, overwrite: true);",
):
    require(token in store, f"G2 store lost isolation/atomicity token: {token}")
require("EditorProjectStore" not in store, "G2 store must not touch schema-7 persistence")
require("VideoEditorService" not in store, "G2 store must not touch the classic render path")

session = read(CORE / "TimelineEditorSession.cs")
require("TimelineProjectStoreV2" in session, "G2 session must own the V2 store")
for token in ("new EditorPage", "EditorPage.", ": EditorPage", "new EditorProjectStore", "EditorProjectStore."):
    require(token not in session, f"G2 session must not touch classic Editor state: {token}")

plan = read(CORE / "TimelineRenderPlan.cs")
require("TimelineRenderPlan" in plan and "TimelineCompiler" in plan,
        "G2 compiler must produce one immutable plan type")
preview = read(CORE / "TimelinePreviewController.cs")
export = read(CORE / "TimelineExportService.cs")
require("TimelineRenderPlan plan" in preview and "TimelineRenderPlan plan" in export,
        "G2 preview/export shells must consume the shared immutable plan")
for token in ("VideoEditorService", "EditorPlaybackController", "ProcessRunner",
               "RunAsync(ffmpeg", "File.Move(", "File.Delete(", "File.Write"):
    require(token not in preview,
            f"G2 preview shell must not perform media IO/process work: {token}")
# G10 promotes the export shell to a real V2 segment/concat service. It may own
# V2-root file IO, but must stay free of classic render/process owners.
for token in ("VideoEditorService", "EditorPlaybackController", "TimelineOneClipParity",
              "ToolManager", "EnsureFfmpegAsync"):
    require(token not in export,
            f"G10 export service coupled to classic owner: {token}")

# App page stays separate from EditorPage; G14 promotes its navigation entry
# while the classic items and default selection stay untouched.
require((PAGES / "TimelineEditorPage.xaml").exists(), "G2 TimelineEditorPage.xaml missing")
require((PAGES / "TimelineEditorPage.xaml.cs").exists(), "G2 TimelineEditorPage.xaml.cs missing")
page_cs = read(PAGES / "TimelineEditorPage.xaml.cs")
require("TimelineProjectStoreV2.FromAppPaths(application.Paths)" in page_cs,
        "G2 page must use the isolated V2 test root")
require("TimelineCompiler.Compile(" in page_cs, "G2 page must resolve the shared plan type")
for token in ("new EditorPage", "EditorPage.", ": EditorPage", "new EditorProjectStore",
               "EditorProjectStore.", "new VideoEditorService", "VideoEditorService.",
               "Paths.Data", "_editorProjects"):
    require(token not in page_cs, f"G2 page must not touch classic state: {token}")

main_cs = read(MAINWINDOW)
require('new TimelineEditorPage(_application, folderPicker, filePicker)' in main_cs,
        "G14 page must register in the normal navigation")
require("TimelineFeatureFlags.IsSuppressed" in main_cs
        and 'Tag as string, "timelinev2"' in main_cs,
        "G14 must hide the Timeline entry only on kill-switch (DEC-024)")
main_xaml = read(MAIN_XAML)
require('Tag="timelinev2"' in main_xaml and "Timeline V2 (dev)" not in main_xaml,
        "G14 navigation must carry a permanent Timeline entry without the dev label")
for tag in ('Tag="video"', 'Tag="ocr"', 'Tag="editor"', 'Tag="settings"'):
    require(tag in main_xaml, f"G14 must keep classic navigation item: {tag}")

print("PASS: G2 Timeline V2 shell is flagged, separate and has no production writes")

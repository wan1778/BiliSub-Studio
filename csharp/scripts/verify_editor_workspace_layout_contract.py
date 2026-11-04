#!/usr/bin/env python3
"""Source contract for the built-in resizable workspace; not a visual PASS."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
SPLITTER = ROOT / "csharp/src/BiliSubStudio.App/Controls/WorkspaceSplitter.cs"
LAYOUT = PAGES / "EditorPage.WorkspaceLayout.cs"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


xaml = read(XAML)
splitter = read(SPLITTER)
layout = read(LAYOUT)
bootstrap = read(BOOTSTRAP)

# WORKSPACE-01: exactly four minimal splitters: Source|Player, Player|Details,
# editor main|Timeline, and Timeline header|track content.
for name in (
    "SourcePlayerSplitter",
    "PlayerDetailsSplitter",
    "WorkspaceTimelineSplitter",
    "TimelineHeaderSplitter",
):
    require(f'x:Name="{name}"' in xaml, f"workspace splitter missing: {name}")
require(xaml.count("<controls:WorkspaceSplitter") == 4,
        "Editor workspace must declare exactly four WorkspaceSplitter controls")
require("Orientation=\"Horizontal\"" in xaml and "Orientation=\"Vertical\"" in xaml,
        "workspace splitters must support horizontal and vertical resize")

# WORKSPACE-02: splitter is app-owned pointer logic, not an external docking library.
require("public sealed class WorkspaceSplitter : Grid" in splitter
        and "public event EventHandler<double>? DragDelta" in splitter
        and "CapturePointer(e.Pointer)" in splitter
        and "ReleasePointerCapture(e.Pointer)" in splitter,
        "workspace splitter must be a minimal pointer-owned control")
# Drag delta must be read in the PARENT's space: a self-relative point feeds the
# splitter's own movement back into the delta (runaway resize + reversed drag).
require("Parent as FrameworkElement ?? this" in splitter
        and "e.GetCurrentPoint(space)" in splitter,
        "workspace splitter must measure the drag against its parent, not itself")
for forbidden in ("GridSplitter", "AvalonDock", "Syncfusion", "Actipro"):
    require(forbidden not in xaml + splitter + layout,
            f"external docking dependency returned: {forbidden}")

# WORKSPACE-03: resizing is bounded, and only workspace geometry owns the deltas.
for marker in (
    "Math.Clamp(SourceColumn.Width.Value + delta, _sourceColumnMinWidth, _sourceColumnMaxWidth)",
    "Math.Clamp(DetailsColumn.Width.Value - delta, _detailsColumnMinWidth, _detailsColumnMaxWidth)",
    # The timeline row sits BELOW its splitter: dragging down must shrink it.
    "Math.Clamp(TimelineRow.Height.Value - delta, _timelineRowMinHeight, _timelineRowMaxHeight)",
    # The header column sits LEFT of its splitter: dragging right must widen it.
    "Math.Clamp(TimelineHeaderColumn.Width.Value + delta, _timelineHeaderMinWidth, _timelineHeaderMaxWidth)",
):
    require(marker in layout, "workspace drag lost its reviewed clamp: " + marker)
require("MaxWidth=\"200\"" in xaml and "MaxWidth=\"380\"" in xaml
        and "MinHeight=\"160\"" in xaml and "MaxHeight=\"420\"" in xaml
        and "MinWidth=\"56\"" in xaml and "MaxWidth=\"150\"" in xaml,
        "workspace grid must declare explicit min/max bounds")

# WORKSPACE-04: layout persists under Data/Editor/workspace.json and is never
# allowed to block render if the JSON is missing or corrupt.
require("Path.Combine(_application.Paths.Data, \"Editor\", \"workspace.json\")" in layout,
        "workspace layout must live under the app Data/Editor directory")
for marker in ("EditorWorkspaceState", "LoadWorkspaceLayout", "ApplyWorkspaceLayout",
               "CaptureWorkspaceLayout", "SaveWorkspaceLayoutAsync", "File.Move(temporary"):
    require(marker in layout, "workspace layout persistence missing " + marker)
require("catch (Exception)" in layout and layout.count("catch (Exception)") >= 2,
        "workspace layout load/save failures must fall back instead of blocking the editor")

# WORKSPACE-05: collapse/restore has a real owner and cannot resurrect a panel
# below its reviewed minimum without restoring the saved size.
require("SourceCollapseButton" in xaml and "TimelineCollapseButton" in xaml
        and "SourceCollapseToggle_Click" in layout and "TimelineCollapseToggle_Click" in layout,
        "workspace must keep a collapse/restore owner")
require("SourceColumn.MinWidth = 0;" in layout and "SourceColumn.MinWidth = _sourceColumnMinWidth;" in layout,
        "Source collapse/restore must relax and restore its reviewed minimum width")
require("TimelineRow.MinHeight = 0;" in layout and "TimelineRow.MinHeight = _timelineRowMinHeight;" in layout,
        "Timeline collapse/restore must relax and restore its reviewed minimum height")

# WORKSPACE-07: a collapsed panel is session-only. Restoring the collapse from
# disk brought the timeline (and Source) back hidden on every reopen, which was
# never requested; the drag on a collapsed column and the racing save are what
# made the saved state disagree with the screen.
require("_workspaceLayout.SourceCollapsed = false;" in layout
        and "_workspaceLayout.TimelineCollapsed = false;" in layout,
        "collapsed panels must not be restored from the persisted layout")
source_drag = layout.split("private void SourcePlayerSplitter_DragDelta", 1)
require(len(source_drag) == 2 and "if (_sourceCollapsed) return;" in source_drag[1][:600],
        "a collapsed Source column must ignore its splitter instead of stretching back out")
require("File.Move(temporary, WorkspaceLayoutPath, true)" in layout,
        "workspace save must move over the previous file instead of delete-then-move")
require("File.Delete(WorkspaceLayoutPath)" not in layout,
        "workspace save must not delete the target before moving (that lost updates)")
require("_workspaceSaveQueued" in layout,
        "overlapping workspace saves must queue instead of losing the newest state")

# WORKSPACE-06: startup smoke must audit the real 3 content + 2 splitter grid.
require("WorkspaceGrid.ColumnDefinitions.Count != 5" in bootstrap
        and "WorkspaceGrid.RowDefinitions.Count != 3" in bootstrap,
        "startup smoke must audit the resizable workspace grid, not the old three-column layout")

print("PASS: Editor workspace splitter, clamp, persistence and collapse contracts")

#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
TESTS = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


editor = EDITOR.read_text(encoding="utf-8")
application = APPLICATION.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
tests = TESTS.read_text(encoding="utf-8")

for token in (
    "private EditorSubtitleBurn? CurrentFrameSubtitleBurn()",
    ".Where(cue => seconds >= cue.Start && seconds < cue.End)",
    ".Select(cue => cue with { VietnameseText = SubtitlePreviewText(cue) })",
    "CurrentFrameSubtitleBurn(), cancellationToken",
    "QueuePreviewRefresh();",
):
    require(token in editor, f"SUBPREVIEW-01 Editor frame-subtitle state lost: {token}")

placement = editor.split("private void RenderSubtitlePlacement(Rect video)", 1)[1].split(
    "private void RenderSubtitleHandles", 1
)[0]
require("new TextBlock" not in placement and "SubtitlePreviewText" not in placement,
        "SUBPREVIEW-01 WinUI fake text renderer returned")
require("RenderSubtitleHandles(placement, video, stroke)" in placement,
        "SUBPREVIEW-01 draggable subtitle frame/handles were removed")

require("EditorSubtitleBurn? subtitle," in application,
        "SUBPREVIEW-01 application boundary lost frame subtitle")
require("regions, subtitle, cancellationToken" in application,
        "SUBPREVIEW-01 application boundary did not forward frame subtitle")

for token in (
    "subtitleAss, BuildAss(subtitle!, sourceWidth, sourceHeight)",
    "BuildPreviewFrameArguments(",
    'var graph = $"[0:v]setpts=PTS-STARTPTS+{timestamp}/TB[framebase];"',
    'BuildFilterCore(request, subtitleAssPath, "framebase", requireEdit: false)',
    '";[vout]scale=1280:-2:force_original_aspect_ratio=decrease[preview]"',
):
    require(token in service, f"SUBPREVIEW-01 shared ASS/FFmpeg frame renderer lost: {token}")

require("editor still-frame subtitle Preview uses the same ASS timeline as playback" in tests,
        "SUBPREVIEW-01 executable contract test is missing")

print("PASS: SUBPREVIEW-01 still frame and playback share ASS/libass placement")

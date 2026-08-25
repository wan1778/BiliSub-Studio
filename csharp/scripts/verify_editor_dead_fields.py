#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

FORBIDDEN_DEAD_FIELDS = (
    "RefreshFrameButton",
    "RegionTimelineCanvas",
    "_lastTimelineWidth",
)


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


code = "\n".join(path.read_text(encoding="utf-8") for path in PARTIALS)

for field in FORBIDDEN_DEAD_FIELDS:
    require(
        not re.search(rf"\b{re.escape(field)}\b", code),
        f"CLEAN-03 dead compatibility field/reference returned: {field}",
    )

# Active state owners that can look 'old' must not be swept merely because they are
# nullable or assigned from XAML. These tokens lock the field-vs-live-owner boundary.
for live_owner in (
    "_editorExportProgressTimer",
    "_voiceArtifactWatcher",
    "_projectSaveTimer",
    "_imageOverlayCanvas",
    "_editorAutoCompositeCancellation",
):
    require(code.count(live_owner) > 1, f"CLEAN-03 live owner was removed accidentally: {live_owner}")

fixture = "private readonly Button RefreshFrameButton = new();\nRefreshFrameButton.IsEnabled = true;"
require(
    any(re.search(rf"\b{re.escape(field)}\b", fixture) for field in FORBIDDEN_DEAD_FIELDS),
    "CLEAN-03 negative fixture failed to represent a dead compatibility field",
)

print("PASS: CLEAN-03 Editor dead field contract is locked")

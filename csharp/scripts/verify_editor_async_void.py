#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
PARTIALS = sorted(PAGES.glob("EditorPage*.cs"))

XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
UI_EVENT_NAMES = {
    "Click", "Checked", "Unchecked", "Toggled", "SelectionChanged", "TextChanged",
    "ValueChanged", "LostFocus", "PointerPressed", "PointerMoved", "PointerReleased",
    "PointerCanceled", "Loaded", "Unloaded", "SizeChanged", "LayoutUpdated", "KeyDown",
}
ASYNC_VOID_RE = re.compile(
    r"(?m)^\s*(?:(?:public|private|protected|internal)\s+)?"
    r"(?:(?:static|sealed|virtual|override|new)\s+)*"
    r"async\s+void\s+([A-Za-z_]\w*)\s*\("
)


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def async_void_declarations(code_by_file: dict[str, str]) -> list[tuple[str, str]]:
    result: list[tuple[str, str]] = []
    for file_name, source in code_by_file.items():
        result.extend((file_name, match.group(1)) for match in ASYNC_VOID_RE.finditer(source))
    return result


def runtime_event_handlers(code: str) -> set[str]:
    return set(re.findall(r"\+=\s*([A-Za-z_]\w*)\s*;", code))


def xaml_event_handlers(xaml_path: Path) -> set[str]:
    root = ET.parse(xaml_path).getroot()
    result: set[str] = set()
    for element in root.iter():
        for raw_name, handler in element.attrib.items():
            event_name = raw_name.rsplit("}", 1)[-1]
            if event_name in UI_EVENT_NAMES and handler and not handler.startswith("{"):
                result.add(handler)
    return result


require(PARTIALS, "CLEAN-10 EditorPage partials not found")
require(XAML.is_file(), "CLEAN-10 EditorPage.xaml not found")

code_by_file = {path.name: path.read_text(encoding="utf-8") for path in PARTIALS}
code = "\n".join(code_by_file.values())
async_voids = async_void_declarations(code_by_file)
owners = xaml_event_handlers(XAML) | runtime_event_handlers(code)

violations = [(file_name, name) for file_name, name in async_voids if name not in owners]
require(
    not violations,
    "CLEAN-10 async void is allowed only for event handlers; "
    + ", ".join(f"{file_name}:{name}" for file_name, name in violations),
)

for file_name, name in async_voids:
    calls = len(re.findall(rf"\b{re.escape(name)}\s*\(", code))
    require(
        calls == 1,
        f"CLEAN-10 async-void handler must not be called directly: {file_name}:{name} ({calls} occurrences)",
    )

images = code_by_file.get("EditorPage.Images.cs", "")
require(images, "CLEAN-10 EditorPage.Images.cs not found")
require(
    re.search(r"\basync\s+void\s+FinishImageDrag\s*\(", images) is None,
    "CLEAN-10 FinishImageDrag helper returned as async void",
)
require(
    re.search(r"\basync\s+Task\s+FinishImageDragAsync\s*\(", images) is not None,
    "CLEAN-10 image drag helper must return Task",
)
for handler, commit in (
    ("ImageOverlay_PointerReleased", "true"),
    ("ImageOverlay_PointerCanceled", "false"),
):
    pattern = (
        rf"\basync\s+void\s+{handler}\s*\([^)]*\)\s*=>\s*"
        rf"await\s+FinishImageDragAsync\s*\(\s*e\s*,\s*commit:\s*{commit}\s*\)\s*;"
    )
    require(
        re.search(pattern, images, re.S) is not None,
        f"CLEAN-10 {handler} must be the async-void event boundary and await the Task helper",
    )
require(
    len(re.findall(r"\bFinishImageDragAsync\s*\(", images)) == 3,
    "CLEAN-10 FinishImageDragAsync must have exactly two event calls plus one implementation",
)

fixture_code = {"Fixture.cs": "private async void HelperAsync() { await WorkAsync(); }\n"}
fixture_owners: set[str] = set()
fixture_violations = [
    name
    for _, name in async_void_declarations(fixture_code)
    if name not in fixture_owners
]
require(
    fixture_violations == ["HelperAsync"],
    "CLEAN-10 negative fixture no longer detects an unowned async-void helper",
)

print(
    f"PASS: CLEAN-10 {len(async_voids)} async-void declarations are event-owned; "
    "image drag helper returns Task"
)

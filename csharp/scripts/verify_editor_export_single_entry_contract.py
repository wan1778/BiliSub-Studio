#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def extract_method(text: str, signature: str) -> str:
    start = text.find(signature)
    if start < 0:
        return ""
    brace = text.find("{", start + len(signature))
    if brace < 0:
        return ""
    depth = 0
    for index in range(brace, len(text)):
        char = text[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return text[start:index + 1]
    return ""


def contract_errors(xaml: str, parts: dict[str, str]) -> list[str]:
    errors: list[str] = []
    all_cs = "\n".join(parts.values())

    # EXPORT-01: the visible Editor exposes one final Render action. Navigation to
    # the Export inspector is not a render entry point; only RenderButton is.
    if xaml.count('x:Name="RenderButton"') != 1:
        errors.append("Editor must expose exactly one RenderButton.")
    if xaml.count('Click="Render_Click"') != 1:
        errors.append("RenderButton must have exactly one XAML Render_Click binding.")
    if "RenderButton.Click +=" in all_cs:
        errors.append("RenderButton must not receive a second code-behind Click binding.")

    handler_signature = "private async void Render_Click("
    if all_cs.count(handler_signature) != 1:
        errors.append("Editor must define exactly one Render_Click handler.")
        handler = ""
    else:
        owner = next(text for text in parts.values() if handler_signature in text)
        handler = extract_method(owner, handler_signature)
        if not handler:
            errors.append("Render_Click body could not be parsed.")

    if handler:
        if handler.count("await RenderProjectAsync();") != 1:
            errors.append("Render_Click must delegate exactly once to RenderProjectAsync.")
        if "_application.StartEditor(" in handler or "EditorImageOverlayComposer" in handler:
            errors.append("Render_Click must not start a render pipeline directly.")

    orchestrator_signature = "private async Task RenderProjectAsync()"
    if all_cs.count(orchestrator_signature) != 1:
        errors.append("Editor must define exactly one RenderProjectAsync orchestrator.")
        orchestrator = ""
        orchestrator_owner = None
    else:
        orchestrator_owner = next(name for name, text in parts.items() if orchestrator_signature in text)
        orchestrator = extract_method(parts[orchestrator_owner], orchestrator_signature)
        if not orchestrator:
            errors.append("RenderProjectAsync body could not be parsed.")

    # The method definition does not contain parentheses followed by semicolon, so
    # this count is the call-site count. It must remain exactly one: Render_Click.
    if all_cs.count("RenderProjectAsync();") != 1:
        errors.append("RenderProjectAsync must have exactly one caller: Render_Click.")

    if orchestrator:
        # Internal branching is allowed: no-image export, base edit before image
        # composition, and image composition all remain owned by this one entry.
        if "_application.StartEditor(" not in orchestrator:
            errors.append("RenderProjectAsync must own the base Editor render path.")
        if "composer.RenderAsync(" not in orchestrator:
            errors.append("RenderProjectAsync must own the image/logo composition path.")

        stripped_parts = dict(parts)
        stripped_parts[orchestrator_owner] = stripped_parts[orchestrator_owner].replace(orchestrator, "", 1)
        outside = "\n".join(stripped_parts.values())
        if "_application.StartEditor(" in outside:
            errors.append("A final Editor render bypasses RenderProjectAsync via StartEditor.")
        if "composer.RenderAsync(" in outside or "new EditorImageOverlayComposer(" in outside:
            errors.append("A final image/logo render bypasses RenderProjectAsync.")

    if all_cs.count("RenderButton.IsEnabled =") != 1:
        errors.append("RenderButton enablement must have one state owner.")

    return errors


def main() -> int:
    if not XAML.exists():
        print(f"FAIL: missing {XAML}", file=sys.stderr)
        return 1

    part_paths = sorted(PAGES.glob("EditorPage*.cs"))
    if not part_paths:
        print("FAIL: no EditorPage code-behind parts found", file=sys.stderr)
        return 1
    parts = {path.name: read(path) for path in part_paths}
    xaml = read(XAML)

    errors = contract_errors(xaml, parts)
    if errors:
        for error in errors:
            print("FAIL:", error, file=sys.stderr)
        return 1

    # Negative fixtures ensure the gate actually rejects a second render route.
    duplicate_binding = xaml.replace(
        'Click="Render_Click"',
        'Click="Render_Click" data-export01="duplicate" Click="Render_Click"',
        1,
    )
    if not contract_errors(duplicate_binding, parts):
        print("FAIL: duplicate Render_Click fixture was not rejected", file=sys.stderr)
        return 1

    mutated = dict(parts)
    any_name = next(iter(mutated))
    mutated[any_name] += (
        "\nprivate async void HiddenRender_Click(object sender, object e)\n"
        "{\n    _application.StartEditor(CurrentEditRequest(CompletedSubtitleBurn()));\n}\n"
    )
    if not contract_errors(xaml, mutated):
        print("FAIL: direct StartEditor bypass fixture was not rejected", file=sys.stderr)
        return 1

    print("PASS: EXPORT-01 Editor has one Render entry point and one final-render orchestrator")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

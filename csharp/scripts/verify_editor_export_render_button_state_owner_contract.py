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

    # XAML owns only the fail-safe startup default. Runtime state ownership must be
    # centralized in RefreshEditorActions().
    if xaml.count('x:Name="RenderButton"') != 1:
        errors.append("Editor must declare exactly one RenderButton.")
    if 'x:Name="RenderButton"' in xaml and 'IsEnabled="False"' not in xaml.split('x:Name="RenderButton"', 1)[1].split("/>", 1)[0]:
        errors.append("RenderButton must start disabled until runtime state is evaluated.")

    assignment = "RenderButton.IsEnabled ="
    if all_cs.count(assignment) != 1:
        errors.append("RenderButton.IsEnabled must have exactly one runtime assignment.")

    owner_signature = "private void RefreshEditorActions()"
    if all_cs.count(owner_signature) != 1:
        errors.append("Editor must define exactly one RefreshEditorActions state owner.")
        owner = ""
        owner_name = None
    else:
        owner_name = next(name for name, text in parts.items() if owner_signature in text)
        owner = extract_method(parts[owner_name], owner_signature)
        if not owner:
            errors.append("RefreshEditorActions body could not be parsed.")

    if owner:
        if owner.count(assignment) != 1:
            errors.append("The sole RenderButton.IsEnabled assignment must live inside RefreshEditorActions.")

        required_tokens = (
            "var idle = !EditorBusy;",
            "var hasMedia = _media is not null;",
            "var editable = idle && hasMedia && !_playback.IsPreviewMode;",
            "var subtitleReady = _subtitleSource is not null",
            'var audioChanged = _audioSettings.SourceMode != "keep";',
            "var hasImages = _imageFeatureInitialized && _imageOverlays.Count > 0;",
            "editable && !_subtitleManualDirty && _path is not null",
            "_document.Regions.Count > 0",
            "subtitleReady",
            "audioChanged",
            "_voiceTrack is not null",
            "hasImages",
            "!string.IsNullOrWhiteSpace(FileNameBox.Text)",
        )
        for token in required_tokens:
            if token not in owner:
                errors.append(f"RefreshEditorActions lost RenderButton state input: {token}")

        outside_parts = dict(parts)
        outside_parts[owner_name] = outside_parts[owner_name].replace(owner, "", 1)
        outside = "\n".join(outside_parts.values())
        if assignment in outside:
            errors.append("A second runtime RenderButton state owner exists outside RefreshEditorActions.")

    return errors


def render_enabled(
    *,
    busy: bool,
    has_media: bool,
    preview_mode: bool,
    subtitle_dirty: bool,
    has_path: bool,
    has_region: bool,
    subtitle_ready: bool,
    audio_changed: bool,
    has_voice: bool,
    has_images: bool,
    file_name: str,
) -> bool:
    editable = (not busy) and has_media and (not preview_mode)
    return (
        editable
        and (not subtitle_dirty)
        and has_path
        and (has_region or subtitle_ready or audio_changed or has_voice or has_images)
        and bool(file_name.strip())
    )


def main() -> int:
    if not XAML.exists():
        print(f"FAIL: missing {XAML}", file=sys.stderr)
        return 1

    part_paths = sorted(PAGES.glob("EditorPage*.cs"))
    if not part_paths:
        print("FAIL: no EditorPage code-behind parts found", file=sys.stderr)
        return 1

    xaml = read(XAML)
    parts = {path.name: read(path) for path in part_paths}
    errors = contract_errors(xaml, parts)
    if errors:
        for error in errors:
            print("FAIL:", error, file=sys.stderr)
        return 1

    # Synthetic truth-table: all state combinations must flow through the same
    # predicate. A valid image-only edit is intentionally renderable.
    base = dict(
        busy=False,
        has_media=True,
        preview_mode=False,
        subtitle_dirty=False,
        has_path=True,
        has_region=False,
        subtitle_ready=False,
        audio_changed=False,
        has_voice=False,
        has_images=True,
        file_name="video_edited.mp4",
    )
    if not render_enabled(**base):
        print("FAIL: valid image-only project should enable RenderButton", file=sys.stderr)
        return 1

    blockers = (
        {"busy": True},
        {"has_media": False},
        {"preview_mode": True},
        {"subtitle_dirty": True},
        {"has_path": False},
        {"file_name": "   "},
    )
    for change in blockers:
        case = base | change
        if render_enabled(**case):
            print(f"FAIL: blocker did not disable RenderButton: {change}", file=sys.stderr)
            return 1

    no_edit = base | {"has_images": False}
    if render_enabled(**no_edit):
        print("FAIL: project with no exportable change must keep RenderButton disabled", file=sys.stderr)
        return 1

    for edit_flag in ("has_region", "subtitle_ready", "audio_changed", "has_voice", "has_images"):
        case = no_edit | {edit_flag: True}
        if not render_enabled(**case):
            print(f"FAIL: exportable state did not enable RenderButton: {edit_flag}", file=sys.stderr)
            return 1

    # Negative fixture: a new direct setter anywhere outside the owner must fail.
    mutated = dict(parts)
    any_name = next(iter(mutated))
    mutated[any_name] += "\nprivate void BadOwner() { RenderButton.IsEnabled = true; }\n"
    if not contract_errors(xaml, mutated):
        print("FAIL: duplicate RenderButton state owner fixture was not rejected", file=sys.stderr)
        return 1

    print("PASS: EXPORT-02 RenderButton has one runtime state owner with complete enablement inputs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

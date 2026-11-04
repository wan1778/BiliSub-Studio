#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
SERVICES = ROOT / "csharp/src/BiliSubStudio.App/Services"
CORE_APP = ROOT / "csharp/src/BiliSubStudio.Core/Application"
CONFIG = ROOT / "csharp/src/BiliSubStudio.Core/Configuration"
XAML = PAGES / "EditorPage.xaml"
BOOTSTRAP = PAGES / "EditorPage.ParityBootstrap.cs"
PARITY = PAGES / "EditorPage.ParityFixes.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
OUTPUT_TARGET = PAGES / "EditorPage.OutputTarget.cs"
FOLDER_PICKER = SERVICES / "FolderPickerService.cs"
SETTINGS = CORE_APP / "SettingsApplicationService.cs"
APPLICATION = CORE_APP / "BiliSubApplication.cs"
CONFIG_STORE = CONFIG / "JsonConfigStore.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


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


xaml = read(XAML)
bootstrap = read(BOOTSTRAP)
parity = read(PARITY)
editor = read(EDITOR)
images = read(IMAGES)
output_target = read(OUTPUT_TARGET)
folder_picker = read(FOLDER_PICKER)
settings = read(SETTINGS)
application = read(APPLICATION)
config_store = read(CONFIG_STORE)

# EXPORT-03 — the export dialog must expose one visible output-directory owner
# (tab card removed: dialog is the single owner since its confirm persists).
require(xaml.count('x:Name="ExportDialogDirectoryBox"') == 1,
        "EXPORT-03 requires one visible output-directory text owner")
require(xaml.count('x:Name="ExportDialogChooseDirectoryButton"') == 1,
        "EXPORT-03 requires one Choose output-directory button")
require(xaml.count('x:Name="ExportDialogOpenDirectoryButton"') == 1,
        "EXPORT-03 requires one Open output-directory button")
require('Content="Chọn"' in xaml and 'Click="ExportDialogChooseDirectory_Click"' not in xaml,
        "EXPORT-03 Choose button must remain user-visible and wired exactly once, in code")
require('Click="EditorChooseOutput_Click"' not in xaml,
        "EXPORT-03 Choose button must not gain a duplicate XAML Click owner")

# Static shell binds the one visible button exactly once.
require(bootstrap.count("ExportDialogChooseDirectoryButton.Click += EditorChooseOutput_Click;") == 1,
        "EXPORT-03 Choose output button must bind exactly once")
require(bootstrap.count("ExportDialogOpenDirectoryButton.Click += EditorOpenOutput_Click;") == 1,
        "EXPORT-03 Open output button must bind exactly once")
require("_editorOutputPathText = ExportDialogDirectoryBox;" in bootstrap,
        "EXPORT-03 output path text must be owned by the static shell")
require("ExportDialogDirectoryBox.Text = _application.Config.EditorOutputDirectory;" in bootstrap,
        "EXPORT-03 initial UI must show the current persisted Editor output directory")

choose = extract_method(parity, "private async void EditorChooseOutput_Click(")
require(choose, "EXPORT-03 Choose output handler is missing")
require("new FolderPickerService(() => window)" in choose,
        "EXPORT-03 Choose handler must open the native Windows folder picker")
require("PickFolderAsync(_application.Config.EditorOutputDirectory)" in choose,
        "EXPORT-03 picker must receive the current Editor output directory as its context")
require("if (string.IsNullOrWhiteSpace(path)) return;" in choose,
        "EXPORT-03 picker cancel must be a no-op")
require("await _application.Settings.SetEditorOutputDirectoryAsync(path, CancellationToken.None);" in choose,
        "EXPORT-03 selected folder must flow through Settings validation/persistence")
require("_editorOutputPathText.Text = _application.Config.EditorOutputDirectory" in choose,
        "EXPORT-03 UI must refresh from authoritative Config after selection")
require("RefreshEditorActions();" in choose,
        "EXPORT-03 editor action state must refresh after output-directory selection")

# FolderPicker must return null on cancel rather than inventing a path.
require("var folder = await picker.PickSingleFolderAsync();" in folder_picker,
        "EXPORT-03 native picker must use PickSingleFolderAsync")
require("return folder?.Path;" in folder_picker,
        "EXPORT-03 native picker cancel must return null")

# Settings owns path validation and durable config mutation.
set_output = extract_method(settings, "public async Task<SettingsSnapshot> SetOutputDirectoryAsync(")
require(set_output, "EXPORT-03 Settings output-directory owner is missing")
validate_helper = extract_method(settings, "private static async Task<string> ValidateWritableDirectoryAsync(")
require(validate_helper, "EXPORT-03 Settings writable-directory validator is missing")
for token in (
    "path = path?.Trim() ?? string.Empty;",
    "Directory.CreateDirectory(path);",
    ".bilisub-write-probe-",
    "File.WriteAllTextAsync(probe,",
):
    require(token in validate_helper, f"EXPORT-03 Settings lost required output-directory behavior: {token}")
require("config => config with { OutputDirectory = path }" in set_output
        and "ValidateWritableDirectoryAsync(path, cancellationToken)" in set_output,
        "EXPORT-03 Settings lost required output-directory behavior: config => config with { OutputDirectory = path }")
require("Thư mục đã chọn không cho phép BiliSub Studio ghi tệp." in validate_helper,
        "EXPORT-03 unwritable selected folders must be rejected")

# Config snapshot must update immediately and be written atomically for reopen.
require("public AppConfig Config => _configStore.Snapshot;" in application,
        "EXPORT-03 render/UI must read the current config snapshot")
require("Volatile.Write(ref _current, next);" in config_store,
        "EXPORT-03 config snapshot must update after a successful settings mutation")
require("await AtomicJsonFile.WriteAsync(paths.ConfigFile, next, cancellationToken)" in config_store,
        "EXPORT-03 selected output directory must persist to config for app reopen")

# UI display/open state remains derived from the same authoritative config.
refresh = extract_method(parity, "private void RefreshEditorParityControls()")
require(refresh, "EXPORT-03 output-directory control refresh owner is missing")
require("_editorOutputPathText.Text = _application.Config.EditorOutputDirectory" in refresh,
        "EXPORT-03 displayed output directory must come from Config")
require("Directory.Exists(_application.Config.EditorOutputDirectory)" in refresh,
        "EXPORT-03 Open-folder availability must follow the configured Editor output directory")

# PROJECT-10: Config is authoritative at click time, then one immutable target
# snapshot must feed every final stage so an in-flight export cannot jump folders.
capture = extract_method(output_target, "private EditorOutputTarget CaptureEditorOutputTarget(")
require(capture, "EXPORT-03/PROJECT-10 output target capture owner is missing")
require("_application.Config.EditorOutputDirectory" in capture,
        "EXPORT-03 output target must originate from the authoritative Config snapshot")
require("_project?.FileName" in capture,
        "EXPORT-03 output target must capture the current filename with the directory")

render_project = extract_method(images, "private async Task RenderProjectAsync()")
require(render_project, "EXPORT-03 RenderProjectAsync is missing")
require("outputTarget = await ShowExportDialogAsync();" in render_project,
        "EXPORT-03 final render must capture one output target")
require("OutputDirectory = outputTarget.Directory" in render_project
        and "FileName = outputTarget.FileName" in render_project,
        "EXPORT-03 direct/base final request must use the captured target")
require("composerInput, outputTarget.Directory, outputTarget.FileName" in render_project,
        "EXPORT-03 image/logo final composer must use the same captured target")
require("OutputDirectory = temporaryDirectory" in render_project,
        "EXPORT-03 image base intermediate must stay in temp rather than polluting final output directory")

# CurrentEditRequest may still expose Config as its default for Preview and callers,
# but final RenderProjectAsync must override it with the locked click-time target.
current_request = extract_method(editor, "private VideoEditRequest CurrentEditRequest(")
require(current_request, "EXPORT-03 CurrentEditRequest is missing")
require("_application.Config.EditorOutputDirectory," in current_request,
        "EXPORT-03 shared request default must continue to derive from the Editor Config folder")

# Tiny behavioral fixture: cancel preserves current directory; a chosen writable folder
# persists. Once Render is clicked, later config changes do not retarget that run.
def choose_output(current: str, picked: str | None) -> str:
    if picked is None or not picked.strip():
        return current
    return picked.strip()


old = r"C:\Users\Test\Downloads"
require(choose_output(old, None) == old,
        "EXPORT-03 fixture: picker cancel changed the current output directory")
require(choose_output(old, "") == old,
        "EXPORT-03 fixture: empty picker result changed the current output directory")

with tempfile.TemporaryDirectory() as td:
    selected = os.path.join(td, "exports")
    os.makedirs(selected, exist_ok=True)
    probe = os.path.join(selected, ".bilisub-write-probe.tmp")
    Path(probe).write_text("BiliSub Studio output write probe", encoding="utf-8")
    Path(probe).unlink()

    current = choose_output(old, selected)
    config_path = os.path.join(td, "config.json")
    Path(config_path).write_text(json.dumps({"OutputDirectory": current}), encoding="utf-8")
    reopened = json.loads(Path(config_path).read_text(encoding="utf-8"))["OutputDirectory"]
    require(reopened == selected,
            "EXPORT-03 fixture: selected output directory did not survive config reopen")

    click_target = reopened
    changed_after_click = os.path.join(td, "later-setting")
    os.makedirs(changed_after_click, exist_ok=True)
    require(click_target == selected and click_target != changed_after_click,
            "EXPORT-03 fixture: in-flight render was retargeted after click")

# Negative fixture proves the gate detects loss of Settings persistence.
require("SetEditorOutputDirectoryAsync(path" not in choose.replace(
    "await _application.Settings.SetEditorOutputDirectoryAsync(path, CancellationToken.None);", "", 1),
    "EXPORT-03 fixture sanity failure")

print("PASS: EXPORT-03 output directory selection, persistence and immutable in-flight render target are locked")

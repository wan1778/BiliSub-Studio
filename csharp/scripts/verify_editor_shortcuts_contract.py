#!/usr/bin/env python3
"""Kiểm tra hệ thống phím tắt của trang Editor: registry ở Core, mapping id → hành vi thật, quản lý focus và hộp thoại trợ giúp lấy từ registry."""
from __future__ import annotations

import sys
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor"
SHORTCUTS = PAGES / "EditorPage.Shortcuts.cs"
TIMELINE = PAGES / "EditorPage.Timeline.cs"
PLAYBACK = PAGES / "EditorPage.Playback.cs"
EDITOR = PAGES / "EditorPage.xaml.cs"
EXPORT_DIALOG = PAGES / "EditorPage.ExportDialog.cs"
XAML = PAGES / "EditorPage.xaml"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


def window(text: str, marker: str, length: int = 3200) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    return text[index:index + length]


def method(text: str, marker: str) -> str:
    index = text.find(marker)
    if index < 0:
        return ""
    end = text.find("\n    }", index)
    return text[index:end if end > 0 else index + 1200]


shortcuts = SHORTCUTS.read_text(encoding="utf-8")
timeline = TIMELINE.read_text(encoding="utf-8")
playback = PLAYBACK.read_text(encoding="utf-8")
editor = EDITOR.read_text(encoding="utf-8")
export_dialog = EXPORT_DIALOG.read_text(encoding="utf-8")
xaml = XAML.read_text(encoding="utf-8")
core = (CORE / "EditorShortcuts.cs").read_text(encoding="utf-8")

# SHORTCUT-01: ONE real binding registry in Core that describes the action id,
# key, modifier, display name, group and scope.
for token in (
    "public static class EditorShortcuts",
    "public sealed class EditorShortcutRegistry",
    "public sealed record EditorShortcutBinding",
    "public enum EditorShortcutScope",
    "public enum EditorShortcutGroup",
    "public string Gesture => EditorShortcuts.FormatGesture(Key, Modifiers);",
):
    require(token in core, f"SHORTCUT-01 the core shortcut registry lost: {token}")

required_bindings = (
    ("TogglePlaybackId", "Space", ""),
    ("StepBackwardId", "Left", ""),
    ("StepForwardId", "Right", ""),
    ("StepBackwardTenId", "Left", "Shift"),
    ("StepForwardTenId", "Right", "Shift"),
    ("GoToStartId", "Home", ""),
    ("GoToEndId", "End", ""),
    ("SplitAtPlayheadId", "B", "Ctrl"),
    ("RemoveSelectedClipId", "Delete", ""),
    ("ZoomAtPointerId", "Wheel", "Ctrl"),
    ("SaveProjectId", "S", "Ctrl"),
    ("UndoId", "Z", "Ctrl"),
    ("RedoId", "Y", "Ctrl"),
)
defaults = window(core, "public static IReadOnlyList<EditorShortcutBinding> CreateDefaultBindings()", 8000)
require(defaults != "", "SHORTCUT-01 the default binding list is missing")
for id_name, key, modifiers in required_bindings:
    require(f"public const string {id_name} =" in core,
            f"SHORTCUT-01 the shortcut id '{id_name}' is missing")
    binding = window(defaults, f"            {id_name},", 400)
    require(binding != "", f"SHORTCUT-01 the default binding '{id_name}' is missing")
    require(f'"{key}"' in binding,
            f"SHORTCUT-01 the shortcut '{id_name}' must use the key '{key}'")
    if modifiers:
        require(f'"{modifiers}"' in binding,
                f"SHORTCUT-01 the shortcut '{id_name}' must use the modifier '{modifiers}'")
    require(f"EditorShortcutGroup." in binding,
            f"SHORTCUT-01 the shortcut '{id_name}' must declare its group")
    require(f"EditorShortcutScope." in binding,
            f"SHORTCUT-01 the shortcut '{id_name}' must declare its scope")

group_enum = window(core, "public enum EditorShortcutGroup", 900)
require(group_enum != "", "SHORTCUT-01 the shortcut group enum is missing")
for group in (
    "PlaybackAndNavigation",
    "TimelineEditing",
    "ZoomAndView",
    "Project",
):
    require(group in group_enum, f"SHORTCUT-01 the shortcut group '{group}' is missing")

scope_enum = window(core, "public enum EditorShortcutScope", 900)
require(scope_enum != "", "SHORTCUT-01 the shortcut scope enum is missing")
for scope in (
    "Application",
    "EditorPage",
    "Timeline",
):
    require(scope in scope_enum, f"SHORTCUT-01 the shortcut scope '{scope}' is missing")

require('"Phát video và điều hướng"' in core and '"Chỉnh sửa timeline"' in core
        and '"Zoom và hiển thị"' in core and '"Dự án"' in core,
        "SHORTCUT-01 the four shortcut group names must be declared once")

# SHORTCUT-02: a same key + modifier conflict inside the same scope must fail
# at registration; the same key in a different scope is valid.
register = method(core, "public void Register(EditorShortcutBinding binding)")
require(register != "", "SHORTCUT-02 the registry register owner is missing")
for token in (
    "existing.Scope == binding.Scope",
    "EditorShortcuts.CanonicalModifiers(existing.Modifiers)",
    "Xung đột phím tắt",
    "id bị trùng",
):
    require(token in register, f"SHORTCUT-02 the conflict detection lost: {token}")

# SHORTCUT-03: the UI registers the id → real action mapping, one action per
# binding; there is no independent handler for each key.
for token in (
    "private readonly Dictionary<string, Func<Task>> _shortcutActions",
    "var registry = EditorShortcuts.CreateDefaultRegistry();",
    "[EditorShortcuts.TogglePlaybackId] = TogglePlaybackAsync,",
    "[EditorShortcuts.StepBackwardId] = () => StepTimelineFramesAsync(-1),",
    "[EditorShortcuts.StepForwardId] = () => StepTimelineFramesAsync(1),",
    "[EditorShortcuts.StepBackwardTenId] = () => StepTimelineFramesAsync(-10),",
    "[EditorShortcuts.StepForwardTenId] = () => StepTimelineFramesAsync(10),",
    "[EditorShortcuts.GoToStartId] = GoToTimelineStartAsync,",
    "[EditorShortcuts.GoToEndId] = GoToTimelineEndAsync,",
    "[EditorShortcuts.SplitAtPlayheadId] = SplitTimelineClipAtPlayheadAsync,",
    "[EditorShortcuts.RemoveSelectedClipId] = RemoveSelectedTimelineClipAsync,",
    "[EditorShortcuts.SaveProjectId] = SaveProjectFromShortcutAsync,",
    "_shortcutActions.TryGetValue(binding.Id, out var action)",
):
    require(token in shortcuts, f"SHORTCUT-03 the id → behavior mapping lost: {token}")

# SHORTCUT-04: the UI calls the real commands (the Split button, the Remove
# button, the Play/Pause button, project save) instead of rewriting the logic.
split_button = method(timeline, "private async void TimelineSplitClipButton_Click(")
require("await SplitTimelineClipAtPlayheadAsync();" in split_button,
        "SHORTCUT-04 the split button must call the shared split command")
require("private async Task SplitTimelineClipAtPlayheadAsync()" in timeline,
        "SHORTCUT-04 the shared split command is missing")
require("EditorTimelineComposition.SplitAtSourceTime(" in timeline,
        "SHORTCUT-04 the split command must keep the real composition split")

remove_button = method(timeline, "private async void TimelineRemoveClipButton_Click(")
require("await RemoveSelectedTimelineClipAsync();" in remove_button,
        "SHORTCUT-04 the remove button must call the shared remove command")
require("private async Task RemoveSelectedTimelineClipAsync()" in timeline,
        "SHORTCUT-04 the shared remove command is missing")
require("EditorTimelineComposition.RemoveAt(" in timeline,
        "SHORTCUT-04 the remove command must keep the real composition remove")

play_button = method(playback, "private async void PlayerPlayPause_Click(")
require("await TogglePlaybackAsync();" in play_button,
        "SHORTCUT-04 the play button must call the shared playback command")
require("private async Task TogglePlaybackAsync()" in playback,
        "SHORTCUT-04 the shared playback command is missing")
require("await _playback.ToggleAsync();" in playback,
        "SHORTCUT-04 the shared playback command must call the real player")

require("await SaveProjectNowAsync();" in shortcuts,
        "SHORTCUT-04 the save shortcut must call the real project save")
require("private async Task SaveProjectNowAsync()" in editor,
        "SHORTCUT-04 the real project save owner is missing")

# SHORTCUT-05: seek by the REAL fps of the media being seeked, never assuming
# 30.
step = method(shortcuts, "private Task StepTimelineFramesAsync(int frames)")
for token in (
    "var frameRate = TimelineFrameRateAt(Timeline.Value);",
    "EditorTimelinePlayhead.FrameIndex(Timeline.Value, frameRate)",
    "EditorTimelinePlayhead.FrameTime(index, frameRate)",
):
    require(token in step, f"SHORTCUT-05 the frame step lost: {token}")
require("30" not in step.replace("FrameTime(index, frameRate)", ""),
        "SHORTCUT-05 the frame step must not assume 30fps")
require("private double TimelineFrameRateAt(double timelineTime)" in timeline,
        "SHORTCUT-05 the real frame rate owner is missing")

# SHORTCUT-06: focus management — a text box keeps its own keys, a dialog
# blocks the commands behind it, and shortcuts only run while the Editor page
# is active.
dispatch = method(shortcuts, "private bool HandleEditorShortcut(KeyRoutedEventArgs e)")
for token in (
    "if (e.Handled) return false;",
    "if (!_shortcutsReady || !IsLoaded) return false;",
    "if (WorkspaceGrid.Visibility != Visibility.Visible) return false;",
    "if (EditorDialogOpen) return false;",
    "if (EditorTextInputBlocksShortcut(e.Key, modifiers)) return false;",
    "if (EditorFocusOwnsActivationKey(e.Key)) return false;",
    "if (IsEditorNavigationKey(e.Key) && EditorFocusOwnsNavigationKeys()) return false;",
):
    require(token in dispatch, f"SHORTCUT-06 the focus guard lost: {token}")

focus = method(shortcuts, "private bool IsEditorTextInputFocused()")
for token in ("FocusManager.GetFocusedElement(XamlRoot)", "TextBox => true", "PasswordBox => true",
              "RichEditBox => true", "AutoSuggestBox => true", "NumberBox => true",
              "ComboBox combo => combo.IsEditable"):
    require(token in focus, f"SHORTCUT-06 the text input guard lost: {token}")

text_guard = method(shortcuts, "private bool EditorTextInputBlocksShortcut(VirtualKey key, string modifiers)")
for token in (
    "if (!IsEditorTextInputFocused()) return false;",
    "VirtualKey.Space or VirtualKey.Delete or VirtualKey.Back",
    "VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down",
    "VirtualKey.Home or VirtualKey.End",
):
    require(token in text_guard, f"SHORTCUT-06 the text editing guard lost: {token}")

nav_guard = method(shortcuts, "private bool EditorFocusOwnsNavigationKeys()")
for token in ("ComboBox => true", "ListViewBase => true", "ScrollViewer => true",
              "RangeBase => !ReferenceEquals(focused, Timeline)"):
    require(token in nav_guard, f"SHORTCUT-06 the navigation key guard lost: {token}")

activation_guard = method(shortcuts, "private bool EditorFocusOwnsActivationKey(VirtualKey key)")
for token in ("if (key != VirtualKey.Space) return false;",
              "FocusManager.GetFocusedElement(XamlRoot) is ButtonBase or ComboBox or ListViewBase"):
    require(token in activation_guard, f"SHORTCUT-06 the activation key guard lost: {token}")

require(".Focus()" not in shortcuts and "Focus(FocusState" not in shortcuts,
        "SHORTCUT-06 the timeline shortcuts must never steal the focus of a text box")

require("private int _editorDialogDepth;" in shortcuts,
        "SHORTCUT-06 the open-dialog counter is missing")
require("internal bool EditorDialogOpen => _editorDialogDepth > 0;" in shortcuts,
        "SHORTCUT-06 the open-dialog guard is missing")
require("BeginEditorDialogScope()" in export_dialog,
        "SHORTCUT-06 the export dialog must block the timeline shortcuts while it is open")
require("using var dialogScope = BeginEditorDialogScope();" in export_dialog,
        "SHORTCUT-06 the export dialog scope is not released")

require("AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(EditorPage_PreviewKeyDown), handledEventsToo: true);" in shortcuts,
        "SHORTCUT-06 the shortcuts must use the WinUI tunnel phase (PreviewKeyDown)")
require("private void EditorPage_PreviewKeyDown(object sender, KeyRoutedEventArgs e)" in shortcuts,
        "SHORTCUT-06 the tunnel phase owner is missing")
require("if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Timeline)) return;" in shortcuts,
        "SHORTCUT-06 only the seek slider may hand its arrow keys to the shortcut registry")
require("if (!IsEditorNavigationKey(e.Key)) return;" in shortcuts,
        "SHORTCUT-06 the tunnel phase must only take the navigation keys")
require("private void Page_KeyDown(object sender, KeyRoutedEventArgs e)" in editor,
        "SHORTCUT-06 the bubble phase owner is missing")
require("private bool HandleEditorRegionShortcut(KeyRoutedEventArgs e)" in editor,
        "SHORTCUT-06 the effect region shortcut owner is missing")
region_shortcut = method(editor, "private bool HandleEditorRegionShortcut(KeyRoutedEventArgs e)")
require("if (IsEditorTextInputFocused()) return false;" in region_shortcut,
        "SHORTCUT-06 the effect region shortcut must leave the text inputs alone")
require("if (!TryUndoDocument()) return false;" in region_shortcut,
        "SHORTCUT-06 the effect region undo must not swallow Ctrl+Z when the region history is empty")
require("if (!TryRedoDocument()) return false;" in region_shortcut,
        "SHORTCUT-06 the effect region redo must not swallow Ctrl+Y when the region history is empty")

# SHORTCUT-07: the help dialog reads from the real registry, not a static
# table.
for token in (
    "private void RenderEditorShortcutsHelp()",
    "foreach (var group in _shortcutRegistry.Groups)",
    "var bindings = _shortcutRegistry.InGroup(group);",
    "EditorShortcuts.GroupName(group)",
    "BuildEditorShortcutRow(binding)",
    "binding.Gesture",
    "binding.DisplayName",
):
    require(token in shortcuts, f"SHORTCUT-07 the help dialog lost: {token}")

help_dialog = window(xaml, 'x:Name="EditorShortcutsDialog"', 900)
require(help_dialog != "", "SHORTCUT-07 the shortcuts help dialog is missing from the XAML")
require('x:Name="EditorShortcutsList"' in xaml,
        "SHORTCUT-07 the shortcuts list container is missing")
for gesture in ("Ctrl+B", "Ctrl+S", "Shift+Left"):
    require(gesture not in help_dialog,
            f"SHORTCUT-07 the help dialog must not hardcode the gesture '{gesture}'")

button = window(xaml, 'x:Name="EditorShortcutsButton"', 900)
require(button != "", "SHORTCUT-07 the shortcuts button is missing from the XAML")
require('AutomationProperties.Name="Phím tắt"' in button,
        "SHORTCUT-07 the shortcuts button must carry an AutomationProperties.Name")
require('AutomationProperties.AutomationId="EditorShortcutsButton"' in button,
        "SHORTCUT-07 the shortcuts button must carry an AutomationId for UIA")
require('AutomationProperties.AutomationId="EditorShortcutsDialog"' in xaml,
        "SHORTCUT-07 the shortcuts dialog must carry an AutomationId for UIA")
require('AutomationProperties.AutomationId="EditorShortcutsList"' in xaml,
        "SHORTCUT-07 the shortcuts list must carry an AutomationId for UIA")
require("private async void EditorShortcutsButton_Click(object sender, RoutedEventArgs e)" in shortcuts,
        "SHORTCUT-07 the shortcuts button handler is missing")

# SHORTCUT-08: Ctrl + wheel zooms on the pointer, FilmCraft's Alt + wheel stays
# intact. The anchor uses the shared pixel helper (scroll content + pointer).
wheel = method(timeline, "private void TimelineScrollViewer_PointerWheelChanged(")
for token in (
    "point.Properties.MouseWheelDelta",
    "e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)",
    "e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)",
    "var anchorContentX = EditorTimelineLayout.ZoomAnchorContentX(scrollOffset, pointerX);",
    "ApplyTimelineZoomPixels(newPps, anchorContentX);",
):
    require(token in wheel, f"SHORTCUT-08 the pointer zoom lost: {token}")
require("pointerX / pps" not in wheel,
        "SHORTCUT-08 the zoom anchor must not mix seconds with pixels")

# SHORTCUT-09: timeline undo/redo now has real behavior: the binding must be
# declared as supported and must be wired to the real undo/redo command, never
# to a fake action.
require("IsSupported: false," not in core,
        "SHORTCUT-09 no binding may claim to be unsupported any more")
require("giai đoạn 4" not in core,
        "SHORTCUT-09 the stage 4 unsupported note must be gone")
require("binding.UnsupportedNote ?? \"Phím tắt này chưa được hỗ trợ.\"" in shortcuts,
        "SHORTCUT-09 the UI must still report an unsupported binding")
for token in (
    "[EditorShortcuts.UndoId] = UndoTimelineAsync,",
    "[EditorShortcuts.RedoId] = RedoTimelineAsync,",
):
    require(token in shortcuts, f"SHORTCUT-09 the undo/redo action mapping lost: {token}")
require("private async Task UndoTimelineAsync()" in timeline,
        "SHORTCUT-09 the real undo command is missing")
require("private async Task RedoTimelineAsync()" in timeline,
        "SHORTCUT-09 the real redo command is missing")

# SHORTCUT-10: the executable contract must lock the registry and be
# registered.
tests = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs").read_text(encoding="utf-8")
require("editor shortcuts are declared once and conflicts are detected" in tests,
        "SHORTCUT-10 the executable contract test is missing")
contract = (ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/EditorShortcutsContract.cs").read_text(encoding="utf-8")
for token in (
    "EditorShortcuts.CreateDefaultRegistry()",
    "Xung đột phím tắt",
    "id bị trùng",
    "EditorShortcuts.CreateDefaultBindings().Count",
):
    require(token in contract, f"SHORTCUT-10 the contract test lost: {token}")

# SHORTCUT-11: Delete/Backspace must really delete the selected effect region.
# When there is no region to delete the key must fall back to the timeline's
# clip delete shortcut instead of being swallowed, and clicking the player or a
# timeline block must move keyboard focus out of the X/Y/W/H NumberBoxes so the
# Delete key belongs to the selected clip/region rather than to a text input.
require("if (!TryDeleteSelectedRegion()) return false;" in region_shortcut,
        "SHORTCUT-11 Delete must fall through to the clip shortcut when no effect region was deleted")
require("if (!TryDeleteSelectedRegion()) return false;\n        e.Handled = true;\n        return true;"
        in region_shortcut,
        "SHORTCUT-11 only a real effect-region deletion may consume Delete")

focus_surface = method(timeline, "private void FocusEditorKeyboardSurface()")
for token in (
    "FocusManager.GetFocusedElement(XamlRoot)",
    "Focus(FocusState.Programmatic)",
):
    require(token in focus_surface,
            f"SHORTCUT-11 the clicked-surface focus handoff lost: {token}")
for owner, marker in (
    ("the video clip release", "private void TimelineVideoClipPointerReleased("),
    ("the effect region release", "private void TimelineRegionPointerReleased("),
    ("the subtitle cue release", "private void TimelineCuePointerReleased("),
    ("the timeline release", "private async void TimelineContentGrid_PointerReleased("),
):
    require("FocusEditorKeyboardSurface();" in method(timeline, marker),
            f"SHORTCUT-11 {owner} must hand keyboard focus to the page")
require("FocusEditorKeyboardSurface();" in method(editor, "private void Overlay_PointerReleased("),
        "SHORTCUT-11 releasing an effect region on the player must hand keyboard focus to the page")
for owner, marker in (
    ("the video clip press", "private void TimelineVideoClipPressed("),
    ("the effect region press", "private void TimelineRegionPointerPressed("),
    ("the timeline seek press", "private void SeekTimelineFromPointer("),
    ("the player press", "private void Overlay_PointerPressed("),
):
    require("FocusEditorKeyboardSurface();" not in method(timeline if "Timeline" in marker else editor, marker),
            f"SHORTCUT-11 {owner} must not move focus before the drag/selection finishes")

# SHORTCUT-12: Ctrl+wheel is a pointer gesture, not a key press. The page's
# id -> behavior audit must skip it explicitly; otherwise initialization throws
# because no keyboard action exists for "Wheel" and `_shortcutsReady` stays
# false, silently disabling every other shortcut (including Delete).
require("public bool IsPointerGesture" in core,
        "SHORTCUT-12 the binding must expose IsPointerGesture")
require('IsPointerGesture => string.Equals(Key, "Wheel", StringComparison.OrdinalIgnoreCase)' in core,
        "SHORTCUT-12 only the Wheel gesture may be classified as a pointer gesture")
init = method(shortcuts, "private void InitializeEditorShortcuts()")
require("if (binding.IsPointerGesture) continue;" in init,
        "SHORTCUT-12 the registry audit must skip pointer gestures")
require("if (!actions.ContainsKey(binding.Id))" in init,
        "SHORTCUT-12 keyboard bindings must still require a real action")
require("IsPointerGesture" in contract,
        "SHORTCUT-12 the executable contract must pin the pointer gesture")

verify = (ROOT / "csharp/scripts/verify.ps1").read_text(encoding="utf-8")
require('Invoke-Checked python @("csharp/scripts/verify_editor_shortcuts_contract.py")' in verify,
        "SHORTCUT-10 the shortcut contract script must be registered in verify.ps1")

print("PASS: SHORTCUT-01/02/03/04/05/06/07/08/09/10/11/12 the shared shortcut registry, id -> behavior mapping, real command reuse, focus rules, registry-driven help dialog, the Ctrl+wheel pointer zoom and the Delete fallback are locked")

#!/usr/bin/env python3
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR_XAML = PAGES / "EditorPage.xaml"
EDITOR = PAGES / "EditorPage.xaml.cs"
CUE_EDITOR = PAGES / "EditorPage.SubtitleCueEditing.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
LOCAL_TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"
EVENT_MAP = ROOT / "docs/engineering/EDITOR_EVENT_MAP.md"
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def named_control(name: str) -> ET.Element:
    root = ET.parse(EDITOR_XAML).getroot()
    for element in root.iter():
        if element.attrib.get(f"{{{XAML_NS}}}Name") == name:
            return element
    fail(f"missing XAML control {name}")
    raise AssertionError("unreachable")


editor = read(EDITOR)
cue_editor = read(CUE_EDITOR)
project_store = read(PROJECT_STORE)
local_tts = read(LOCAL_TTS)
event_map = read(EVENT_MAP)

# VOICE-10 — cue override Auto/Nam/Nữ.
# One ComboBox owns the per-cue routing override. Auto means "no override".
# Male/female are persisted project values and must win over Whisper heuristics
# on the next local TTS generation. Any override change invalidates the old TTS
# master because it was generated with a different routing decision.

box = named_control("CurrentCueVoiceBox")
require(box.attrib.get("SelectionChanged") == "CurrentCueVoice_SelectionChanged",
        "VOICE-10 CurrentCueVoiceBox must have one SelectionChanged owner")
items = list(box)
tags = [item.attrib.get("Tag") for item in items if item.tag.endswith("ComboBoxItem")]
require(tags == ["auto", "male", "female"],
        f"VOICE-10 selector must expose exactly Auto/Nam/Nữ tags, got {tags}")
contents = [item.attrib.get("Content") for item in items if item.tag.endswith("ComboBoxItem")]
require(contents == ["Tự động Nam/Nữ", "Ép voice Nam", "Ép voice Nữ"],
        "VOICE-10 selector labels changed unexpectedly")

require(editor.count("private void CurrentCueVoice_SelectionChanged(") == 1,
        "VOICE-10 override handler must have exactly one implementation")
require("| `CurrentCueVoiceBox` | `SelectionChanged` | XAML | `CurrentCueVoice_SelectionChanged` | 1 |" in event_map,
        "VOICE-10 event map must keep one CurrentCueVoiceBox binding")

handler = editor.split("private void CurrentCueVoice_SelectionChanged(", 1)[1].split(
    "private void UpdateCurrentCueVoiceUi()", 1
)[0]
require("if (!IsLoaded || _syncingVoice || _project is null) return;" in handler,
        "VOICE-10 handler must reject lifecycle/programmatic-sync callbacks")
require("var cue = CurrentSubtitleCue();" in handler and "if (cue is null) return;" in handler,
        "VOICE-10 override must target the cue at the current Player time")
require('?? "auto"' in handler,
        "VOICE-10 missing/unknown UI selection must resolve to Auto")
require("new Dictionary<string, string>(_project.VoiceOverrides ?? new Dictionary<string, string>(), StringComparer.Ordinal)" in handler,
        "VOICE-10 must copy project overrides with stable cue-id comparison")
require('if (value is "male" or "female") overrides[cue.Id] = value;' in handler,
        "VOICE-10 Nam/Nữ selection must persist on the current cue id")
require("else overrides.Remove(cue.Id);" in handler,
        "VOICE-10 Auto must remove the current cue override rather than store an 'auto' value")
require("_voiceTrack = null;" in handler,
        "VOICE-10 changing routing must invalidate the previously generated voice track")
require("_project = _project with { VoiceOverrides = overrides, Tts = null };" in handler,
        "VOICE-10 changing routing must persist overrides and invalidate old TTS metadata together")
require("QueueProjectSave();" in handler,
        "VOICE-10 override must be autosaved to the Editor project")
require("RefreshEditorActions();" in handler,
        "VOICE-10 override change must refresh TTS/render availability")
for text in (
    "Đã ép câu hiện tại dùng voice Nam.",
    "Đã ép câu hiện tại dùng voice Nữ.",
    "Câu hiện tại trở lại tự động Nam/Nữ.",
):
    require(text in handler, f"VOICE-10 missing explicit user status: {text}")

sync = editor.split("private void UpdateCurrentCueVoiceUi()", 1)[1].split(
    "private async void Translate_Click", 1
)[0]
require("_syncingVoice = true;" in sync and "finally { _syncingVoice = false; }" in sync,
        "VOICE-10 UI hydration must be guarded from firing its own override handler")
require("var cue = CurrentSubtitleCue();" in sync,
        "VOICE-10 UI hydration must follow the cue at the Player position")
require("overrides.TryGetValue(cue.Id, out var selected)" in sync and ': "auto";' in sync,
        "VOICE-10 UI hydration must show persisted override or Auto")
require("CurrentCueVoiceBox.SelectedIndex = index;" in sync,
        "VOICE-10 UI hydration must restore the matching selector item")

timeline = editor.split("private void Timeline_ValueChanged(", 1)[1].split(
    "private async Task UpdateFrameAsync()", 1
)[0]
require("UpdateCurrentCueVoiceUi();" in timeline,
        "VOICE-10 moving the Player must rehydrate Auto/Nam/Nữ for the new current cue")

selection = cue_editor.split("private async void SubtitleCueList_SelectionChanged(", 1)[1].split(
    "private async Task SeekEditorToSubtitleCueAsync", 1
)[0]
require("await SeekEditorToSubtitleCueAsync(cue.Start);" in selection
        and "UpdateCurrentCueVoiceUi();" in selection,
        "VOICE-10 selecting a subtitle cue must move Player ownership and refresh its override")

seek = cue_editor.split("private async Task SeekEditorToSubtitleCueAsync(", 1)[1].split(
    "private void SubtitleManualText_TextChanged", 1
)[0]
require("Timeline.Value = target;" in seek and "UpdateCurrentCueVoiceUi();" in seek,
        "VOICE-10 cue seek must synchronize the voice selector with the Player time")

require("IReadOnlyDictionary<string, string>? VoiceOverrides = null" in project_store,
        "VOICE-10 EditorProject must persist per-cue voice overrides")
load = project_store.split("public async Task<EditorProject> LoadOrCreateAsync(", 1)[1].split(
    "private static EditorProject CreateFreshProject", 1
)[0]
require("VoiceOverrides = NormalizeVoiceOverrides(loaded.VoiceOverrides)" in load,
        "VOICE-10 reopen must normalize and restore voice overrides")
save = project_store.split("public async Task SaveAsync(", 1)[1].split(
    "public string GetProjectPath", 1
)[0]
require("VoiceOverrides = NormalizeVoiceOverrides(project.VoiceOverrides)" in save,
        "VOICE-10 save must normalize overrides before writing JSON")

normalize = project_store.split(
    "private static IReadOnlyDictionary<string, string> NormalizeVoiceOverrides(", 1
)[1].split("private static bool FileShaMatches", 1)[0]
require('var voice = pair.Value?.Trim().ToLowerInvariant() ?? string.Empty;' in normalize,
        "VOICE-10 persisted override values must be canonical lowercase")
require('voice is not ("male" or "female")' in normalize,
        "VOICE-10 project store must reject every persisted override except male/female")
require("result[id] = voice;" in normalize,
        "VOICE-10 valid normalized cue overrides must survive reopen")
require('"auto"' not in normalize,
        "VOICE-10 Auto must never be persisted as a voice value")

generate = editor.split("private async void GenerateTts_Click(", 1)[1].split(
    "private async Task PollTtsJobAsync()", 1
)[0]
require("_project.VoiceOverrides));" in generate,
        "VOICE-10 Generate voice must pass the current project overrides to TTS")

select_voice = local_tts.split("private static (string Voice, bool Review) SelectVoice(", 1)[1].split(
    "private static string CacheKey", 1
)[0]
manual_pos = select_voice.find("overrides.TryGetValue(cueId, out var manual)")
heuristic_pos = select_voice.find("return timing.VoiceClass switch")
require(manual_pos >= 0 and heuristic_pos > manual_pos,
        "VOICE-10 manual override must be evaluated before Whisper heuristic routing")
require('if (normalized is "male" or "female") return (normalized, false);' in select_voice,
        "VOICE-10 valid manual Nam/Nữ must win directly and not be marked for review")

cache_key = local_tts.split("private static string CacheKey(", 1)[1].split(
    "private async Task<TtsWorkerResult> ReadResultAsync", 1
)[0]
require("\\n{voice}\\n" in cache_key,
        "VOICE-10 TTS clip cache identity must include the resolved Nam/Nữ route")

refresh = editor.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1
)[0]
require('CurrentCueVoiceBox.IsEnabled = editable && _subtitleSource is not null && _project?.Speech is { Status: "complete" };' in refresh,
        "VOICE-10 selector availability must remain tied to editable source + completed Whisper analysis")

# Small state fixture: Auto is absence, not a third persisted voice.
overrides: dict[str, str] = {}
cue_id = "cue-00000001"
overrides[cue_id] = "male"
require(overrides[cue_id] == "male", "VOICE-10 fixture failed to persist Nam")
overrides[cue_id] = "female"
require(overrides[cue_id] == "female", "VOICE-10 fixture failed to replace Nam with Nữ")
overrides.pop(cue_id, None)
require(cue_id not in overrides, "VOICE-10 fixture Auto must remove the override")

print("PASS: VOICE-10 cue Auto/Nam/Nữ override contract")

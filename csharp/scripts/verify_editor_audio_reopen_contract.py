#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
EDITOR = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
STORE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


require("EditorAudioSettings? Audio = null," in STORE,
        "AUDIO-REOPEN project no longer persists source gain")
snapshot = EDITOR.split("private EditorProject ProjectSnapshot()", 1)[1].split(
    "private void RefreshEditorActions()", 1
)[0]
require("Audio = _audioSettings," in snapshot,
        "AUDIO-REOPEN snapshot lost source gain")

open_video = EDITOR.split("private async Task OpenVideoAsync()", 1)[1].split(
    "private async Task SaveCurrentSourceStateForSwitchAsync", 1
)[0]
for token in (
    "_audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);",
    "ApplyAudioSettingsToUi();",
):
    require(token in open_video, f"AUDIO-REOPEN load path lost: {token}")
require(open_video.index("_audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);")
        < open_video.index("ApplyAudioSettingsToUi();"),
        "AUDIO-REOPEN UI was synchronized before project audio was hydrated")

apply_audio = EDITOR.split("private void ApplyAudioSettingsToUi()", 1)[1].split(
    "private EditorSubtitleBurn? CompletedSubtitleBurn", 1
)[0]
require("SourceAudioGainSlider.Value = _audioSettings.SourceGain * 100;" in apply_audio,
        "AUDIO-REOPEN exact gain was not restored to the direct slider")
require("SourceAudioModeBox" not in apply_audio,
        "AUDIO-REOPEN obsolete mode UI returned")

load = STORE.split("public async Task<EditorProject> LoadOrCreateAsync(", 1)[1].split(
    "private static EditorProject CreateFreshProject", 1
)[0]
save = STORE.split("public async Task SaveAsync(EditorProject project", 1)[1].split(
    "public string GetProjectPath", 1
)[0]
require("Audio = NormalizeAudio(loaded.Audio)," in load,
        "AUDIO-REOPEN loaded gain is not normalized")
require("Audio = NormalizeAudio(project.Audio)," in save,
        "AUDIO-REOPEN saved gain is not canonicalized")
require('public static EditorAudioSettings Default { get; } = new("keep", 1);' in STORE,
        "AUDIO-REOPEN legacy project no longer defaults to 100%")

print("PASS: AUDIO-REOPEN direct source gain survives project save and reopen")

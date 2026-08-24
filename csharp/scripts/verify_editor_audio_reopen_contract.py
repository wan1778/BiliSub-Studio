#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR_MAIN = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
APPLICATION = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor_main = read(EDITOR_MAIN)
application = read(APPLICATION)
project_store = read(PROJECT_STORE)

# AUDIO-09 — Reopening the same Editor source/project must restore the persisted
# source-audio policy. The project store owns persistence; EditorPage hydrates
# _audioSettings first and only then synchronizes the Details controls.

require("EditorAudioSettings? Audio = null," in project_store,
        "AUDIO-09 EditorProject must persist an Audio field")

project_snapshot = editor_main.split("private EditorProject ProjectSnapshot()", 1)[1].split(
    "private void RefreshEditorActions()", 1)[0]
require("Audio = _audioSettings," in project_snapshot,
        "AUDIO-09 ProjectSnapshot must persist the current source-audio owner")

audio_update = editor_main.split("private void UpdateAudioSettingsFromUi()", 1)[1].split(
    "private void ApplyAudioSettingsToUi()", 1)[0]
require("QueueProjectSave();" in audio_update,
        "AUDIO-09 changing Keep/Duck/Mute or Duck gain must queue project persistence")

queue_save = editor_main.split("private void QueueProjectSave()", 1)[1].split(
    "private async Task SaveProjectLaterAsync", 1)[0]
require("var snapshot = ProjectSnapshot();" in queue_save
        and "SaveProjectLaterAsync(snapshot, cancellation.Token)" in queue_save,
        "AUDIO-09 autosave must capture a project snapshot containing current audio state")

save_later = editor_main.split("private async Task SaveProjectLaterAsync", 1)[1].split(
    "private async Task SaveProjectNowAsync()", 1)[0]
require("_application.SaveEditorProjectAsync(project, cancellationToken)" in save_later,
        "AUDIO-09 queued project snapshot must reach the application persistence owner")

save_now = editor_main.split("private async Task SaveProjectNowAsync()", 1)[1].split(
    "private EditorProject ProjectSnapshot()", 1)[0]
require("_application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None)" in save_now,
        "AUDIO-09 explicit save must persist the latest audio snapshot")

unload = editor_main.split("private async void EditorPage_Unloaded", 1)[1].split(
    "private async void OpenVideo_Click", 1)[0]
require("await SaveProjectNowAsync();" in unload,
        "AUDIO-09 unloading Editor must flush the final audio state")

open_video = editor_main.split("private async Task OpenVideoAsync()", 1)[1].split(
    "private async Task SaveCurrentSourceStateForSwitchAsync", 1)[0]
require("await SaveCurrentSourceStateForSwitchAsync();" in open_video,
        "AUDIO-09 switching source must save the current project first")
require(open_video.index("await SaveCurrentSourceStateForSwitchAsync();")
        < open_video.index("_project = candidateProject;"),
        "AUDIO-09 old project must be saved before replacing the active project owner")
require("_project = candidateProject;" in open_video
        and "_audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);" in open_video
        and "ApplyAudioSettingsToUi();" in open_video,
        "AUDIO-09 reopening must hydrate project Audio into the Editor audio owner and UI")
require(open_video.index("_project = candidateProject;")
        < open_video.index("_audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);")
        < open_video.index("ApplyAudioSettingsToUi();"),
        "AUDIO-09 reopen order must be project -> normalized audio owner -> UI")

same_source_guard = open_video.split(
    "if (EditorSourceSelection.IsSameSource(_path, candidatePath))", 1)[1].split(
    "// SOURCE-05:", 1)[0]
require("return;" in same_source_guard,
        "AUDIO-09 selecting the already-open source must be a no-op, not reset audio state")

save_switch = editor_main.split("private async Task SaveCurrentSourceStateForSwitchAsync()", 1)[1].split(
    "private async Task DisposePreviewForSourceChangeAsync", 1)[0]
require("_application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None)" in save_switch,
        "AUDIO-09 source switch must persist the current audio snapshot before disposal")

apply_audio = editor_main.split("private void ApplyAudioSettingsToUi()", 1)[1].split(
    "private EditorSubtitleBurn? CompletedSubtitleBurn", 1)[0]
require("_syncingAudio = true;" in apply_audio
        and "finally { _syncingAudio = false; }" in apply_audio,
        "AUDIO-09 owner-to-UI audio restore must remain guarded from event feedback")
require("SourceAudioModeBox.SelectedIndex = index;" in apply_audio,
        "AUDIO-09 reopen must restore Keep/Duck/Mute selection into Details")
require('if (_audioSettings.SourceMode == "duck") SourceAudioGainSlider.Value = _audioSettings.SourceGain * 100;' in apply_audio,
        "AUDIO-09 reopen must restore the exact persisted Duck gain")
require("if (_syncingAudio || !IsLoaded) return;" in audio_update,
        "AUDIO-09 programmatic UI restore must not overwrite the loaded audio owner")

load_or_create = project_store.split("public async Task<EditorProject> LoadOrCreateAsync(", 1)[1].split(
    "private static EditorProject CreateFreshProject", 1)[0]
require("Audio = NormalizeAudio(loaded.Audio)," in load_or_create,
        "AUDIO-09 loaded project Audio must be normalized before returning to EditorPage")

save_async = project_store.split("public async Task SaveAsync(EditorProject project", 1)[1].split(
    "public string GetProjectPath", 1)[0]
require("Audio = NormalizeAudio(project.Audio)," in save_async,
        "AUDIO-09 persisted project Audio must be canonicalized before JSON serialization")
require("JsonSerializer.SerializeAsync(stream, normalized, _json, cancellationToken)" in save_async,
        "AUDIO-09 canonicalized audio state must be the object serialized to disk")

normalize_audio = project_store.split("public static EditorAudioSettings NormalizeAudio", 1)[1].split(
    "private static EditorAsrProject? NormalizeAsr", 1)[0]
require('"keep" => new EditorAudioSettings("keep", 1),' in normalize_audio
        and '"mute" => new EditorAudioSettings("mute", 0),' in normalize_audio
        and 'new EditorAudioSettings("duck", Math.Clamp(audio.SourceGain, .05, .95))' in normalize_audio,
        "AUDIO-09 reopen must preserve canonical Keep/Duck/Mute semantics")
require('public static EditorAudioSettings Default { get; } = new("keep", 1);' in project_store,
        "AUDIO-09 legacy/null Audio must safely reopen as Keep at unity gain")

require("_editorProjects.LoadOrCreateAsync(path, media.Width, media.Height, media.Duration, cancellationToken)" in application,
        "AUDIO-09 application load wrapper must delegate directly to EditorProjectStore")
require("_editorProjects.SaveAsync(project, cancellationToken)" in application,
        "AUDIO-09 application save wrapper must delegate directly to EditorProjectStore")

print("PASS: AUDIO-09 reopen project preserves source-audio settings")

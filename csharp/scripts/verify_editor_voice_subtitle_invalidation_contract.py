#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
CUE_EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
EVENT_MAP = ROOT / "docs/engineering/EDITOR_EVENT_MAP.md"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor = read(EDITOR)
cue_editor = read(CUE_EDITOR)
project_store = read(PROJECT_STORE)
event_map = read(EVENT_MAP)

# VOICE-11 — replacing/changing subtitle content must invalidate any generated
# voice whose text/timing source is no longer trustworthy. Presentation-only
# subtitle changes (position/karaoke) do not alter TTS audio inputs.

for row in (
    "| `ImportSrtButton` | XAML | `ImportSubtitle_Click` | 1 |",
    "| `TranslateButton` | XAML | `Translate_Click` | 1 |",
    "| `SubtitleSaveCueButton` | XAML | `SubtitleSaveCue_Click` | 1 |",
    "| `SubtitleRetranslateCueButton` | XAML | `SubtitleRetranslateCue_Click` | 1 |",
):
    require(row in event_map, f"VOICE-11 event owner changed: {row}")

load = project_store.split("public async Task<EditorProject> LoadOrCreateAsync(", 1)[1].split(
    "private static EditorProject CreateFreshProject", 1
)[0]
require("var subtitle = NormalizeSubtitle(loaded.Subtitle);" in load,
        "VOICE-11 reopen must normalize saved subtitle before TTS")
require("Subtitle = subtitle," in load,
        "VOICE-11 reopened project must use the normalized subtitle")
require("Tts = subtitle is null ? null : NormalizeTts(loaded.Tts)," in load,
        "VOICE-11 reopened TTS must be impossible without a valid matching subtitle")
require(load.index("var subtitle = NormalizeSubtitle(loaded.Subtitle);")
        < load.index("Tts = subtitle is null ? null : NormalizeTts(loaded.Tts),"),
        "VOICE-11 subtitle identity must be decided before TTS restoration")

save = project_store.split("public async Task SaveAsync(", 1)[1].split(
    "public string GetProjectPath", 1
)[0]
require("var subtitle = NormalizeSubtitle(project.Subtitle);" in save,
        "VOICE-11 save must normalize subtitle before persisting TTS")
require("Subtitle = subtitle," in save,
        "VOICE-11 save must persist only the validated subtitle")
require("Tts = subtitle is null ? null : NormalizeTts(project.Tts)," in save,
        "VOICE-11 save must repair legacy/inconsistent TTS-without-subtitle state")

normalize_subtitle = project_store.split(
    "private static EditorSubtitleProject? NormalizeSubtitle(", 1
)[1].split("public static EditorAudioSettings NormalizeAudio", 1)[0]
require("subtitle.SourceSize <= 0" in normalize_subtitle
        and "subtitle.SourceLastWriteUtcTicks <= 0" in normalize_subtitle
        and "subtitle.SourceSha256.Length != 64" in normalize_subtitle,
        "VOICE-11 stored subtitle fingerprint fields must remain structurally validated")
require("var info = new FileInfo(path);" in normalize_subtitle,
        "VOICE-11 source SRT validity must be checked against the current file")
require("if (!info.Exists || info.Length != subtitle.SourceSize || !FileShaMatches(path, subtitle.SourceSha256)) return null;"
        in normalize_subtitle,
        "VOICE-11 missing/changed SRT source must invalidate saved subtitle state")
require("SourcePath = path," in normalize_subtitle,
        "VOICE-11 unchanged matching subtitle must still normalize and survive reopen")

import_subtitle = editor.split("private async Task ImportSubtitleAsync()", 1)[1].split(
    "private void AttachSubtitleToProject", 1
)[0]
require("_voiceTrack = null;" in import_subtitle
        and "_project = _project with { Tts = null };" in import_subtitle,
        "VOICE-11 importing/replacing SRT in the UI must invalidate old voice immediately")
require("AttachSubtitleToProject(string.Empty);" in import_subtitle,
        "VOICE-11 replacement SRT must attach only after stale TTS is cleared")

translate_all = cue_editor.split("private async Task TranslateAllWithManualStateAsync()", 1)[1].split(
    "private async void SubtitleRetranslateCue_Click", 1
)[0]
apply = translate_all.split("_subtitleSource = _subtitleSource with { Cues = merged };", 1)
require(len(apply) == 2,
        "VOICE-11 full Vietsub result must replace cue text")
require("_voiceTrack = null;" in apply[1]
        and "_project = _project with { Tts = null };" in apply[1],
        "VOICE-11 successful full Vietsub must invalidate old voice after applying new text")

manual = cue_editor.split("private async Task SaveCurrentSubtitleCueAsync()", 1)[1].split(
    "private void MarkTranslatedOutputStale()", 1
)[0]
require("MarkTranslatedOutputStale();" in manual,
        "VOICE-11 saved manual cue edits must invalidate the old generated voice")

stale = cue_editor.split("private void MarkTranslatedOutputStale()", 1)[1].split(
    "private async Task TranslateAllWithManualStateAsync()", 1
)[0]
require("_voiceTrack = null;" in stale and "Tts = null," in stale,
        "VOICE-11 shared stale subtitle-output owner must clear runtime and persisted TTS")
require("Cues = _subtitleSource?.Cues ?? _project.Subtitle.Cues" in stale,
        "VOICE-11 stale-output owner must persist the changed cue content")

retranslate = cue_editor.split("private async Task RetranslateSelectedCueAsync()", 1)[1].split(
    "private async void SubtitleSaveSrt_Click", 1
)[0]
require("MarkTranslatedOutputStale();" in retranslate,
        "VOICE-11 successful single-cue retranslation must invalidate old voice")

compat_translation = editor.split("private async Task PollTranslationJobAsync(bool preparing)", 1)[1].split(
    "private async void SaveKaraokeAss_Click", 1
)[0]
require("_subtitleSource = _subtitleSource with { Cues = result.Cues };" in compat_translation
        and "_voiceTrack = null;" in compat_translation
        and "Tts = null," in compat_translation,
        "VOICE-11 compatibility translation promotion path must invalidate stale voice too")

asr = editor.split("private async Task PollAsrJobAsync()", 1)[1].split(
    "private async void GenerateTts_Click", 1
)[0]
require("_voiceTrack = null;" in asr and "Tts = null," in asr,
        "VOICE-11 refreshed ASR/subtitle analysis must not retain a previous TTS track")

# Subtitle placement and karaoke styling do not change the spoken Vietnamese text.
karaoke = editor.split("private void Karaoke_Toggled(", 1)[1].split(
    "private void CurrentCueVoice_SelectionChanged", 1
)[0]
for forbidden in ("_voiceTrack = null", "Tts = null"):
    require(forbidden not in karaoke,
            f"VOICE-11 karaoke presentation-only change must preserve voice: {forbidden}")

finish_drag = editor.split("private void FinishDrag(", 1)[1].split(
    "private bool TryCommitCreatedRegion", 1
)[0]
subtitle_drag = finish_drag.split("if (_subtitleDrag)", 1)[1].split(
    "if (!commit && _dragHistoryCaptured)", 1
)[0]
for forbidden in ("_voiceTrack = null", "Tts = null"):
    require(forbidden not in subtitle_drag,
            f"VOICE-11 caption placement-only change must preserve voice: {forbidden}")

current_edit = editor.split("private VideoEditRequest CurrentEditRequest(", 1)[1].split(
    "private async void Render_Click", 1
)[0]
require("_voiceTrack);" in current_edit,
        "VOICE-11 Preview/Export share the runtime voice track, so stale invalidation must clear that owner")

# Small persistence fixture: an unchanged subtitle keeps TTS; missing/changed
# subtitle removes both subtitle ownership and TTS ownership.
def normalize_subtitle(exists: bool, same_size: bool, same_sha: bool) -> bool:
    return exists and same_size and same_sha


def normalize_project(exists: bool, same_size: bool, same_sha: bool, tts: str | None) -> tuple[bool, str | None]:
    subtitle_ok = normalize_subtitle(exists, same_size, same_sha)
    return subtitle_ok, tts if subtitle_ok else None


require(normalize_project(True, True, True, "voice") == (True, "voice"),
        "VOICE-11 fixture: unchanged SRT must keep valid generated voice")
require(normalize_project(True, True, False, "voice") == (False, None),
        "VOICE-11 fixture: same-size but changed SRT content must invalidate voice")
require(normalize_project(True, False, False, "voice") == (False, None),
        "VOICE-11 fixture: changed SRT size must invalidate voice")
require(normalize_project(False, False, False, "voice") == (False, None),
        "VOICE-11 fixture: missing SRT must invalidate voice")
require(normalize_project(False, False, False, None) == (False, None),
        "VOICE-11 fixture: no subtitle/no TTS remains stable")

print("PASS: VOICE-11 subtitle replacement invalidates stale voice contract")

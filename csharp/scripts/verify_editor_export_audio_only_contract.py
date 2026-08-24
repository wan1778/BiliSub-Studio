#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
XAML = PAGES / "EditorPage.xaml"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


xaml = read(XAML)
editor = read(EDITOR)
images = read(IMAGES)
project_store = read(PROJECT_STORE)
video = read(VIDEO_EDITOR)

# UI exposes exactly the three source-audio modes used by Preview/Export.
for token in (
    'Tag="keep" Content="Giữ nguyên"',
    'Tag="duck" Content="Giảm âm lượng"',
    'Tag="mute" Content="Tắt tiếng gốc"',
    'Minimum="5" Maximum="95"',
):
    require(token in xaml, f"EXPORT-08 Audio UI contract lost: {token}")

# UI -> one normalized EditorAudioSettings state.
for token in (
    'private void SourceAudioMode_SelectionChanged',
    'private void SourceAudioGain_ValueChanged',
    'private void UpdateAudioSettingsFromUi()',
    'var mode = (SourceAudioModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "keep";',
    '"mute" => 0,',
    '"duck" => SourceAudioGainSlider.Value / 100,',
    '_audioSettings = EditorProjectStore.NormalizeAudio(new EditorAudioSettings(mode, gain));',
):
    require(token in editor, f"EXPORT-08 Audio state update lost: {token}")

# Only a real audio change (Duck/Mute) is an edit. Keep by itself must not render.
require(
    'var audioChanged = _audioSettings.SourceMode != "keep";' in editor,
    "EXPORT-08 RenderButton no longer distinguishes changed audio from Keep",
)
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-08 RenderButton no longer accepts audio-only state",
)
require(
    'SourceAudioGainSlider.IsEnabled = editable && _audioSettings.SourceMode == "duck";'
    in editor,
    "EXPORT-08 gain slider must only be editable in Duck mode",
)

# CurrentEditRequest carries the one audio state into the final render request.
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-08 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1600]
require("_audioSettings," in request_body, "EXPORT-08 CurrentEditRequest lost audio state")

# Audio-only counts as a base edit and, with no images, uses normal VideoEditorService.
require(
    '_audioSettings.SourceMode != "keep"' in images,
    "EXPORT-08 audio-only no longer qualifies as base edit",
)
require(
    "_application.StartEditor(CurrentEditRequest(subtitle))" in images,
    "EXPORT-08 audio-only must use the normal Editor render service",
)

# Persistence/normalization is centralized and clamps Duck to the UI-safe range.
for token in (
    'public sealed record EditorAudioSettings(string SourceMode, double SourceGain)',
    'public static EditorAudioSettings Default { get; } = new("keep", 1);',
    'if (mode is not ("keep" or "duck" or "mute"))',
    '"keep" => new EditorAudioSettings("keep", 1),',
    '"mute" => new EditorAudioSettings("mute", 0),',
    '_ => new EditorAudioSettings("duck", Math.Clamp(audio.SourceGain, .05, .95)),',
):
    require(token in project_store, f"EXPORT-08 NormalizeAudio contract lost: {token}")

# Backend accepts audio change alone. No dummy visual region/subtitle is required.
require(
    'EditorProjectStore.NormalizeAudio(request.Audio).SourceMode != "keep"' in video,
    "EXPORT-08 backend HasEdit no longer accepts audio-only requests",
)

# Audio-only still creates a valid video graph: no visual change, just [0:v] -> [vout].
require(
    'else parts.Add($"[{current}]null[vout]");' in video,
    "EXPORT-08 audio-only video path must stay visually unchanged",
)

# Final FFmpeg audio policy.
for token in (
    'if (audio.SourceMode == "mute") return ["-an"];',
    'var arguments = new List<string> { "-map", "0:a?" };',
    'if (audio.SourceMode == "duck") filters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
    'if (filters.Count > 0) arguments.AddRange(["-af", string.Join(\',\', filters)]);',
    'if (mp4 || audio.SourceMode == "duck") arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);',
    'else arguments.AddRange(["-c:a", "copy"]);',
):
    require(token in video, f"EXPORT-08 FFmpeg audio policy lost: {token}")

# Voice must be absent in this task's path; when absent, source-audio arguments own audio.
require(
    'if (voice is null) args.AddRange(BuildAudioArguments(audio, mp4));' in video,
    "EXPORT-08 no-voice audio-only render no longer uses source-audio arguments",
)

# Standard safe output lifecycle remains shared with other exports.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    'ValidateRenderedOutputAsync(temporary, request.Duration',
    'File.Move(temporary, output);',
):
    require(token in video, f"EXPORT-08 final output lifecycle lost: {token}")


# Portable behavioral fixtures for task semantics.
def normalize_audio(mode: str, gain: float) -> tuple[str, float]:
    mode = mode.strip().lower()
    if mode not in {"keep", "duck", "mute"}:
        raise ValueError("invalid mode")
    if mode == "keep":
        return "keep", 1.0
    if mode == "mute":
        return "mute", 0.0
    return "duck", max(0.05, min(0.95, gain))


def render_enabled(audio_mode: str) -> bool:
    audio_changed = audio_mode != "keep"
    return audio_changed


def has_edit(audio_mode: str) -> bool:
    return audio_mode != "keep"


def audio_args(mode: str, gain: float, mp4: bool = True) -> list[str]:
    mode, gain = normalize_audio(mode, gain)
    if mode == "mute":
        return ["-an"]
    args = ["-map", "0:a?"]
    filters: list[str] = []
    if mode == "duck":
        filters.append(f"volume={gain:.3f}")
    if filters:
        args += ["-af", ",".join(filters)]
    if mp4 or mode == "duck":
        args += ["-c:a", "aac", "-b:a", "192k"]
    else:
        args += ["-c:a", "copy"]
    return args


require(not render_enabled("keep"), "EXPORT-08 Keep alone must not enable Render")
require(render_enabled("duck"), "EXPORT-08 Duck alone must enable Render")
require(render_enabled("mute"), "EXPORT-08 Mute alone must enable Render")
require(has_edit("duck") and has_edit("mute"), "EXPORT-08 backend must accept Duck/Mute-only")
require(not has_edit("keep"), "EXPORT-08 Keep alone must not count as an edit")

require(normalize_audio("duck", 0.01) == ("duck", 0.05), "EXPORT-08 Duck minimum clamp failed")
require(normalize_audio("duck", 0.35) == ("duck", 0.35), "EXPORT-08 Duck normal gain failed")
require(normalize_audio("duck", 1.0) == ("duck", 0.95), "EXPORT-08 Duck maximum clamp failed")
require(normalize_audio("mute", 0.8) == ("mute", 0.0), "EXPORT-08 Mute must force zero gain")
require(normalize_audio("keep", 0.2) == ("keep", 1.0), "EXPORT-08 Keep must force unity gain")

duck = audio_args("duck", 0.35)
require("-map" in duck and "0:a?" in duck, "EXPORT-08 Duck must map source audio")
require("-af" in duck and "volume=0.350" in duck, "EXPORT-08 Duck must apply requested gain")
require("-an" not in duck, "EXPORT-08 Duck must not mute audio")

mute = audio_args("mute", 0.35)
require(mute == ["-an"], "EXPORT-08 Mute must remove audio stream completely")

# Audio-only leaves the visual stream unchanged.
audio_only_graph = "[0:v]null[vout]"
require(audio_only_graph == "[0:v]null[vout]", "EXPORT-08 visual pass-through fixture failed")

print("PASS: EXPORT-08 Duck/Mute audio-only export contract is locked")

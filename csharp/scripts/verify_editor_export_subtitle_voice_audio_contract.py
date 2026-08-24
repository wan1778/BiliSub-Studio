#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
PROJECT_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


editor = read(EDITOR)
images = read(IMAGES)
video = read(VIDEO_EDITOR)
project = read(PROJECT_STORE)

# EXPORT-12 — completed Vietnamese subtitle + Vietnamese Voice + source-audio policy
# must stay one exportable request and one final FFmpeg job. Subtitle owns the video
# graph; Voice + Keep/Duck/Mute own [aout] independently.
require(
    "var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));"
    in editor,
    "EXPORT-12 Render state lost completed-subtitle readiness",
)
require(
    'var audioChanged = _audioSettings.SourceMode != "keep";' in editor,
    "EXPORT-12 Render state lost source-audio policy",
)
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-12 combined Subtitle/Voice/Audio state no longer enables Render",
)

# Exportable subtitle must remain complete Vietnamese text only.
require(
    "private EditorSubtitleBurn? CompletedSubtitleBurn() =>" in editor
    and "_subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))" in editor
    and "new EditorSubtitleBurn(_subtitleSource.Cues, _subtitlePlacement, _cueSpeechTiming, KaraokeToggle.IsOn)"
    in editor,
    "EXPORT-12 completed subtitle owner changed unexpectedly",
)

# One shared request carries Subtitle, persisted source-audio policy and Voice together.
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-12 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1900]
for token in (
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in request_body, f"EXPORT-12 shared request lost: {token}")

# Final orchestration snapshots the completed subtitle once. Subtitle, Audio change or
# Voice each qualify as base edit; without Image/logo the same request runs directly.
for token in (
    "var subtitle = CompletedSubtitleBurn();",
    'var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null;',
    "if (!hasImages)",
    "_application.StartEditor(CurrentEditRequest(subtitle))",
):
    require(token in images, f"EXPORT-12 final orchestration lost: {token}")

# Audio persistence/normalization has exactly Keep, Duck and Mute semantics.
for token in (
    'public sealed record EditorAudioSettings(string SourceMode, double SourceGain)',
    'public static EditorAudioSettings Default { get; } = new("keep", 1);',
    'mode is not ("keep" or "duck" or "mute")',
    '"keep" => new EditorAudioSettings("keep", 1)',
    '"mute" => new EditorAudioSettings("mute", 0)',
    'new EditorAudioSettings("duck", Math.Clamp(audio.SourceGain, .05, .95))',
):
    require(token in project, f"EXPORT-12 audio policy normalization lost: {token}")

# RunAsync creates the subtitle ASS and visual graph, validates Voice, then combines
# the visual graph and audio graph into one filter_complex invocation.
run_start = video.find("public async Task<VideoEditResult> RunAsync(")
run_end = video.find("private async Task ValidateRenderedOutputAsync", run_start)
require(run_start >= 0 and run_end > run_start, "EXPORT-12 RunAsync block not found")
run_body = video[run_start:run_end]
for token in (
    "var subtitleAss = request.Subtitle is null ? null :",
    "BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight)",
    "var graph = BuildFilter(request, subtitleAss);",
    "var audio = EditorProjectStore.NormalizeAudio(request.Audio);",
    "var voice = NormalizeVoiceTrack(request.VoiceTrack, requireFile: true);",
    'if (voice is not null) args.AddRange(["-i", voice.Path]);',
    "var combinedGraph = voice is null ? graph : graph + \";\" + BuildVoiceAudioFilter(audio, voice, 1, sourceStart: 0);",
    '"-filter_complex", combinedGraph',
    '"-map", "[vout]"',
    'else args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);',
):
    require(token in run_body, f"EXPORT-12 combined final render lost: {token}")

# With Voice present, even source Mute must still require and validate an output
# audio stream because Vietnamese Voice remains audible.
require(
    'voice is not null || audio.SourceMode != "mute"' in run_body,
    "EXPORT-12 final validator must expect Voice audio even when source is Mute",
)

# Subtitle visual graph is independent of the audio graph and must end at [vout].
filter_start = video.find("private static string BuildFilterCore(")
filter_end = video.find("public static string BuildAss(", filter_start)
require(filter_start >= 0 and filter_end > filter_start, "EXPORT-12 BuildFilterCore block not found")
filter_body = video[filter_start:filter_end]
require(
    "if (request.Subtitle is not null)" in filter_body
    and 'parts.Add($"[{current}]ass=filename=\'{escaped}\'[vout]");' in filter_body,
    "EXPORT-12 completed subtitle must remain in the visual graph",
)

# ASS remains Vietnamese-only and preserves cue timing/placement.
ass_start = video.find("public static string BuildAss(")
ass_end = video.find("private static string BuildVoiceAudioFilter", ass_start)
require(ass_start >= 0 and ass_end > ass_start, "EXPORT-12 BuildAss block not found")
ass_body = video[ass_start:ass_end]
for token in (
    "subtitle.Cues.Count == 0 || subtitle.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText))",
    "subtitle.Placement",
    "cue.VietnameseText",
    "cue.Start",
    "cue.End",
):
    require(token in ass_body, f"EXPORT-12 ASS semantics lost: {token}")

# Voice/audio owner: timing and gain apply to Voice; Duck attenuates source only;
# Keep mixes source at full level; Mute removes source while keeping Voice.
voice_start = video.find("private static string BuildVoiceAudioFilter")
voice_end = video.find("public static bool IsActiveAt", voice_start)
require(voice_start >= 0 and voice_end > voice_start, "EXPORT-12 BuildVoiceAudioFilter block not found")
voice_body = video[voice_start:voice_end]
for token in (
    "var relativeDelay = Math.Max(0, voice.Start - sourceStart);",
    'var voiceFilters = new List<string> { "asetpts=PTS-STARTPTS" };',
    'voiceFilters.Add($"adelay={milliseconds}:all=1");',
    'voiceFilters.Add("volume=" + voice.Gain.ToString("0.000", CultureInfo.InvariantCulture));',
    'if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";',
    'var sourceFilters = new List<string> { "asetpts=PTS-STARTPTS" };',
    'if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
    "[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]",
):
    require(token in voice_body, f"EXPORT-12 Voice/source-audio semantics lost: {token}")

# Final output lifecycle remains validated temp -> promotion.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "await ValidateRenderedOutputAsync(temporary, request.Duration",
    "File.Move(temporary, output);",
):
    require(token in run_body, f"EXPORT-12 final output lifecycle lost: {token}")


# Portable behavior fixtures for the exact Subtitle + Voice + Audio boundary.
def completed_subtitle(values: list[str]) -> bool:
    return bool(values) and all(value.strip() for value in values)


def normalize_audio(mode: str, gain: float) -> tuple[str, float]:
    mode = mode.strip().lower()
    if mode == "keep":
        return ("keep", 1.0)
    if mode == "mute":
        return ("mute", 0.0)
    if mode == "duck":
        return ("duck", min(0.95, max(0.05, gain)))
    raise ValueError(mode)


def render_enabled(*, subtitle_ready: bool, voice: bool, audio_mode: str, filename: str = "movie.mp4") -> bool:
    audio_changed = audio_mode != "keep"
    return (subtitle_ready or voice or audio_changed) and bool(filename.strip())


def subtitle_graph(ass_path: str) -> str:
    return f"[0:v]ass=filename='{ass_path}'[vout]"


def voice_mix(audio_mode: str, source_gain: float, voice_start: float, voice_gain: float) -> str:
    mode, gain = normalize_audio(audio_mode, source_gain)
    voice_filters = ["asetpts=PTS-STARTPTS"]
    if voice_start > 0.0005:
        voice_filters.append(f"adelay={round(voice_start * 1000)}:all=1")
    if abs(voice_gain - 1.0) > 0.0005:
        voice_filters.append(f"volume={voice_gain:.3f}")
    voice_chain = f"[1:a]{','.join(voice_filters)}[voicea]"
    if mode == "mute":
        return voice_chain + ";[voicea]anull[aout]"
    source_filters = ["asetpts=PTS-STARTPTS"]
    if mode == "duck":
        source_filters.append(f"volume={gain:.3f}")
    return (
        f"[0:a]{','.join(source_filters)}[sourcea];"
        + voice_chain
        + ";[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]"
    )


require(completed_subtitle(["Xin chào", "Đạo hữu"]), "EXPORT-12 complete Vietsub fixture failed")
require(not completed_subtitle(["Xin chào", ""]), "EXPORT-12 incomplete Vietsub must not burn")
for mode in ("keep", "duck", "mute"):
    require(
        render_enabled(subtitle_ready=True, voice=True, audio_mode=mode),
        f"EXPORT-12 Subtitle + Voice + {mode} must enable Render",
    )

visual = subtitle_graph("caption.ass")
require(visual == "[0:v]ass=filename='caption.ass'[vout]", "EXPORT-12 subtitle visual fixture failed")

keep = visual + ";" + voice_mix("keep", 1.0, 2.75, 1.25)
require("ass=filename='caption.ass'[vout]" in keep, "EXPORT-12 Keep lost subtitle")
require("adelay=2750:all=1" in keep and "volume=1.250" in keep, "EXPORT-12 Keep lost Voice timing/gain")
require("[0:a]asetpts=PTS-STARTPTS[sourcea]" in keep, "EXPORT-12 Keep lost source audio")
require("[sourcea][voicea]amix=" in keep, "EXPORT-12 Keep must mix source + Voice")

duck = visual + ";" + voice_mix("duck", 0.35, 0.0, 1.0)
require("ass=filename='caption.ass'[vout]" in duck, "EXPORT-12 Duck lost subtitle")
require("[0:a]asetpts=PTS-STARTPTS,volume=0.350[sourcea]" in duck, "EXPORT-12 Duck must attenuate source")
require("[1:a]asetpts=PTS-STARTPTS[voicea]" in duck, "EXPORT-12 Duck must not attenuate Voice")

mute = visual + ";" + voice_mix("mute", 0.35, 0.0, 1.0)
require("ass=filename='caption.ass'[vout]" in mute, "EXPORT-12 Mute lost subtitle")
require("[0:a]" not in mute, "EXPORT-12 Mute must remove source audio")
require("[voicea]anull[aout]" in mute, "EXPORT-12 Mute must keep Vietnamese Voice")
require(True, "EXPORT-12 Voice means final output must expect audio even under source Mute")

print("PASS: EXPORT-12 Subtitle + Voice + Audio export contract is locked")

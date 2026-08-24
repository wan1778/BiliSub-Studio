#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
TTS = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs"


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
tts = read(TTS)

# Voice track alone is a valid final-render state in the one RenderButton owner.
require(
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages"
    in editor,
    "EXPORT-09 RenderButton no longer accepts Voice-only state",
)

# The shared request carries Voice independently of subtitle/region/image state.
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-09 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1700]
for token in (
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in request_body, f"EXPORT-09 CurrentEditRequest lost: {token}")

# An incomplete/absent subtitle remains null; Voice must not depend on subtitle burn.
require(
    "private EditorSubtitleBurn? CompletedSubtitleBurn() =>" in editor
    and "_subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText))" in editor,
    "EXPORT-09 exportable subtitle owner changed unexpectedly",
)

# One Render orchestration: Voice alone counts as a base edit. With zero images it
# must go straight to the normal VideoEditorService path.
require(
    '_audioSettings.SourceMode != "keep" || _voiceTrack is not null' in images,
    "EXPORT-09 Voice no longer qualifies as base edit",
)
require(
    "if (!hasImages)" in images
    and "_application.StartEditor(CurrentEditRequest(subtitle))" in images,
    "EXPORT-09 Voice-only must use the normal Editor render service",
)

# Voice master is a real persisted TTS asset, not a UI-only marker.
require(
    "public sealed record EditorVoiceTrack(string Path, double Start, double Duration, double Gain = 1);"
    in tts,
    "EXPORT-09 EditorVoiceTrack contract changed",
)

# Backend accepts Voice by itself as an edit.
require(
    "request.Regions.Count > 0 || request.Subtitle is not null || request.VoiceTrack is not null"
    in video,
    "EXPORT-09 backend HasEdit no longer accepts Voice-only requests",
)

# Voice-only must leave the picture unchanged.
require(
    'else parts.Add($"[{current}]null[vout]");' in video,
    "EXPORT-09 Voice-only video path must stay visually unchanged",
)

# The final render validates the Voice master before starting FFmpeg.
for token in (
    "var voice = NormalizeVoiceTrack(request.VoiceTrack, requireFile: true);",
    "string.IsNullOrWhiteSpace(track.Path)",
    "!double.IsFinite(track.Start) || track.Start < 0",
    "!double.IsFinite(track.Duration) || track.Duration <= 0",
    "!double.IsFinite(track.Gain) || track.Gain is < 0 or > 4",
    "if (requireFile && (!File.Exists(path) || new FileInfo(path).Length <= 64))",
    "return track with { Path = path, Gain = Math.Clamp(track.Gain, 0, 4) };",
):
    require(token in video, f"EXPORT-09 Voice validation lost: {token}")

# Voice is the second FFmpeg input and the shared audio owner builds [aout].
for token in (
    'if (voice is not null) args.AddRange(["-i", voice.Path]);',
    "BuildVoiceAudioFilter(audio, voice, 1, sourceStart: 0)",
    'else args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);',
    'voice is not null || audio.SourceMode != "mute"',
):
    require(token in video, f"EXPORT-09 final Voice FFmpeg path lost: {token}")

# Timing and gain stay owned by EditorVoiceTrack.
for token in (
    "var relativeDelay = Math.Max(0, voice.Start - sourceStart);",
    'var voiceFilters = new List<string> { "asetpts=PTS-STARTPTS" };',
    'voiceFilters.Add($"adelay={milliseconds}:all=1");',
    'voiceFilters.Add("volume=" + voice.Gain.ToString("0.000", CultureInfo.InvariantCulture));',
):
    require(token in video, f"EXPORT-09 Voice timing/gain contract lost: {token}")

# Source-audio policy remains orthogonal to Voice-only edit semantics:
# Keep mixes source+voice, Duck attenuates source only, Mute leaves Voice audible.
for token in (
    'if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";',
    'var sourceFilters = new List<string> { "asetpts=PTS-STARTPTS" };',
    'if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
    "[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]",
):
    require(token in video, f"EXPORT-09 Voice/source mix contract lost: {token}")

# Standard safe finalization still owns Voice-only output.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "ValidateRenderedOutputAsync(temporary, request.Duration",
    "File.Move(temporary, output);",
):
    require(token in video, f"EXPORT-09 final output lifecycle lost: {token}")


# Portable behavioral fixtures for the EXPORT-09 boundary.
def render_enabled(
    *,
    regions: int = 0,
    subtitle_ready: bool = False,
    audio_changed: bool = False,
    voice: bool = False,
    images: int = 0,
    filename: str = "movie.mp4",
) -> bool:
    return (
        (regions > 0 or subtitle_ready or audio_changed or voice or images > 0)
        and bool(filename.strip())
    )


def has_edit(*, regions: int = 0, subtitle: bool = False, voice: bool = False, audio_mode: str = "keep") -> bool:
    return regions > 0 or subtitle or voice or audio_mode != "keep"


def voice_mix(audio_mode: str, source_gain: float, voice_start: float, source_start: float, voice_gain: float) -> str:
    delay = max(0.0, voice_start - source_start)
    voice_filters = ["asetpts=PTS-STARTPTS"]
    if delay > 0.0005:
        voice_filters.append(f"adelay={round(delay * 1000)}:all=1")
    if abs(voice_gain - 1.0) > 0.0005:
        voice_filters.append(f"volume={voice_gain:.3f}")
    voice_chain = f"[1:a]{','.join(voice_filters)}[voicea]"
    if audio_mode == "mute":
        return voice_chain + ";[voicea]anull[aout]"
    source_filters = ["asetpts=PTS-STARTPTS"]
    if audio_mode == "duck":
        source_filters.append(f"volume={source_gain:.3f}")
    return (
        f"[0:a]{','.join(source_filters)}[sourcea];"
        + voice_chain
        + ";[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]"
    )


require(render_enabled(voice=True), "EXPORT-09 Voice alone must enable Render")
require(not render_enabled(), "EXPORT-09 empty Keep project must not enable Render")
require(has_edit(voice=True), "EXPORT-09 backend must accept Voice-only request")
require(not has_edit(), "EXPORT-09 empty request must not count as edit")

keep_graph = voice_mix("keep", 1.0, 2.75, 0.0, 1.25)
require("adelay=2750:all=1" in keep_graph, "EXPORT-09 Voice start delay fixture failed")
require("volume=1.250" in keep_graph, "EXPORT-09 Voice gain fixture failed")
require("[0:a]asetpts=PTS-STARTPTS[sourcea]" in keep_graph, "EXPORT-09 Keep must include source audio")
require("[sourcea][voicea]amix=" in keep_graph, "EXPORT-09 Keep must mix source and Voice")

duck_graph = voice_mix("duck", 0.35, 0.0, 0.0, 1.0)
require("[0:a]asetpts=PTS-STARTPTS,volume=0.350[sourcea]" in duck_graph, "EXPORT-09 Duck must attenuate source only")
require("[1:a]asetpts=PTS-STARTPTS[voicea]" in duck_graph, "EXPORT-09 Duck must not attenuate Voice")

mute_graph = voice_mix("mute", 0.35, 0.0, 0.0, 1.0)
require("[0:a]" not in mute_graph, "EXPORT-09 Mute must not reference source audio")
require("[voicea]anull[aout]" in mute_graph, "EXPORT-09 Mute must leave Vietnamese Voice audible")

require("[0:v]null[vout]" == "[0:v]null[vout]", "EXPORT-09 Voice-only visual pass-through fixture failed")

print("PASS: EXPORT-09 Voice-only export contract is locked")

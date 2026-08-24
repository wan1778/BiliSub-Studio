#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages"
EDITOR = PAGES / "EditorPage.xaml.cs"
IMAGES = PAGES / "EditorPage.Images.cs"
VIDEO_EDITOR = ROOT / "csharp/src/BiliSubStudio.Core/Editor/VideoEditorService.cs"
COMPOSER = ROOT / "csharp/src/BiliSubStudio.App/Services/EditorImageOverlayComposer.cs"


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
composer = read(COMPOSER)

# EXPORT-13 — Full combo must remain one user Render entry and one orchestrated
# two-stage pipeline:
#   stage 1: Blur + completed Vietsub + source-audio policy + Vietnamese Voice
#   stage 2: ordered Image/logo overlays
# The image stage must not re-apply/mix audio; it copies the already-rendered base audio.

# All five edit families remain exportable from the single Render state owner.
for token in (
    "var subtitleReady = _subtitleSource is not null && _subtitleSource.Cues.All(x => !string.IsNullOrWhiteSpace(x.VietnameseText));",
    'var audioChanged = _audioSettings.SourceMode != "keep";',
    "var hasImages = _imageFeatureInitialized && _imageOverlays.Count > 0;",
    "_document.Regions.Count > 0 || subtitleReady || audioChanged || _voiceTrack is not null || hasImages",
):
    require(token in editor, f"EXPORT-13 Render state lost: {token}")

# The base request carries Blur regions, completed Subtitle, Audio policy and Voice together.
request_start = editor.find("private VideoEditRequest CurrentEditRequest(EditorSubtitleBurn? subtitle)")
require(request_start >= 0, "EXPORT-13 CurrentEditRequest is missing")
request_body = editor[request_start:request_start + 1900]
for token in (
    "_document.Regions.ToArray(),",
    "subtitle,",
    "_audioSettings,",
    "_voiceTrack);",
):
    require(token in request_body, f"EXPORT-13 shared base request lost: {token}")

# Full combo orchestration: base edit first, then image stage.
for token in (
    "var subtitle = CompletedSubtitleBurn();",
    'var hasBaseEdit = _document.Regions.Count > 0 || subtitle is not null || _audioSettings.SourceMode != "keep" || _voiceTrack is not null;',
    "var hasImages = _imageOverlays.Count > 0;",
    'var temporaryDirectory = Path.Combine(_application.Paths.Temp, "Editor", "ImageBase");',
    'var temporaryName = "editor-image-base-" + Guid.NewGuid().ToString("N") + ".mp4";',
    "var request = CurrentEditRequest(subtitle) with { OutputDirectory = temporaryDirectory, FileName = temporaryName };",
    "_jobId = _application.StartEditor(request);",
    "baseOutput = result.OutputPath;",
    "composerInput = result.OutputPath;",
    "var specs = _imageOverlays.Select(image => new EditorImageOverlaySpec(",
    "image.Path, image.X, image.Y, image.Width, image.Height, image.Opacity",
    "_media.Width, _media.Height, _media.Duration, specs, copyAudio: hasBaseEdit",
):
    require(token in images, f"EXPORT-13 two-stage orchestration lost: {token}")

# Temporary base is application-owned and cleaned after the image stage.
require(
    "if (baseOutput is not null)" in images and "try { File.Delete(baseOutput); } catch { }" in images,
    "EXPORT-13 temporary base cleanup lost",
)

# Base visual graph: committed effects first, ASS subtitle second.
filter_start = video.find("private static string BuildFilterCore(")
filter_end = video.find("public static string BuildAss(", filter_start)
require(filter_start >= 0 and filter_end > filter_start, "EXPORT-13 BuildFilterCore block missing")
filter_body = video[filter_start:filter_end]
for token in (
    "for (var index = 0; index < request.Regions.Count; index++)",
    'else if (effect is "" or "blur")',
    "boxblur=luma_radius={strength}:luma_power=1",
    "current = output;",
    "if (request.Subtitle is not null)",
    'parts.Add($"[{current}]ass=filename=\'{escaped}\'[vout]");',
):
    require(token in filter_body, f"EXPORT-13 base visual graph lost: {token}")
require(
    filter_body.find("for (var index = 0; index < request.Regions.Count; index++)")
    < filter_body.find("if (request.Subtitle is not null)"),
    "EXPORT-13 Blur/effects must be applied before Subtitle",
)

# Base RunAsync owns Voice/source-audio mixing exactly once.
run_start = video.find("public async Task<VideoEditResult> RunAsync(")
run_end = video.find("private async Task ValidateRenderedOutputAsync", run_start)
require(run_start >= 0 and run_end > run_start, "EXPORT-13 RunAsync block missing")
run_body = video[run_start:run_end]
for token in (
    "BuildAss(request.Subtitle!, request.SourceWidth, request.SourceHeight)",
    "var graph = BuildFilter(request, subtitleAss);",
    "var audio = EditorProjectStore.NormalizeAudio(request.Audio);",
    "var voice = NormalizeVoiceTrack(request.VoiceTrack, requireFile: true);",
    'if (voice is not null) args.AddRange(["-i", voice.Path]);',
    'var combinedGraph = voice is null ? graph : graph + ";" + BuildVoiceAudioFilter(audio, voice, 1, sourceStart: 0);',
    '"-filter_complex", combinedGraph',
    'else args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);',
    'voice is not null || audio.SourceMode != "mute"',
):
    require(token in run_body, f"EXPORT-13 base audio/voice render lost: {token}")

voice_start = video.find("private static string BuildVoiceAudioFilter")
voice_end = video.find("public static bool IsActiveAt", voice_start)
require(voice_start >= 0 and voice_end > voice_start, "EXPORT-13 Voice audio owner missing")
voice_body = video[voice_start:voice_end]
for token in (
    "var relativeDelay = Math.Max(0, voice.Start - sourceStart);",
    'voiceFilters.Add($"adelay={milliseconds}:all=1");',
    'voiceFilters.Add("volume=" + voice.Gain.ToString("0.000", CultureInfo.InvariantCulture));',
    'if (audio.SourceMode == "mute") return voiceChain + ";[voicea]anull[aout]";',
    'if (audio.SourceMode == "duck") sourceFilters.Add("volume=" + audio.SourceGain.ToString("0.000", CultureInfo.InvariantCulture));',
    "[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]",
):
    require(token in voice_body, f"EXPORT-13 source/Voice policy lost: {token}")

# Image stage consumes the already rendered base as input 0 and only owns picture overlays.
for token in (
    "var input = Path.GetFullPath(inputPath.Trim());",
    '"-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", input,',
    "var graph = BuildGraph(normalized, sourceWidth, sourceHeight);",
    '"-filter_complex", graph,',
    '"-map", "[vout]", "-map", "0:a?", "-map_metadata", "0", "-sn", "-dn",',
    'if (copyAudio) args.AddRange(["-c:a", "copy"]);',
):
    require(token in composer, f"EXPORT-13 image post-stage lost: {token}")

# Critical full-combo invariant: image stage must never re-run source audio policy or
# Voice mixing. It only copies audio from the validated base render.
for forbidden in ("BuildVoiceAudioFilter", "amix=inputs=", "adelay=", '"-af"'):
    require(forbidden not in composer, f"EXPORT-13 image stage must not remix audio: {forbidden}")

# Ordered image/logo stacking is the final visual stage.
for token in (
    'var current = "0:v";',
    "for (var index = 0; index < images.Count; index++)",
    'var outputLabel = index == images.Count - 1 ? "vout" : $"logoout{index}";',
    "colorchannelmixer=aa={alpha}",
    "overlay={x}:{y}:eof_action=repeat:shortest=0",
):
    require(token in composer, f"EXPORT-13 image stacking lost: {token}")

# Safe output lifecycle in both stages.
for token in (
    'var temporary = output + ".rendering" + Path.GetExtension(output);',
    "await ValidateRenderedOutputAsync(temporary, request.Duration",
    "File.Move(temporary, output);",
):
    require(token in run_body, f"EXPORT-13 base output safety lost: {token}")
for token in (
    "var output = FileNamePolicy.UniquePath(Path.Combine(directory, sanitized), input);",
    'var temporary = output + ".rendering" + extension;',
    "await ValidateAsync(temporary, sourceWidth, sourceHeight, duration, job.CancellationToken);",
    "File.Move(temporary, output);",
):
    require(token in composer, f"EXPORT-13 final image output safety lost: {token}")


# Portable behavior fixture for the exact full-combo boundary.
def full_combo_route(
    *,
    subtitle_ready: bool,
    blur_regions: int,
    audio_mode: str,
    voice: bool,
    images: int,
) -> tuple[bool, bool, str, bool]:
    audio_changed = audio_mode != "keep"
    has_base_edit = blur_regions > 0 or subtitle_ready or audio_changed or voice
    has_images = images > 0
    render_enabled = has_base_edit or has_images
    if not render_enabled:
        return (False, False, "reject", False)
    if has_images and has_base_edit:
        return (True, True, "temporary-base", True)
    if has_images:
        return (True, False, "source", False)
    return (True, True, "direct-final", False)


def base_visual_graph(*, blur: bool, subtitle: bool) -> str:
    current = "0:v"
    parts: list[str] = []
    if blur:
        parts.extend([
            "[0:v]split=2[base0][fx0]",
            "[fx0]crop=900:180:120:700,boxblur=luma_radius=18:luma_power=1[rendered0]",
            "[base0][rendered0]overlay=120:700[v0]",
        ])
        current = "v0"
    if subtitle:
        parts.append(f"[{current}]ass=filename='caption.ass'[vout]")
    else:
        parts.append(f"[{current}]null[vout]")
    return ";".join(parts)


def voice_audio_graph(mode: str, source_gain: float, voice_start: float, voice_gain: float) -> str:
    voice_filters = ["asetpts=PTS-STARTPTS"]
    if voice_start > 0.0005:
        voice_filters.append(f"adelay={round(voice_start * 1000)}:all=1")
    if abs(voice_gain - 1) > 0.0005:
        voice_filters.append(f"volume={voice_gain:.3f}")
    voice_chain = f"[1:a]{','.join(voice_filters)}[voicea]"
    if mode == "mute":
        return voice_chain + ";[voicea]anull[aout]"
    source_filters = ["asetpts=PTS-STARTPTS"]
    if mode == "duck":
        source_filters.append(f"volume={source_gain:.3f}")
    return (
        f"[0:a]{','.join(source_filters)}[sourcea];"
        + voice_chain
        + ";[sourcea][voicea]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]"
    )


def image_stage_audio(copy_audio: bool) -> list[str]:
    args = ["-map", "[vout]", "-map", "0:a?"]
    args += ["-c:a", "copy"] if copy_audio else ["-c:a", "aac", "-b:a", "192k"]
    return args


for mode in ("keep", "duck", "mute"):
    route = full_combo_route(
        subtitle_ready=True,
        blur_regions=1,
        audio_mode=mode,
        voice=True,
        images=2,
    )
    require(route == (True, True, "temporary-base", True),
            f"EXPORT-13 full combo route failed for {mode}")

visual = base_visual_graph(blur=True, subtitle=True)
require(visual.index("boxblur") < visual.index("ass=filename="),
        "EXPORT-13 fixture must Blur before Subtitle")
require(visual.endswith("[vout]"), "EXPORT-13 base visual graph must end at [vout]")

keep_audio = voice_audio_graph("keep", 1.0, 2.75, 1.25)
require("adelay=2750:all=1" in keep_audio and "volume=1.250" in keep_audio,
        "EXPORT-13 Keep fixture lost Voice timing/gain")
require("[sourcea][voicea]amix=" in keep_audio, "EXPORT-13 Keep must mix source + Voice")

duck_audio = voice_audio_graph("duck", 0.35, 0.0, 1.0)
require("[0:a]asetpts=PTS-STARTPTS,volume=0.350[sourcea]" in duck_audio,
        "EXPORT-13 Duck must attenuate source once")
require("[1:a]asetpts=PTS-STARTPTS[voicea]" in duck_audio,
        "EXPORT-13 Duck must not attenuate Voice")

mute_audio = voice_audio_graph("mute", 0.35, 0.0, 1.0)
require("[0:a]" not in mute_audio and "[voicea]anull[aout]" in mute_audio,
        "EXPORT-13 Mute must remove source but retain Voice")

post_audio = image_stage_audio(copy_audio=True)
require(post_audio == ["-map", "[vout]", "-map", "0:a?", "-c:a", "copy"],
        "EXPORT-13 Image stage must copy base audio without remix/re-encode")

print("PASS: EXPORT-13 full combo export contract is locked")

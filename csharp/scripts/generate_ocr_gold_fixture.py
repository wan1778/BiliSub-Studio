"""Generate the deterministic OCR gold fixtures and bind manifests to real frame PTS.

Render definitions (CFR or irregular-VFR), captions and cue lists live in the
template manifest. The generator renders every distinct video once, then binds
each expected cue to actual decoded frame PTS reported by ffprobe, so gold
timing is frame-accurate by construction. Rendering-only template keys are
stripped from the emitted manifest, which the strict harness loader validates.
"""

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tempfile
from fractions import Fraction


SCRIPT = pathlib.Path(__file__).resolve()
REPO = SCRIPT.parents[2]
FIXTURES = REPO / "csharp" / "tests" / "BiliSubStudio.OcrGoldHarness" / "fixtures"


def run(command):
    result = subprocess.run(command, text=True, encoding="utf-8", errors="replace",
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(map(str, command))}\n{result.stderr}")
    return result.stdout


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def filter_path(path):
    value = path.resolve().as_posix().replace("\\", "/")
    return value.replace(":", r"\:").replace("'", r"\'")


def frame_pts(ffprobe, video):
    raw = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames",
               "-show_entries", "stream=time_base:frame=pts,best_effort_timestamp,best_effort_timestamp_time",
               "-of", "json", str(video)])
    probe = json.loads(raw)
    streams = probe.get("streams", [])
    frames = probe.get("frames", [])
    if len(streams) != 1 or not frames:
        raise SystemExit("ffprobe did not return one video stream and a non-empty frame inventory")
    output = []
    for frame in frames:
        pts = frame.get("pts", frame.get("best_effort_timestamp"))
        seconds = frame.get("best_effort_timestamp_time")
        if pts is None or seconds is None:
            raise SystemExit("ffprobe frame is missing integer PTS or pts_time; refusing to drop it")
        output.append((int(pts), float(seconds)))
    if any(right[0] <= left[0] or right[1] <= left[1] for left, right in zip(output, output[1:])):
        raise SystemExit("ffprobe frame PTS are not strictly increasing")
    return streams[0]["time_base"], output


def boundary(frames, seconds):
    return next(pts for pts, time in frames if time + 1e-9 >= seconds)


def drawtext(font, line, y, start, end):
    return (f"drawtext=fontfile='{font}':text='{line}':fontcolor=white:fontsize=58:"
            f"borderw=3:bordercolor=black:x=(w-text_w)/2:y={y}"
            f":enable='gte(t,{start})*lt(t,{end})'")


def render_video(render, name, font, ffmpeg, ffprobe, destination):
    captions = render["captions"]
    y_single, y_first, y_second = 560, 505, 578
    filters = []
    for caption in captions:
        lines = caption["text"].split("\n")
        ys = [y_single] if len(lines) == 1 else [y_first, y_second]
        for line, y in zip(lines, ys):
            filters.append(drawtext(font, line, y, caption["start"], caption["end"]))
    kind = render.get("kind", "cfr")
    if kind == "cfr":
        fps = render["fps"]
        source = f"color=c=0x20242b:s=1280x720:r={fps}:d={render['durationSeconds']}"
        frames_expected = int(render["frames"])
        chain = ",".join(filters)
    elif kind == "vfr":
        # Irregular native durations alternating between two values, produced by
        # re-timelineing a fixed-rate source with setpts, so ffprobe shows real
        # non-uniform frame PTS (VFR) while the frame count stays exact.
        fps = render["baseFps"]
        count = render["frames"]
        d0, d1 = render["durations"]
        mean, swing = (d0 + d1) / 2, (d1 - d0) / 2
        expr = f"round(({mean:.6f}*N-{swing:.6f}*mod(N\\,2))/TB)"
        source = f"color=c=0x20242b:s=1280x720:r={fps}:d={count / fps + 1}"
        frames_expected = count
        chain = f"settb=1/1000,setpts='{expr}',{','.join(filters)}"
    else:
        raise SystemExit(f"unknown render kind: {kind}")
    with tempfile.TemporaryDirectory(prefix="bilisub-ocr-gold-") as temporary:
        temporary_video = pathlib.Path(temporary) / name
        command = [ffmpeg, "-hide_banner", "-nostdin", "-f", "lavfi", "-i", source]
        command += ["-frames:v", str(frames_expected), "-fps_mode", "passthrough",
                    "-enc_time_base", "1/1000" if kind == "vfr" else str(1 / Fraction(str(fps)))]
        command += ["-vf", chain, "-an", "-c:v", "ffv1", "-level", "3", "-g", "1",
                    "-pix_fmt", "bgr0", "-threads", "1", "-fflags", "+bitexact",
                    "-flags:v", "+bitexact", "-map_metadata", "-1", "-y", str(temporary_video)]
        run(command)
        time_base, frames = frame_pts(ffprobe, temporary_video)
        if len(frames) != frames_expected:
            raise SystemExit(f"fixture {name} decoded {len(frames)} frames, expected exactly {frames_expected}")
        tick = float(Fraction(time_base))
        for index, (_, seconds) in enumerate(frames):
            expected = (index // 2 * (d0 + d1) + index % 2 * d0
                        if kind == "vfr" else float(index / Fraction(str(fps))))
            if abs(seconds - expected) > tick / 2 + 1e-6:
                raise SystemExit(f"fixture {name} frame {index}: PTS {seconds} != expected {expected}")
        if kind == "vfr":
            deltas = [round(right[1] - left[1], 4) for left, right in zip(frames, frames[1:])]
            if len(set(deltas)) < 2 or max(deltas) - min(deltas) < 0.01:
                raise SystemExit("vfr fixture has uniform frame spacing; the point is irregular PTS")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(temporary_video, destination)
    return time_base, frames


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--font", required=True, type=pathlib.Path, help="explicit Chinese font file")
    parser.add_argument("--font-license", required=True, help="SPDX id or documented license identifier")
    parser.add_argument("--font-source-url", default=None)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--manifest", type=pathlib.Path, default=FIXTURES / "manifest.json")
    parser.add_argument("--template", type=pathlib.Path, default=FIXTURES / "manifest.template.json")
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()
    args.font = args.font.resolve()
    args.manifest = args.manifest.resolve()
    args.template = args.template.resolve()
    if not args.font.is_file():
        raise SystemExit(f"font does not exist: {args.font}")
    if args.manifest.exists() and args.manifest != (FIXTURES / "manifest.json").resolve() and not args.force:
        raise SystemExit("output exists; pass --force to replace generated files")
    if not args.template.is_file():
        raise SystemExit(f"manifest template does not exist: {args.template}")

    manifest = json.loads(args.template.read_text(encoding="utf-8"))
    renders = manifest["render"]
    outputs = {name: args.manifest.parent / spec["file"] for name, spec in renders.items()}
    for path in outputs.values():
        if path.exists() and not args.force:
            raise SystemExit(f"fixture exists; pass --force: {path}")
    ffmpeg_version = run([args.ffmpeg, "-version"]).splitlines()[0]
    rendered = {}
    for name, spec in renders.items():
        time_base, frames = render_video(spec, spec["file"], filter_path(args.font),
                                         args.ffmpeg, args.ffprobe, outputs[name])
        rendered[name] = (spec, time_base, frames)
    for case in manifest["cases"]:
        name = case.pop("render")
        spec, time_base, frames = rendered[name]
        captions = spec["captions"]
        case["video"] = spec["file"]
        case["videoSha256"] = sha256(outputs[name])
        case["provenance"] = {
            "kind": "generated",
            "source": "generator:csharp/scripts/generate_ocr_gold_fixture.py",
            "license": args.font_license,
            "attribution": (f"Synthetic captions rendered with {args.font.name}; font sha256={sha256(args.font)}; "
                            f"ffmpeg={ffmpeg_version}; time_base={time_base}"),
            "sourceUrl": args.font_source_url,
        }
        if len(case["expectedCues"]) != len(captions):
            raise SystemExit(f"case '{case['id']}' expectedCues must match captions 1:1 in order")
        for cue, caption in zip(case["expectedCues"], captions):
            if cue["text"] != caption["text"]:
                raise SystemExit(f"case '{case['id']}' cue '{cue['id']}' text does not match caption order")
            # Start is the first visible native frame; end is the first
            # blank frame after visibility, matching the half-open [start, end).
            cue["onsetPts"] = boundary(frames, caption["start"])
            cue["offsetPts"] = boundary(frames, caption["end"])
        case.pop("visibleHints", None)
    for key in ("render", "fps", "durationSeconds"):
        manifest.pop(key, None)

    args.manifest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(args.manifest),
                      "fixtures": {name: str(path) for name, path in outputs.items()},
                      "frames": {name: len(meta[2]) for name, meta in rendered.items()},
                      "cases": len(manifest["cases"])}))


if __name__ == "__main__":
    main()

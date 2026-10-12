#!/usr/bin/env python3
"""Integration slice BEFORE/AFTER for repeat-guard work (11/10).

Runs ~40 real repeat-prone cue texts through fit_cue on the CURRENT worker,
records per-cue shipped-repeat proxy + cost. Run BEFORE the fix, save JSON;
run AFTER with --after <before.json> to print the comparison table.

Usage (venv python):
  python internal/vieneu/slice_repeat_check.py before D:/temp/slicebefore.json [ffmpeg] [input.json] [workdir]
  python internal/vieneu/slice_repeat_check.py after D:/temp/slicebefore.json [ffmpeg] [input.json] [workdir]
Defaults target this dev machine; override via argv on other machines.
"""
from __future__ import annotations

import importlib.util
import json
import sys
import time
from pathlib import Path

WORKER = Path(__file__).resolve().parent / "worker.py"
FFMPEG = Path(sys.argv[3] if len(sys.argv) > 3
              else r"E:\du_an\BiliSub-Studio\csharp\publish\BiliSubStudio.App\Tools\ffmpeg.exe")
SRC = Path(sys.argv[4] if len(sys.argv) > 4
           else r"E:\du_an\BiliSub-Studio\csharp\publish\BiliSubStudio.App\Cache\Editor\TTS\25f89bc887414561e2b93a85\failed-runs\0006c75acb7e452c97d41b6c918b64ad\input.json")


def load_worker():
    spec = importlib.util.spec_from_file_location("vieneu_worker_slice", WORKER)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "before"
    w = load_worker()
    events = []
    w.emit = events.append
    from vieneu import Vieneu
    engine = Vieneu()
    voice = w.VieneuVoice(engine, "Ngọc Huyền")

    inp = json.load(open(SRC, encoding="utf-8"))
    shorts, mediums = [], []
    for c in inp["cues"]:
        t = c.get("text", "")
        try:
            u = w.speakable_units(t)
        except ValueError:
            continue
        dur = (c["voice_end"] - c["voice_start"]) if c.get("voice_end", 0) > c.get("voice_start", 0) else 2.0
        if u <= 8 and len(shorts) < 25:
            shorts.append((c, t, dur))
        elif 8 < u <= 20 and len(mediums) < 15:
            mediums.append((c, t, dur))
        if len(shorts) >= 25 and len(mediums) >= 15:
            break
    picks = shorts + mediums
    print(f"SLICE: {len(picks)} cues ({len(shorts)} short + {len(mediums)} medium)", flush=True)

    workdir = Path(sys.argv[5] if len(sys.argv) > 5 else "D:/temp/ttsslice")
    base = workdir / mode
    run_root = base / "run"
    clip_root = base / "clips"
    run_root.mkdir(parents=True, exist_ok=True)
    clip_root.mkdir(parents=True, exist_ok=True)

    rows = []
    t_all = time.time()
    for pos, (c, text, dur) in enumerate(picks):
        key = f"slicetest-{pos:03d}"
        target = max(2205, int(round(dur * 22050)))
        calls = {"n": 0}

        def on_attempt(attempt, scale, _calls=calls):
            _calls["n"] += 1

        t0 = time.time()
        try:
            out, record = w.fit_cue(
                voice, text, target, run_root, key, "srt-fallback",
                FFMPEG, on_attempt, cue_id=c.get("id", f"slice-{pos}"))
            dt = time.time() - t0
            rep = bool(w.speech_repeats(out))
            try:
                tail = bool(w.tail_repeats(out))
            except AttributeError:
                tail = None
            rows.append({"i": pos, "id": c.get("id", ""), "ok": True,
                         "repeat": rep, "tail": tail,
                         "attempts": record.get("synthesis_attempts"),
                         "fit": record.get("fit_method"), "status": record.get("status"),
                         "seconds": round(dt, 2)})
        except Exception as error:
            rows.append({"i": pos, "id": c.get("id", ""), "ok": False,
                         "error": str(error)[:160], "seconds": round(time.time() - t0, 2)})
        print(f"[{pos + 1}/{len(picks)}] ok={rows[-1]['ok']} repeat={rows[-1].get('repeat')} "
              f"s={rows[-1]['seconds']}", flush=True)
    print(f"WALL: {time.time() - t_all:.0f}s", flush=True)

    if mode == "before":
        out_path = sys.argv[2] if len(sys.argv) > 2 else "D:/temp/slicebefore.json"
        json.dump(rows, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"SAVED {out_path}")
    else:
        before = {r["i"]: r for r in json.load(open(sys.argv[2], encoding="utf-8"))}
        print("i | before_repeat -> after_repeat | before_s -> after_s | status")
        worse = same = better = 0
        for r in rows:
            b = before.get(r["i"], {})
            br, ar = b.get("repeat"), r.get("repeat")
            mark = "SAME"
            if br and not ar:
                mark = "FIXED"; better += 1
            elif not br and ar:
                mark = "NEW-REPEAT"; worse += 1
            print(f"{r['i']} | {br} -> {ar} | {b.get('seconds')} -> {r['seconds']}s | {mark} ok={r['ok']}")
        print(f"SUMMARY: fixed={better} new_repeat={worse} ok={sum(1 for r in rows if r['ok'])}/{len(rows)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

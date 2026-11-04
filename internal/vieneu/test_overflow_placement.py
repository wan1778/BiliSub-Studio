#!/usr/bin/env python3
"""Repeat-reading regression: overflow may only use the real gap to the next cue.

The 30/10 production result had 129 overlapping clip pairs (28.3s) because a
single cue could spill up to OVERFLOW_MAX_FRAMES (3.0s) into the following
cue's window. These tests pin the gap-aware budget and replay that result.

Run: python internal/vieneu/test_overflow_placement.py (also wired into verify.ps1).
"""
from __future__ import annotations

import importlib.util
import json
import os
import unittest
from pathlib import Path

WORKER = Path(__file__).resolve().parent / "worker.py"
SAMPLE_RATE = 22050
RESULT_ENV = "VIENEU_OVERFLOW_RESULT"
DEFAULT_RESULT = Path(
    r"E:\du_an\BiliSub-Studio\csharp\publish\BiliSubStudio.App\Cache\Editor\TTS"
    r"\fbdf66d3eec012912d3a2de9\result-d1db7c2e54014eb1858c2becd13c423b.json")


def load_worker():
    spec = importlib.util.spec_from_file_location("vieneu_worker_overflow_test", WORKER)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


def clip(cue, start, frames, window_end):
    return {"id": cue["id"], "start_sample": start, "frames": frames,
            "end_sample": start + frames, "window_end_sample": window_end}


class OverflowPlacementTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = load_worker()

    def _place(self, cues, requested):
        clips = []
        total = max(round(cue["cue_end"] * SAMPLE_RATE) for cue in cues)
        for index, (cue, overflow) in enumerate(zip(cues, requested)):
            start, target = self.w.cue_window(cue)
            budget = self.w.overflow_budget_frames(cues, index, cue, total)
            used = max(0, min(int(overflow), budget))
            clips.append(clip(cue, start, target + used, round(cue["cue_end"] * SAMPLE_RATE)))
        return clips

    def test_pathological_contiguous_timecodes_never_overlap(self):
        # 200 back-to-back 1s cues: every cue is too long for its window, which
        # is exactly where the old unconditional 3.0s overflow produced doubles.
        cues = []
        for index in range(200):
            start = index * 1.0
            cues.append({"id": f"cue-{index}", "cue_start": start, "cue_end": start + 1.0,
                         "voice_start": start, "voice_end": start + 1.0, "timing_source": "whisper"})
        requested = [int(2.0 * SAMPLE_RATE)] * len(cues)
        old = [clip(cue, round(cue["voice_start"] * SAMPLE_RATE),
                    round((cue["voice_end"] - cue["voice_start"]) * SAMPLE_RATE) + want,
                    round(cue["cue_end"] * SAMPLE_RATE))
               for cue, want in zip(cues, requested)]
        self.assertGreater(len(self.w.clip_overlap_pairs(old)), 0, "fixture must reproduce the bug")
        new = self._place(cues, requested)
        self.assertEqual([], self.w.clip_overlap_pairs(new))
        self.assertTrue(all(item["frames"] <= item["window_end_sample"] - item["start_sample"]
                            for item in new))

    def test_overflow_is_capped_to_a_real_gap(self):
        cues = [
            {"id": "a", "cue_start": 0.0, "cue_end": 1.0, "voice_start": 0.0,
             "voice_end": 1.0, "timing_source": "whisper"},
            {"id": "b", "cue_start": 1.5, "cue_end": 2.5, "voice_start": 1.5,
             "voice_end": 2.5, "timing_source": "whisper"},
        ]
        clips = self._place(cues, [int(2.0 * SAMPLE_RATE), 0])
        self.assertEqual([], self.w.clip_overlap_pairs(clips))
        self.assertEqual(int(0.5 * SAMPLE_RATE), clips[0]["frames"] - int(1.0 * SAMPLE_RATE))

    def test_replay_production_result_129_to_0(self):
        path = Path(os.environ.get(RESULT_ENV) or DEFAULT_RESULT)
        if not path.is_file():
            self.skipTest(f"production result fixture missing: {path}")
        cues = json.loads(path.read_text(encoding="utf-8"))["cues"]
        total = max(cue["clip_start_sample"] + cue["frames"] for cue in cues)
        source = []
        fixed = []
        for index, cue in enumerate(cues):
            target = cue["target_frames"]
            overflow = int(cue.get("overflow_frames", 0))
            start = cue["clip_start_sample"]
            window_end = start + target
            source.append(clip(cue, start, cue["frames"], window_end))
            budget = 0
            if index + 1 < len(cues):
                budget = max(0, cues[index + 1]["clip_start_sample"] - window_end)
            else:
                budget = max(0, total - window_end)
            fixed.append(clip(cue, start, target + min(overflow, budget), window_end))
        old_pairs = self.w.clip_overlap_pairs(source)
        adjacent = sum(1 for left, right in zip(source, source[1:])
                       if left["start_sample"] + left["frames"] > right["start_sample"])
        self.assertEqual(129, adjacent, "production result must reproduce the 129 adjacent pairs")
        self.assertGreaterEqual(len(old_pairs), adjacent)
        self.assertEqual([], self.w.clip_overlap_pairs(fixed))


if __name__ == "__main__":
    unittest.main(verbosity=1)

"""F1 (17/11): a tight cue must read its WHOLE sentence before any word is cut.

Regression: an exported video had cues that dropped the last 2-3 words of the
sentence. Root cause: escalate_truncated_voice ran the seeded prefix search
(cutting the tail) before the whole-sentence spill into the following silence,
so a cue with a real gap never even tried to keep every word.

Pinned contract:
  1. escalate_truncated_voice tries the whole-sentence overflow FIRST and
     returns it when it fits - the prefix search must not run at all.
  2. With no usable gap the prefix search still runs (the impossible-window
     path must not be removed, only demoted).
  3. The spill helper refuses impossible windows instead of overlapping.

Run: python internal/vieneu/test_word_loss_order.py (also wired into verify.ps1).
"""
from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

WORKER = Path(__file__).resolve().parents[2] / "internal" / "vieneu" / "worker.py"


def load_worker():
    spec = importlib.util.spec_from_file_location("vieneu_worker_word_loss_test", WORKER)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


ACTIVE = {
    "id": "cue-word-loss",
    "cue_start": 0.0,
    "cue_end": 2.0,
    "voice_start": 0.0,
    "voice_end": 2.0,
    "timing_source": "srt-fallback",
}
TEXT = "ngay ca chuc nang tru vat"


class WordLossOrderTest(unittest.TestCase):
    def setUp(self) -> None:
        self.w = load_worker()
        self.calls: list[str] = []
        self._clip = Path.cwd() / "_word_loss_order_clip.wav"

    def tearDown(self) -> None:
        try:
            self._clip.unlink()
        except OSError:
            pass

    def _patch(self, overflow_result=None, prefix_hit=None, fit_result=None):
        saved = (self.w.try_overflow_voice, self.w._seeded_prefix_search, self.w.fit_cue,
                 self.w.native_record_valid, self.w.sha256)
        self.w.try_overflow_voice = (
            lambda *a, **k: (self.calls.append("overflow"), overflow_result)[1])
        self.w._seeded_prefix_search = (
            lambda *a, **k: (self.calls.append("prefix"), prefix_hit)[1])
        # Same drive as the run root the helper is called with (os.replace cannot
        # cross drives); removed in tearDown so the repo stays clean.
        clip = Path.cwd() / "_word_loss_order_clip.wav"
        clip.write_bytes(b"RIFF")
        self.w.fit_cue = (
            lambda *a, **k: (self.calls.append("fit"), (clip, fit_result or {}))[1])
        self.w.native_record_valid = lambda *a, **k: True
        self.w.sha256 = lambda path, **k: "0" * 64
        return saved

    def _unpatch(self, saved) -> None:
        (self.w.try_overflow_voice,
         self.w._seeded_prefix_search,
         self.w.fit_cue,
         self.w.native_record_valid,
         self.w.sha256) = saved

    def _escalate(self):
        return self.w.escalate_truncated_voice(
            0, ACTIVE["id"], 1, ACTIVE, TEXT, 1000, None, 1.0,
            Path("."), Path("."), "sha", Path("."), 5000, lambda attempt, scale: None)

    def test_whole_sentence_overflow_wins_before_any_prefix_cut(self):
        sentinel = (ACTIVE, "overflow-key", Path("clip.wav"), {"status": "review"}, False, 1)
        saved = self._patch(overflow_result=sentinel)
        try:
            result = self._escalate()
        finally:
            self._unpatch(saved)
        self.assertIs(result, sentinel, "a whole-sentence spill must be accepted as-is")
        self.assertEqual(["overflow"], self.calls, "no word may be cut while the spill fits")

    def test_prefix_cut_only_runs_when_no_gap_exists(self):
        fit_record = {
            "base_length_scale": 1.0, "length_scale": 1.0, "source_frames": 100,
            "generated_frames": 100, "trimmed_silence_frames": 0, "padding_frames": 0,
            "frames": 100, "target_frames": 1000, "synthesis_attempts": 1,
            "native_reference_frames": 100, "group_native_frames": 0,
            "actual_speed_factor": 1.0, "tempo_factor": 0, "tempo_input_frames": 0,
            "tempo_attempts": 0, "fit_method": "vieneu-natural",
            "timing_source": "srt-fallback", "status": "review", "raw_duration": 0.01,
            "fitted_duration": 0.01,
        }
        saved = self._patch(overflow_result=None, prefix_hit=("ngay ca chuc nang", "trunc-key"),
                            fit_result=fit_record)
        try:
            result = self._escalate()
        finally:
            self._unpatch(saved)
        self.assertEqual(["overflow", "prefix", "fit"], self.calls,
                         "the tail cut must still exist, but only after the spill failed")
        self.assertEqual("review", result[3]["status"], "a cut cue always stays a review")

    def test_spill_into_gap_rejects_an_impossible_window(self):
        self.assertIsNone(self.w.try_spill_into_gap(
            Path("ffmpeg"), Path("native.wav"), Path("."), "key", "cue-id", "srt-fallback",
            target_frames=1000, budget_frames=0, base=1.0, text=TEXT,
            source_frames=100, trimmed_silence=10, natural=9000,
            raw_frames=100, native_reference_frames=9000, cap=1.5, attempt=1,
            minimum_generated=10), "no silence after the cue must never spill")

    def test_spill_into_gap_declines_when_even_the_cap_cannot_fit(self):
        # 9.5s of speech into a 1s window + 10ms of gap: impossible at any permitted pace.
        self.assertIsNone(self.w.try_spill_into_gap(
            Path("ffmpeg"), Path("native.wav"), Path("."), "key", "cue-id", "srt-fallback",
            target_frames=1000, budget_frames=220, base=1.0, text=TEXT,
            source_frames=210000, trimmed_silence=0, natural=210000,
            raw_frames=210000, native_reference_frames=210000, cap=1.5, attempt=1,
            minimum_generated=10))


if __name__ == "__main__":
    unittest.main(verbosity=2)

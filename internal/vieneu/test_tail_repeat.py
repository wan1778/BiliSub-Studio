#!/usr/bin/env python3
"""Repeat-guard tests, second layer (11/10): final verification + punct-swap.

Layer 1 (existing, must keep passing): speech_repeats catches full doubles
(ACF + nuclear rule), cut_first_instance cuts gapped doubles.

Layer 2 (new): every about-to-ship pick passes a final ACF-leaning check;
suspects get bounded punct-swapped redraws; still-suspect ships carry review.
A previous tail-correlation-detector idea was evaluated on real clips and
REJECTED (clean max 0.87 vs doubled min 0.52 — inseparable); no such code ships.

Run: python internal/vieneu/test_tail_repeat.py (also wired into verify.ps1).
"""
from __future__ import annotations

import importlib.util
import unittest
import wave
from pathlib import Path

import numpy as np

WORKER = Path(__file__).resolve().parent / "worker.py"


def load_worker():
    spec = importlib.util.spec_from_file_location("vieneu_worker_repeat_test", WORKER)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


def am_seg(freq, mod, seconds, rate=22050, amp=0.5):
    t = np.arange(int(seconds * rate)) / rate
    return (amp * np.sin(2 * np.pi * freq * t)
            * (0.55 + 0.45 * np.sin(2 * np.pi * mod * t))).astype(np.float32)


def noisy_burst(seed, seconds, rate=22050):
    # Speech-like envelope: random syllabic bursts (deterministic).
    # Pure periodic tones fake ACF peaks; this does not.
    rng = np.random.default_rng(seed)
    cell = rate // 100
    n = int(seconds * rate) // cell * cell
    env = rng.uniform(0.05, 1.0, size=n // cell)
    env = np.repeat(env, cell)
    t = np.arange(n) / rate
    return (0.5 * np.sin(2 * np.pi * 200 * t) * env).astype(np.float32)


def write_wav(path, samples, rate=22050):
    pcm = (np.clip(np.asarray(samples, dtype=np.float32), -1.0, 1.0) * 32767).astype(np.int16)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes(pcm.tobytes())


class RepeatGuardTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.w = load_worker()

    def setUp(self):
        import tempfile
        self._dir = tempfile.TemporaryDirectory()
        self.root = Path(self._dir.name)

    def tearDown(self):
        self._dir.cleanup()

    def test_punct_swap_variants(self):
        self.assertEqual(self.w.punct_swap_variants("Đi nào."), ["Đi nào!"])
        self.assertEqual(self.w.punct_swap_variants("Xong rồi。"), ["Xong rồi！"])
        self.assertEqual(self.w.punct_swap_variants("Êtes-vous prêt?"), [])
        self.assertEqual(self.w.punct_swap_variants("Đi nào!"), [])
        self.assertEqual(self.w.punct_swap_variants(""), [])
        self.assertEqual(self.w.punct_swap_variants("Không dấu câu cuối"), [])

    def test_final_gate_fires_on_full_double(self):
        a = noisy_burst(505, 1.0)
        doubled = np.concatenate([a, a])
        p = self.root / "doubled.wav"
        write_wav(p, doubled)
        self.assertTrue(self.w.final_pick_repeats(p, "đi nào"))

    def test_final_gate_quiet_on_clean(self):
        clean = np.concatenate([noisy_burst(11, 1.0), noisy_burst(22, 1.0), noisy_burst(33, 1.0)])
        p = self.root / "clean.wav"
        write_wav(p, clean)
        self.assertFalse(self.w.final_pick_repeats(p, "một hai ba bốn"))

    def test_final_gate_skips_self_repeating_text(self):
        a = am_seg(220, 3.0, 1.0)
        p = self.root / "selfrep.wav"
        write_wav(p, np.concatenate([a, a]))
        # Text itself repeats: identical halves are CORRECT, never suspect.
        self.assertFalse(self.w.final_pick_repeats(p, "Vâng! Vâng!"))

    def test_final_gate_safe_on_short_silent_missing(self):
        tiny = self.root / "tiny.wav"
        write_wav(tiny, am_seg(440, 9.0, 0.2))
        self.assertFalse(self.w.final_pick_repeats(tiny, "a"))
        silent = self.root / "silent.wav"
        write_wav(silent, np.zeros(22050, dtype=np.float32))
        self.assertFalse(self.w.final_pick_repeats(silent, "a"))
        self.assertFalse(self.w.final_pick_repeats(self.root / "nope.wav", "a"))

    def test_speech_repeats_anchors(self):
        a = noisy_burst(303, 1.2)
        doubled = self.root / "d.wav"
        write_wav(doubled, np.concatenate([a, a]))
        self.assertTrue(self.w.speech_repeats(doubled))
        clean = self.root / "c.wav"
        write_wav(clean, noisy_burst(404, 1.2))
        self.assertFalse(self.w.speech_repeats(clean))


if __name__ == "__main__":
    unittest.main(verbosity=1)

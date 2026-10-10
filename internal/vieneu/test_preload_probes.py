#!/usr/bin/env python3
"""Regression tests for preload_probes chunk isolation (10/10 speedup work).

Bug class: one bad chunk (OOM/strange text) used to discard the ENTIRE
pre-pass silently (return {}), sending thousands of cues back to slow
single synthesis with no trace. Now only the failing chunk falls back,
a fallback notice names it, and length bucketing keeps chunks uniform.

Run: python internal/vieneu/test_preload_probes.py (also wired into verify.ps1).
"""
from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
import wave
from pathlib import Path

WORKER = Path(__file__).resolve().parent / "worker.py"


def load_worker():
    spec = importlib.util.spec_from_file_location("vieneu_worker_under_test", WORKER)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


def make_cue(i, text, start=0.0, end=2.0):
    return {
        "id": f"cue-{i:04d}", "cue_start": start, "cue_end": end,
        "voice_start": start, "voice_end": end, "timing_source": "srt-fallback",
    }


class PreloadProbesTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.run_root = root / "run"
        self.clip_root = root / "clips"
        self.run_root.mkdir()
        self.clip_root.mkdir()
        self.w = load_worker()
        self.events = []
        self.chunks = []

    def tearDown(self):
        self.tmp.cleanup()

    def stub_batch(self, fail_on=None):
        w = self.w
        chunks = self.chunks

        def fake_batch(voice, jobs, batch_size=16):
            chunks.append([text for text, _ in jobs])
            for text, path in jobs:
                if fail_on is not None and fail_on in text:
                    raise RuntimeError("simulated batch failure")
                with wave.open(str(path), "wb") as out:
                    out.setnchannels(1)
                    out.setsampwidth(2)
                    out.setframerate(22050)
                    out.writeframes(b"\x00\x00" * 2205)
        w.synthesize_cue_batch = fake_batch

    def run_preload(self, texts, **kwargs):
        cues = [make_cue(i, t, start=float(i * 3), end=float(i * 3 + 2)) for i, t in enumerate(texts)]
        return self.w.preload_probes(
            object(), cues, texts, self.run_root, self.clip_root,
            "test-sha", self.events.append,
            [(i, cues[i], texts[i]) for i in range(len(texts))], **kwargs)

    def test_chunk_isolation(self):
        # 20 cues, batch 8 -> 3 chunks; middle chunk contains the poison text.
        texts = [f"cau thoai so {i}" for i in range(20)]
        texts[10] = "BADPOISON cau loi"
        self.stub_batch(fail_on="BADPOISON")
        got = self.run_preload(texts, batch_size=8)
        # Bucketing puts the longest (poison) text in the last 4-item chunk;
        # only that chunk is missing, the other 16 survive with one notice.
        self.assertEqual(len(got), 16)
        fallbacks = [e for e in self.events if e.get("code") == "batch-probe-chunk-fallback"]
        self.assertEqual(len(fallbacks), 1)
        # Every returned path exists on disk (fit_cue reuses by existence check).
        for path in got:
            self.assertTrue(Path(path).is_file())
        # Poison text's own path must NOT be reusable.
        w = self.w
        bad_key = w.cache_identity(make_cue(10, texts[10], start=30.0, end=32.0), texts[10], "test-sha")
        self.assertNotIn(str(self.run_root / (bad_key + "-native.wav")), got)

    def test_bucketing_groups_by_length(self):
        texts = [f"n{i}" for i in range(4)] + [f"cau dai so {i} them vai chu" for i in range(4)]
        self.stub_batch()
        got = self.run_preload(texts, batch_size=4)
        self.assertEqual(len(got), 8)
        # With bucketing, no chunk mixes short and long texts.
        for chunk in self.chunks:
            lens = {len(t) for t in chunk}
            self.assertEqual(len(lens), 1)

    def test_cache_skip_and_dedup(self):
        w = self.w
        texts = ["lap lai", "lap lai", "moi"]
        cues = [make_cue(i, t, start=float(i * 3), end=float(i * 3 + 2)) for i, t in enumerate(texts)]
        key0 = w.cache_identity(cues[0], texts[0], "test-sha")
        (self.clip_root / (key0 + ".wav")).write_bytes(b"\x00" * 64)
        (self.clip_root / (key0 + ".json")).write_bytes(b"{}")
        self.stub_batch()
        got = w.preload_probes(
            object(), cues, texts, self.run_root, self.clip_root,
            "test-sha", self.events.append,
            [(i, cues[i], texts[i]) for i in range(3)], batch_size=8)
        # 'lap lai'@cue0 cached -> skipped. The other two differ by window/id
        # (dedup is by full key, not text) -> one batch of 2.
        self.assertEqual(len(got), 2)
        self.assertEqual(len(self.chunks), 1)
        self.assertEqual(len(self.chunks[0]), 2)

    def test_empty_returns_empty(self):
        self.stub_batch()
        got = self.w.preload_probes(
            object(), [], [], self.run_root, self.clip_root,
            "test-sha", self.events.append, [], batch_size=8)
        self.assertEqual(got, {})
        self.assertEqual(self.chunks, [])


if __name__ == "__main__":
    unittest.main(verbosity=1)

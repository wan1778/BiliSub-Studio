#!/usr/bin/env python3
"""Offline duration/byte-preservation definitions; not a speech or runtime PASS."""
import importlib.util
import inspect
import math
from pathlib import Path
import tempfile
import unittest
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("tts_duration_worker", ROOT / "internal/tts/worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class VoiceDurationContract(unittest.TestCase):
    def cue(self):
        return {"id": "cue-one", "cue_start": 1.0, "cue_end": 5.0, "voice_start": 1.5,
                "voice_end": 3.5, "timing_source": "whisper", "voice": worker.VOICE_NAME, "text": "Xin chào."}

    def test_two_seconds_means_44100_frames_not_full_srt_window(self):
        start, frames = worker.cue_window(self.cue())
        self.assertEqual(start, 33075)
        self.assertEqual(frames, 44100)
        self.assertEqual(frames / worker.SAMPLE_RATE, 2)

    def test_absolute_rounding_prevents_one_sample_overrun(self):
        cue = self.cue() | {"voice_start": 1.00003, "voice_end": 3.00006}
        start, frames = worker.cue_window(cue)
        self.assertEqual(start + frames, round(cue["voice_end"] * worker.SAMPLE_RATE))

    def test_tempo_chain_preserves_requested_product(self):
        for ratio in (.125, .4, .8, 1, 1.2, 1.5, 2, 3, 8):
            factors = [float(part.split("=")[1]) for part in worker.tempo_filter(ratio).split(",")]
            self.assertTrue(all(.5 <= factor <= 2 for factor in factors))
            self.assertAlmostEqual(math.prod(factors), ratio, places=9)
        for ratio in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                worker.tempo_filter(ratio)

    def test_quality_warning_does_not_allow_wrong_duration(self):
        self.assertFalse(worker.needs_tempo_review(2.4, 44100, "whisper"))
        self.assertFalse(worker.needs_tempo_review(1.6, 44100, "whisper"))
        self.assertTrue(worker.needs_tempo_review(3, 44100, "whisper"))
        self.assertFalse(worker.needs_tempo_review(3, 220500, "sample"))

    def test_padding_copies_every_pcm_byte_and_refuses_overflow(self):
        # Tiny byte fixture only: no mock model or generated-speech claim.
        pcm = b"\x01\x00\xff\x7f\x00\x80\x02\x00"
        with tempfile.TemporaryDirectory(prefix="bilisub-duration-") as directory:
            raw, fitted = Path(directory) / "bytes.wav", Path(directory) / "padded.wav"
            with wave.open(str(raw), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(pcm)
            worker.pad_exact_clip(raw, fitted, 5)
            with wave.open(str(fitted), "rb") as source:
                self.assertEqual(source.getnframes(), 5)
                self.assertEqual(source.readframes(5), pcm + b"\0\0")
            with self.assertRaises(ValueError):
                worker.pad_exact_clip(raw, Path(directory) / "must-not-cut.wav", 3)
            self.assertFalse((Path(directory) / "must-not-cut.wav").exists())

    def test_changed_whisper_window_invalidates_same_srt_cache(self):
        cue = self.cue()
        original = worker.cache_identity(cue, "Xin chào.", "worker-sha")
        for changed in (cue | {"voice_start": 1.6}, cue | {"voice_end": 3.6}, cue | {"timing_source": "sample"}):
            self.assertNotEqual(worker.cache_identity(changed, "Xin chào.", "worker-sha"), original)

    def test_missing_source_window_is_not_silently_invented(self):
        manifest = {"schema": 2, "engine_version": worker.ENGINE_VERSION, "voice": worker.VOICE_NAME,
                    "timing_algorithm": worker.TIMING_ALGORITHM, "duration": 6, "cues": [self.cue()]}
        self.assertEqual(worker.validate_manifest(manifest, worker.VOICE_NAME), manifest["cues"])
        for change in ({"voice_end": 1.5}, {"timing_source": "sample"}, {"timing_source": "srt-fallback"}):
            with self.assertRaises(ValueError):
                worker.validate_manifest(manifest | {"cues": [self.cue() | change]}, worker.VOICE_NAME)

    def test_production_path_has_no_tail_cut_and_checks_actual_wav(self):
        fit = inspect.getsource(worker.fit_cue)
        self.assertIn("tempo_filter(tempo)", fit)
        self.assertIn("len(read_wav(final_path)) != target_frames", fit)
        self.assertNotIn("synthesize_cue(", fit)
        self.assertNotIn('"-t"', fit)
        self.assertNotIn("atrim=", fit)
        main = inspect.getsource(worker.main)
        self.assertIn('end_sample = start_sample + record["frames"]', main)
        self.assertNotIn("end_sample = min(", main)
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != targetFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)


if __name__ == "__main__":
    unittest.main()

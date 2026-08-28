#!/usr/bin/env python3
"""Offline duration/byte-preservation definitions; not a speech or runtime PASS."""
import importlib.util
import inspect
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

    def test_native_duration_controller_direction_and_bounds(self):
        # Frame-count arithmetic only; these are not pretend model outputs.
        self.assertAlmostEqual(worker.next_length_scale(1, 52920, 44100, 1, None, None), 2 / 2.4)
        self.assertAlmostEqual(worker.next_length_scale(1, 35280, 44100, 1, None, None), 2 / 1.6)
        self.assertAlmostEqual(worker.next_length_scale(1.1, 52920, 44100, 1.1, None, None), 1.1 * 2 / 2.4)
        self.assertAlmostEqual(worker.next_length_scale(1, 60000, 44100, 1, .8, 1), .9)
        self.assertEqual(worker.next_length_scale(1, 1000000, 44100, 1, None, None), .5)
        self.assertEqual(worker.next_length_scale(1, 1000, 44100, 1, None, None), 2)
        for scale in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                worker.next_length_scale(scale, 52920, 44100, 1, None, None)

    def test_review_reflects_actual_model_rate_not_playback_tempo(self):
        self.assertFalse(worker.needs_rate_review(.8, 1))
        self.assertFalse(worker.needs_rate_review(1.25, 1))
        self.assertTrue(worker.needs_rate_review(.79, 1))
        self.assertTrue(worker.needs_rate_review(1.26, 1))
        self.assertFalse(worker.needs_rate_review(1.5, 1.5))

    def test_native_metadata_rejects_old_method_and_excess_silence(self):
        record = {"fit_method": "piper-length-scale", "raw_duration": 2.4, "frames": 44100,
                  "generated_frames": 43800, "padding_frames": 300, "base_length_scale": 1,
                  "length_scale": .83, "synthesis_attempts": 2, "status": "fit"}
        self.assertTrue(worker.native_record_valid(record, 44100, False))
        for change in ({"fit_method": "atempo"}, {"padding_frames": 1000, "generated_frames": 43100},
                       {"generated_frames": 43700}, {"base_length_scale": 0}, {"length_scale": .4},
                       {"length_scale": float("nan")}, {"synthesis_attempts": 0}, {"synthesis_attempts": 11},
                       {"synthesis_attempts": 1}, {"status": "review"}):
            self.assertFalse(worker.native_record_valid(record | change, 44100, False))
        sample = record | {"raw_duration": 2, "generated_frames": 44100, "padding_frames": 0,
                           "length_scale": 1, "synthesis_attempts": 1}
        self.assertTrue(worker.native_record_valid(sample, 220500, True))
        self.assertFalse(worker.native_record_valid(sample | {"synthesis_attempts": 2}, 220500, True))
        self.assertEqual(worker.padding_budget(44100), 882)
        self.assertEqual(worker.padding_budget(22050), 441)

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
            with self.assertRaises(ValueError):
                worker.pad_exact_clip(raw, Path(directory) / "must-not-pad-seconds.wav", 44100)
            self.assertFalse((Path(directory) / "must-not-pad-seconds.wav").exists())

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

    def test_production_uses_native_whole_cue_retries_not_audio_speed_filters(self):
        fit = inspect.getsource(worker.fit_cue)
        self.assertIn("range(1, MAX_SYNTHESIS_ATTEMPTS + 1)", fit)
        self.assertIn("synthesize_cue(voice, text, candidate, scale)", fit)
        self.assertIn("next_length_scale(", fit)
        self.assertIn("on_attempt(attempt, scale)", fit)
        self.assertIn("output_frames != frames + padding", fit)
        self.assertNotIn("subprocess", fit)
        self.assertNotIn("ffmpeg", fit.lower())
        synthesis = inspect.getsource(worker.synthesize_cue)
        self.assertIn("SynthesisConfig(length_scale=length_scale)", synthesis)
        self.assertIn("voice.synthesize(text, syn_config=syn_config)", synthesis)
        self.assertNotIn("split(", synthesis)
        source = inspect.getsource(worker)
        for forbidden in ('"-t"', '"-af"', "atempo=", "asetrate=", "rubberband=", "atrim="):
            self.assertNotIn(forbidden, source)
        main = inspect.getsource(worker.main)
        self.assertIn('end_sample = start_sample + record["frames"]', main)
        self.assertNotIn("end_sample = min(", main)
        self.assertIn('"synthesis_calls": 0 if cache_hit else record["synthesis_attempts"]', main)
        self.assertIn('"event": "attempt"', main)
        self.assertEqual(worker.TIMING_ALGORITHM, "whole-cue-piper-rate-v4")
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != targetFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)
        self.assertIn("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate)", service)
        self.assertIn('kind == "attempt"', service)
        self.assertIn("(index - 1) / (double)total * 53", service)


if __name__ == "__main__":
    unittest.main()

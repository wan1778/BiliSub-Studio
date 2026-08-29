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
        self.assertEqual((worker.MIN_RATE_SCALE, worker.MAX_RATE_SCALE), (.85, 1.2))
        self.assertEqual((worker.MIN_PREFERRED_RATE_SCALE, worker.MAX_PREFERRED_RATE_SCALE), (.9, 1.15))
        self.assertAlmostEqual(worker.next_length_scale(1, 52920, 44100, 1, None, None), .85)
        self.assertAlmostEqual(worker.next_length_scale(1, 35280, 44100, 1, None, None), 1.2)
        self.assertAlmostEqual(worker.next_length_scale(1.1, 52920, 44100, 1.1, None, None), 1.1 * .85)
        self.assertAlmostEqual(worker.next_length_scale(1, 60000, 44100, 1, .8, 1), .9)
        self.assertEqual(worker.next_length_scale(1, 1000000, 44100, 1, None, None), .85)
        self.assertEqual(worker.next_length_scale(1, 1000, 44100, 1, None, None), 1.2)
        for scale in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                worker.next_length_scale(scale, 52920, 44100, 1, None, None)

    def test_review_reflects_actual_model_rate_not_playback_tempo(self):
        self.assertFalse(worker.needs_rate_review(.9, 1))
        self.assertFalse(worker.needs_rate_review(1.15, 1))
        self.assertTrue(worker.needs_rate_review(.89, 1))
        self.assertTrue(worker.needs_rate_review(1.16, 1))
        self.assertFalse(worker.needs_rate_review(1.5, 1.5))

    def test_native_metadata_keeps_whole_speech_and_reviews_long_tail_silence(self):
        record = {"fit_method": "piper-length-scale", "raw_duration": 2.4, "frames": 44100,
                  "source_frames": 43800, "generated_frames": 43800,
                  "trimmed_silence_frames": 0, "padding_frames": 300, "base_length_scale": 1,
                  "length_scale": .95, "synthesis_attempts": 2, "status": "fit"}
        self.assertTrue(worker.native_record_valid(record, 44100, False))
        self.assertTrue(worker.native_record_valid(record | {"length_scale": .88, "status": "review"}, 44100, False))
        long_tail = record | {"source_frames": 43100, "padding_frames": 1000,
                              "generated_frames": 43100, "status": "review"}
        self.assertTrue(worker.native_record_valid(long_tail, 44100, False))
        self.assertFalse(worker.native_record_valid(long_tail | {"status": "fit"}, 44100, False))
        trimmed_tail = record | {"source_frames": 45000, "generated_frames": 44100,
                                 "trimmed_silence_frames": 900, "padding_frames": 0,
                                 "status": "review"}
        self.assertTrue(worker.native_record_valid(trimmed_tail, 44100, False))
        for change in ({"fit_method": "atempo"}, {"padding_frames": 44100, "generated_frames": 0},
                       {"generated_frames": 43700}, {"base_length_scale": 0}, {"length_scale": .84},
                       {"length_scale": float("nan")}, {"synthesis_attempts": 0}, {"synthesis_attempts": 11},
                       {"synthesis_attempts": 1}, {"status": "review"}):
            self.assertFalse(worker.native_record_valid(record | change, 44100, False))
        sample = record | {"raw_duration": 2, "source_frames": 44100, "generated_frames": 44100,
                           "trimmed_silence_frames": 0, "padding_frames": 0,
                           "length_scale": 1, "synthesis_attempts": 1}
        self.assertTrue(worker.native_record_valid(sample, 220500, True))
        self.assertFalse(worker.native_record_valid(sample | {"synthesis_attempts": 2}, 220500, True))
        self.assertEqual(worker.padding_budget(44100), 882)
        self.assertEqual(worker.padding_budget(22050), 441)

    def test_padding_copies_every_pcm_byte_and_refuses_cutting(self):
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
            long_tail = Path(directory) / "long-tail.wav"
            worker.pad_exact_clip(raw, long_tail, 44100)
            with wave.open(str(long_tail), "rb") as source:
                self.assertEqual(source.getnframes(), 44100)
                self.assertEqual(source.readframes(4), pcm)

    def test_only_trailing_silence_beyond_guard_can_be_trimmed(self):
        guard = worker.SILENCE_GUARD_FRAMES
        with tempfile.TemporaryDirectory(prefix="bilisub-silence-trim-") as directory:
            raw, fitted = Path(directory) / "raw.wav", Path(directory) / "fitted.wav"
            target = guard + 4
            pcm = b"\xff\x7f" * 4 + b"\0\0" * (target + 100)
            with wave.open(str(raw), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(pcm)
            self.assertEqual(worker.trim_trailing_silence_to_fit(raw, fitted, target), 104)
            with wave.open(str(fitted), "rb") as source:
                self.assertEqual(source.getnframes(), target)
                self.assertEqual(source.readframes(4), b"\xff\x7f" * 4)

            unsafe = Path(directory) / "unsafe.wav"
            unsafe_pcm = b"\0\0" * target + b"\xff\x7f" + b"\0\0" * 99
            with wave.open(str(unsafe), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(unsafe_pcm)
            self.assertEqual(worker.trim_trailing_silence_to_fit(unsafe, fitted, target), 0)

    def test_changed_whisper_window_invalidates_same_srt_cache(self):
        cue = self.cue()
        original = worker.cache_identity(cue, "Xin chào.", "worker-sha")
        for changed in (cue | {"voice_start": 1.6}, cue | {"voice_end": 3.6}, cue | {"timing_source": "sample"}):
            self.assertNotEqual(worker.cache_identity(changed, "Xin chào.", "worker-sha"), original)

    def test_srt_fallback_is_explicit_and_must_use_the_complete_cue(self):
        manifest = {"schema": 2, "engine_version": worker.ENGINE_VERSION, "voice": worker.VOICE_NAME,
                    "timing_algorithm": worker.TIMING_ALGORITHM, "duration": 6, "cues": [self.cue()]}
        self.assertEqual(worker.validate_manifest(manifest, worker.VOICE_NAME), manifest["cues"])
        fallback = self.cue() | {"voice_start": 1, "voice_end": 5, "timing_source": "srt-fallback"}
        self.assertEqual(worker.validate_manifest(manifest | {"cues": [fallback]}, worker.VOICE_NAME), [fallback])
        for change in ({"voice_end": 1.5}, {"timing_source": "sample"}, {"timing_source": "unknown"},
                       {"timing_source": "srt-fallback"}):
            with self.assertRaises(ValueError):
                worker.validate_manifest(manifest | {"cues": [self.cue() | change]}, worker.VOICE_NAME)

    def test_failed_whisper_window_has_one_explicit_full_srt_fallback(self):
        cue = self.cue()
        fallback = worker.srt_fallback_cue(cue)
        self.assertEqual(fallback["voice_start"], cue["cue_start"])
        self.assertEqual(fallback["voice_end"], cue["cue_end"])
        self.assertEqual(fallback["timing_source"], "srt-fallback")
        self.assertNotEqual(worker.cache_identity(cue, cue["text"], "worker-sha"),
                            worker.cache_identity(fallback, cue["text"], "worker-sha"))
        self.assertIsNone(worker.srt_fallback_cue(fallback))

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
        self.assertIn('candidate_cues.append(fallback)', main)
        self.assertIn('"synthesis_calls": synthesis_calls', main)
        self.assertIn('"event": "attempt"', main)
        self.assertEqual(worker.TIMING_ALGORITHM, "whole-cue-piper-rate-v8")
        self.assertIn("trim_trailing_silence_to_fit", fit)
        self.assertIn("biên giữ chất giọng 0,85–1,20×", source)
        self.assertIn('cue["timing_source"] == "srt-fallback"', source)
        self.assertIn('output_status = "review"', source)
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != targetFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)
        self.assertIn("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate)", service)
        self.assertIn('fallback ? "srt-fallback" : "whisper"', service)
        self.assertIn('expected.TimingSource == "whisper" && cue.TimingSource == "srt-fallback"', service)
        self.assertIn('kind == "attempt"', service)
        self.assertIn("(index - 1) / (double)total * 53", service)
        timing = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.Timing.cs").read_text(encoding="utf-8")
        self.assertIn("relativeScale is < .85 or > 1.20", timing)
        self.assertIn("cue.PaddingFrames > precisionPaddingBudget", timing)
        installer = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs").read_text(encoding="utf-8")
        self.assertIn('TimingAlgorithm = "whole-cue-piper-rate-v8"', installer)


if __name__ == "__main__":
    unittest.main()

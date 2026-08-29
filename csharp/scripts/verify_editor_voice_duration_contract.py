#!/usr/bin/env python3
"""Offline duration/byte-preservation definitions; not a speech or runtime PASS."""
import importlib.util
import inspect
from pathlib import Path
import tempfile
import types
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

        grouped = record | {"timing_source": "sentence-group", "length_scale": .5, "status": "review"}
        self.assertTrue(worker.native_record_valid(grouped, 44100, False))
        self.assertFalse(worker.native_record_valid(grouped | {"length_scale": .44}, 44100, False))
        self.assertFalse(worker.native_record_valid(grouped | {"status": "fit"}, 44100, False))

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

    def test_contiguous_subtitle_fragments_form_bounded_sentence_groups(self):
        def cue(index, start, end, timing_source="srt-fallback"):
            return {"id": f"cue-{index}", "cue_start": start, "cue_end": end,
                    "voice_start": start, "voice_end": end, "timing_source": timing_source,
                    "voice": worker.VOICE_NAME, "text": f"fragment {index}"}

        cues = [cue(9, 14.4, 15.2), cue(10, 15.2, 16.4), cue(11, 16.4, 17.2), cue(12, 17.2, 18.4)]
        texts = ["Có đệ tử bầu bạn,", "sư phụ không còn cô độc,",
                 "đương nhiên ngày nào cũng vui vẻ,", "luôn được hạnh phúc vây quanh."]
        self.assertEqual(worker.sentence_groups(cues, texts), [[0, 1, 2, 3]])
        self.assertEqual(worker.sentence_groups(cues, [texts[0] + ".", *texts[1:]]), [[0], [1, 2, 3]])
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"cue_start": 15.4, "voice_start": 15.4}], texts[:2]),
                         [[0], [1]])
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"timing_source": "sample"}], texts[:2]),
                         [[0], [1]])

    def test_sentence_group_borrows_time_without_cutting_pcm_or_crossing_sentence_boundary(self):
        cues = [
            {"id": "cue-9", "cue_start": 0, "cue_end": .8, "voice_start": 0, "voice_end": .8,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "cue-10", "cue_start": .8, "cue_end": 2, "voice_start": .8, "voice_end": 2,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
            {"id": "cue-11", "cue_start": 2, "cue_end": 2.8, "voice_start": 2, "voice_end": 2.8,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "c"},
            {"id": "cue-12", "cue_start": 2.8, "cue_end": 4, "voice_start": 2.8, "voice_end": 4,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "d"},
        ]
        native_frames = {"a": 26000, "b": 33000, "c": 39000, "d": 35000}
        original = worker.synthesize_cue

        def fake_synthesize(_voice, text, path, scale):
            active = max(1, round(native_frames[text] * scale))
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * active + b"\0\0" * 2000)

        attempts = []
        worker.synthesize_cue = fake_synthesize
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-sentence-group-") as directory:
                root = Path(directory)
                (root / "clips").mkdir()
                entries = worker.synthesize_sentence_group(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    cues, ["a", "b", "c", "d"], root, root / "clips", "worker-sha",
                    root / "plan.json", "plan-key",
                    lambda local, attempt, scale: attempts.append((local, attempt, scale)))
                self.assertEqual(len(entries), 4)
                windows = [entry[0] for entry in entries]
                self.assertEqual(round(windows[0]["voice_start"] * worker.SAMPLE_RATE), 0)
                self.assertEqual(round(windows[-1]["voice_end"] * worker.SAMPLE_RATE), 4 * worker.SAMPLE_RATE)
                for previous, current in zip(windows, windows[1:]):
                    self.assertEqual(round(previous["voice_end"] * worker.SAMPLE_RATE),
                                     round(current["voice_start"] * worker.SAMPLE_RATE))
                self.assertTrue(any(round(window["voice_end"] * worker.SAMPLE_RATE)
                                    != round(cue["cue_end"] * worker.SAMPLE_RATE)
                                    for window, cue in zip(windows[:-1], cues[:-1])))
                for active, _key, clip, record, cache_hit, calls in entries:
                    _start, target = worker.cue_window(active)
                    self.assertTrue(worker.native_record_valid(record, target, False))
                    self.assertEqual(worker.read_wav(clip).shape[0], target)
                    self.assertFalse(cache_hit)
                    self.assertEqual(calls, record["synthesis_attempts"])
                self.assertGreater(len(attempts), 4)
        finally:
            worker.synthesize_cue = original

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
        self.assertEqual(worker.TIMING_ALGORITHM, "whole-cue-piper-sentence-group-v9")
        self.assertIn("trim_trailing_silence_to_fit", fit)
        self.assertIn("biên giữ chất giọng 0,85–1,20×", source)
        self.assertIn('cue["timing_source"] == "srt-fallback"', source)
        self.assertIn('active_cue["timing_source"] in ("srt-fallback", "sentence-group")', source)
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != targetFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)
        self.assertIn("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate)", service)
        self.assertIn('fallback ? "srt-fallback" : "whisper"', service)
        self.assertIn('expected.TimingSource == "whisper" && cue.TimingSource == "srt-fallback"', service)
        self.assertIn('cue.TimingSource == "sentence-group"', service)
        self.assertIn("ValidateSentenceGroupWindows(result.Cues, expectedCues, expectedSampleRate)", service)
        self.assertIn('kind == "attempt"', service)
        self.assertIn("(index - 1) / (double)total * 53", service)
        timing = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.Timing.cs").read_text(encoding="utf-8")
        self.assertIn('cue.TimingSource == "sentence-group" ? .45 : .85', timing)
        self.assertIn("cue.PaddingFrames > precisionPaddingBudget", timing)
        installer = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs").read_text(encoding="utf-8")
        self.assertIn('TimingAlgorithm = "whole-cue-piper-sentence-group-v9"', installer)


if __name__ == "__main__":
    unittest.main()

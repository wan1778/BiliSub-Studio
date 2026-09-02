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

    def test_vietnamese_normalization_keeps_canonical_tone_marks(self):
        class EchoNormalizer:
            @staticmethod
            def normalize(text):
                return text.lower()

        decomposed = "Sư phu\u0323"
        self.assertEqual(worker.normalize_vietnamese_text(EchoNormalizer(), decomposed), "sư phụ")
        self.assertEqual(worker.normalize_vietnamese_text(EchoNormalizer(), "Đệ tử của Sư Phụ"),
                         "đệ tử của sư phụ")
        with self.assertRaises(ValueError):
            worker.normalize_vietnamese_text(EchoNormalizer(), "   ")

    def test_native_duration_controller_direction_and_bounds(self):
        # Frame-count arithmetic only; these are not pretend model outputs.
        self.assertEqual((worker.MIN_RATE_SCALE, worker.MAX_RATE_SCALE), (.30, 1.2))
        self.assertEqual((worker.MIN_PREFERRED_RATE_SCALE, worker.MAX_PREFERRED_RATE_SCALE), (.70, 1.0))
        self.assertAlmostEqual(worker.next_length_scale(1, 52920, 44100, 1, None, None), 44100 / 52920)
        self.assertAlmostEqual(worker.next_length_scale(1, 35280, 44100, 1, None, None), 1.2)
        self.assertAlmostEqual(worker.next_length_scale(1.1, 52920, 44100, 1.1, None, None), 1.1 * 44100 / 52920)
        self.assertAlmostEqual(worker.next_length_scale(1, 60000, 44100, 1, .8, 1), .9)
        self.assertEqual(worker.next_length_scale(1, 1000000, 44100, 1, None, None), .30)
        self.assertEqual(worker.next_length_scale(1, 1000, 44100, 1, None, None), 1.2)
        for scale in (0, -1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                worker.next_length_scale(scale, 52920, 44100, 1, None, None)

    def test_review_reflects_actual_model_rate_not_playback_tempo(self):
        self.assertFalse(worker.needs_rate_review(.70, 1))
        self.assertFalse(worker.needs_rate_review(1.0, 1))
        self.assertTrue(worker.needs_rate_review(.69, 1))
        self.assertTrue(worker.needs_rate_review(1.01, 1))
        self.assertFalse(worker.needs_rate_review(1.05, 1.5))

    def test_native_metadata_accepts_natural_first_and_accelerated_whole_speech(self):
        natural = {"fit_method": "piper-length-scale", "raw_duration": 43800 / worker.SAMPLE_RATE,
                   "frames": 44100, "source_frames": 43800, "generated_frames": 43800,
                   "trimmed_silence_frames": 0, "padding_frames": 300, "base_length_scale": 1,
                   "length_scale": 1, "synthesis_attempts": 1, "status": "fit",
                   "native_reference_frames": 43800, "group_native_frames": 0,
                   "actual_speed_factor": 1.0, "timing_source": "srt-fallback"}
        self.assertTrue(worker.native_record_valid(natural, 44100, False))
        self.assertFalse(worker.native_record_valid(natural | {"length_scale": .99}, 44100, False))
        self.assertFalse(worker.native_record_valid(natural | {"native_reference_frames": 43801}, 44100, False))

        record = natural | {"raw_duration": 59130 / worker.SAMPLE_RATE,
                            "source_frames": 43800, "generated_frames": 43800,
                            "padding_frames": 300, "length_scale": .65,
                            "synthesis_attempts": 2, "status": "review",
                            "native_reference_frames": 59130, "actual_speed_factor": 1.35}
        self.assertTrue(worker.native_record_valid(record, 44100, False))
        self.assertTrue(worker.native_record_valid(record | {"length_scale": .70, "status": "fit"}, 44100, False))
        long_tail = record | {"source_frames": 43100, "padding_frames": 1000,
                              "generated_frames": 43100, "native_reference_frames": 58185}
        self.assertTrue(worker.native_record_valid(long_tail, 44100, False))
        self.assertFalse(worker.native_record_valid(long_tail | {"status": "fit"}, 44100, False))
        trimmed_tail = record | {"source_frames": 45000, "generated_frames": 44100,
                                 "trimmed_silence_frames": 900, "padding_frames": 0,
                                 "native_reference_frames": 59535}
        self.assertTrue(worker.native_record_valid(trimmed_tail, 44100, False))
        for change in ({"fit_method": "atempo"}, {"padding_frames": 44100, "generated_frames": 0},
                       {"generated_frames": 43700}, {"base_length_scale": 0}, {"length_scale": .299},
                       {"length_scale": float("nan")}, {"synthesis_attempts": 0}, {"synthesis_attempts": 13},
                       {"status": "fit"}, {"actual_speed_factor": .999}):
            self.assertFalse(worker.native_record_valid(record | change, 44100, False))
        sample = natural | {"raw_duration": 44100 / worker.SAMPLE_RATE, "frames": 220500,
                            "target_frames": 220500, "source_frames": 44100, "generated_frames": 44100,
                            "trimmed_silence_frames": 0, "padding_frames": 176400,
                            "native_reference_frames": 44100, "timing_source": "sample", "status": "review"}
        self.assertTrue(worker.native_record_valid(sample, 220500, True))
        self.assertFalse(worker.native_record_valid(sample | {"timing_source": "srt-fallback"}, 220500, True))
        self.assertEqual(worker.padding_budget(44100), 882)
        self.assertEqual(worker.padding_budget(22050), 441)

        grouped = record | {"timing_source": "sentence-group", "length_scale": .50,
                            "group_native_frames": 59130}
        self.assertTrue(worker.native_record_valid(grouped, 44100, False))
        self.assertTrue(worker.native_record_valid(
            grouped | {"synthesis_attempts": worker.MAX_GROUP_SYNTHESIS_ATTEMPTS}, 44100, False))
        self.assertFalse(worker.native_record_valid(grouped | {"length_scale": .299}, 44100, False))
        self.assertFalse(worker.native_record_valid(
            grouped | {"synthesis_attempts": worker.MAX_GROUP_SYNTHESIS_ATTEMPTS + 1}, 44100, False))
        self.assertTrue(worker.native_record_valid(grouped | {"actual_speed_factor": 1.401}, 44100, False))
        self.assertFalse(worker.native_record_valid(grouped | {"status": "fit"}, 44100, False))

        tempo = {"fit_method": "piper-atempo", "raw_duration": 90000 / worker.SAMPLE_RATE,
                 "frames": 44100, "source_frames": 90000, "tempo_input_frames": 88000,
                 "generated_frames": 40000, "trimmed_silence_frames": 2000, "padding_frames": 4100,
                 "base_length_scale": 1, "length_scale": .30, "tempo_factor": 2.2,
                 "tempo_attempts": 1, "synthesis_attempts": 12, "status": "review",
                 "native_reference_frames": 80000, "group_native_frames": 0,
                 "actual_speed_factor": 2.0, "timing_source": "srt-fallback"}
        self.assertTrue(worker.native_record_valid(tempo, 44100, False))
        for change in ({"tempo_input_frames": 87999}, {"tempo_factor": 2.1},
                       {"tempo_attempts": 0}, {"tempo_attempts": 7},
                       {"generated_frames": 88000, "padding_frames": -43900}):
            self.assertFalse(worker.native_record_valid(tempo | change, 44100, False))

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

    def test_individual_cue_keeps_natural_rate_when_it_already_fits(self):
        original = worker.synthesize_cue
        calls = []

        def fake_synthesize(_voice, _text, path, scale):
            calls.append(scale)
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * 30000)

        worker.synthesize_cue = fake_synthesize
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-natural-first-") as directory:
                root = Path(directory)
                clip, record = worker.fit_cue(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    "xin chào", 44100, root, "cue", "srt-fallback", Path("ffmpeg"), lambda *_: None)
                self.assertEqual(calls, [1.0])
                self.assertEqual(record["synthesis_attempts"], 1)
                self.assertEqual(record["actual_speed_factor"], 1)
                self.assertEqual(record["length_scale"], record["base_length_scale"])
                self.assertEqual(record["generated_frames"], 30000)
                self.assertEqual(len(worker.read_wav(clip)), 44100)
                self.assertTrue(worker.native_record_valid(record, 44100, False))
        finally:
            worker.synthesize_cue = original

    def test_individual_cue_uses_only_required_speed_without_fixed_fast_floor(self):
        original = worker.synthesize_cue
        calls = []

        def fake_synthesize(_voice, _text, path, scale):
            calls.append(scale)
            frames = max(1, round(46000 * scale))
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * frames)

        worker.synthesize_cue = fake_synthesize
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-minimum-rate-") as directory:
                root = Path(directory)
                clip, record = worker.fit_cue(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    "xin chào", 44100, root, "cue", "srt-fallback", Path("ffmpeg"), lambda *_: None)
                self.assertEqual(len(calls), 2)
                self.assertEqual(calls[0], 1.0)
                self.assertLess(calls[1], 1.0)
                self.assertGreater(record["actual_speed_factor"], 46000 / 44100)
                self.assertLess(record["actual_speed_factor"], 1.10)
                self.assertEqual(len(worker.read_wav(clip)), 44100)
                self.assertTrue(worker.native_record_valid(record, 44100, False))
        finally:
            worker.synthesize_cue = original

    def test_dynamic_tempo_fallback_is_bounded_and_cutting_filters_are_absent(self):
        source = inspect.getsource(worker)
        self.assertIn("atempo=", source)
        self.assertTrue(hasattr(worker, "tempo_fit_clip"))
        self.assertEqual(worker.MAX_TEMPO_ATTEMPTS, 6)
        self.assertNotIn("atrim=", source)
        self.assertNotIn("-t", inspect.getsource(worker.apply_atempo))
        self.assertFalse(hasattr(worker, "fit_cue_with_tempo"))

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

    def test_truncated_audio_cannot_masquerade_as_a_spoken_sentence(self):
        minimum = worker.minimum_speech_frames("Không sai, ta phải đi rồi.")
        self.assertGreaterEqual(minimum, round(.35 * worker.SAMPLE_RATE))
        record = {
            "fit_method": "piper-length-scale", "frames": 733, "target_frames": 733,
            "source_frames": 44300,
            "generated_frames": 578, "trimmed_silence_frames": 43722,
            "padding_frames": 155, "raw_duration": 44300 / worker.SAMPLE_RATE,
            "fitted_duration": 733 / worker.SAMPLE_RATE, "base_length_scale": 1,
            "length_scale": 1, "synthesis_attempts": 1, "status": "review",
        }
        self.assertFalse(worker.native_record_valid(record, 733, False, minimum))

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
        self.assertEqual(worker.sentence_groups(cues, [texts[0] + ".", *texts[1:]]), [[0, 1, 2, 3]])
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"cue_start": 31, "voice_start": 31}], texts[:2]),
                         [[0], [1]])
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"timing_source": "sample"}], texts[:2]),
                         [[0], [1]])
        self.assertEqual(worker.sentence_groups(
            [cues[0], cues[1] | {"cue_start": .801, "voice_start": .801}], texts[:2]), [[0], [1]])

    def test_sentence_group_borrows_time_without_cutting_pcm_when_native_rate_fits(self):
        cues = [
            {"id": "cue-9", "cue_start": 0, "cue_end": .8, "voice_start": 0, "voice_end": .8,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "cue-10", "cue_start": .8, "cue_end": 2, "voice_start": .8, "voice_end": 2,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
            {"id": "cue-11", "cue_start": 2, "cue_end": 2.8, "voice_start": 2, "voice_end": 2.8,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "c"},
            {"id": "cue-12", "cue_start": 2.8, "cue_end": 6.2, "voice_start": 2.8, "voice_end": 6.2,
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
                    root / "plan.json", "plan-key", Path("ffmpeg"),
                    lambda local, attempt, scale: attempts.append((local, attempt, scale)))
                self.assertEqual(len(entries), 4)
                windows = [entry[0] for entry in entries]
                self.assertEqual(round(windows[0]["voice_start"] * worker.SAMPLE_RATE), 0)
                self.assertEqual(round(windows[-1]["voice_end"] * worker.SAMPLE_RATE), round(6.2 * worker.SAMPLE_RATE))
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
                self.assertEqual(len(attempts), 4)
                self.assertTrue(all(worker.MIN_RATE_SCALE <= scale <= worker.MAX_RATE_SCALE
                                    for _local, _attempt, scale in attempts))
                self.assertTrue(all(record["synthesis_attempts"] == 1
                                    and record["actual_speed_factor"] == 1
                                    and record["length_scale"] == record["base_length_scale"]
                                    for _active, _key, _clip, record, _hit, _calls in entries))
        finally:
            worker.synthesize_cue = original

    def test_overlong_sentence_group_uses_only_bounded_native_piper_rate(self):
        cues = [
            {"id": "cue-a", "cue_start": 0, "cue_end": 1, "voice_start": 0, "voice_end": 1,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "cue-b", "cue_start": 1, "cue_end": 2, "voice_start": 1, "voice_end": 2,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
        ]
        original = worker.synthesize_cue
        attempts = []

        def fake_synthesize(_voice, _text, path, scale):
            active = max(1, round(27300 * scale))
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * active + b"\0\0" * 2000)

        worker.synthesize_cue = fake_synthesize
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-bounded-group-") as directory:
                root = Path(directory)
                (root / "clips").mkdir()
                entries = worker.synthesize_sentence_group(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    cues, ["a", "b"], root, root / "clips", "worker-sha",
                    root / "plan.json", "plan-key", Path("ffmpeg"),
                    lambda local, attempt, scale: attempts.append((local, attempt, scale)))
                self.assertEqual(len(entries), 2)
                selected_scales = {record["length_scale"] for _active, _key, _clip, record, _hit, _calls in entries}
                self.assertEqual(len(selected_scales), 1)
                selected_scale = selected_scales.pop()
                self.assertGreaterEqual(selected_scale, worker.MIN_GROUP_RATE_SCALE)
                self.assertLess(selected_scale, 1.0)
                self.assertLess(entries[0][3]["actual_speed_factor"], 1.30)
                self.assertTrue(all(scale >= worker.MIN_GROUP_RATE_SCALE for _local, _attempt, scale in attempts))
                self.assertTrue(any(scale < 1.0 for _local, _attempt, scale in attempts))
                self.assertEqual(sum(entry[3]["generated_frames"] + entry[3]["padding_frames"]
                                     for entry in entries), 2 * worker.SAMPLE_RATE)
                for active, _key, clip, record, _cache_hit, _calls in entries:
                    _start, target = worker.cue_window(active)
                    self.assertTrue(worker.native_record_valid(record, target, False))
                    self.assertEqual(worker.read_wav(clip).shape[0], target)
        finally:
            worker.synthesize_cue = original

    def test_sentence_group_uses_dynamic_tempo_after_piper_saturates(self):
        cues = [
            {"id": "cue-a", "cue_start": 0, "cue_end": 1, "voice_start": 0, "voice_end": 1,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "cue-b", "cue_start": 1, "cue_end": 2, "voice_start": 1, "voice_end": 2,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
        ]
        original_synthesize = worker.synthesize_cue
        original_atempo = worker.apply_atempo

        def fake_synthesize(_voice, _text, path, scale):
            # Deterministic frame fixture only; real NGHI audio is covered by the
            # separate pinned-model integration gate.
            active = 30000
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * active + b"\0\0" * 2000)

        def fake_atempo(_ffmpeg, source, output_path, factor):
            samples = worker.read_wav(source)
            generated = max(1, round(len(samples) / factor))
            with wave.open(str(output_path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * generated)
            return generated

        worker.synthesize_cue = fake_synthesize
        worker.apply_atempo = fake_atempo
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-dynamic-tempo-") as directory:
                root = Path(directory)
                (root / "clips").mkdir()
                entries = worker.synthesize_sentence_group(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    cues, ["a", "b"], root, root / "clips", "worker-sha",
                    root / "plan.json", "plan-key", Path("ffmpeg"), lambda *_args: None)
                self.assertEqual(len(entries), 2)
                for active, _key, clip, record, _cache_hit, _calls in entries:
                    _start, target = worker.cue_window(active)
                    self.assertEqual(record["fit_method"], "piper-atempo")
                    self.assertGreater(record["tempo_input_frames"], record["generated_frames"])
                    self.assertGreater(record["tempo_factor"], 1)
                    self.assertTrue(worker.native_record_valid(record, target, False))
                    self.assertEqual(len(worker.read_wav(clip)), target)
        finally:
            worker.synthesize_cue = original_synthesize
            worker.apply_atempo = original_atempo

    def test_sentence_group_converges_with_repeatable_duration_jitter(self):
        cues = [
            {"id": "cue-a", "cue_start": 0, "cue_end": 1, "voice_start": 0, "voice_end": 1,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "cue-b", "cue_start": 1, "cue_end": 2, "voice_start": 1, "voice_end": 2,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
        ]
        original = worker.synthesize_cue
        calls = 0
        jitter_by_attempt = (0, 650, -250, 500, -150, 300)

        def fake_synthesize(_voice, _text, path, scale):
            nonlocal calls
            attempt = calls // len(cues)
            calls += 1
            jitter = jitter_by_attempt[min(attempt, len(jitter_by_attempt) - 1)]
            active = max(1, round(27300 * scale) + jitter)
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, worker.SAMPLE_RATE, 0, "NONE", "not compressed"))
                output.writeframes(b"\xff\x7f" * active + b"\0\0" * 2000)

        worker.synthesize_cue = fake_synthesize
        try:
            with tempfile.TemporaryDirectory(prefix="bilisub-jitter-group-") as directory:
                root = Path(directory)
                (root / "clips").mkdir()
                entries = worker.synthesize_sentence_group(
                    types.SimpleNamespace(config=types.SimpleNamespace(length_scale=1.0)),
                    cues, ["a", "b"], root, root / "clips", "worker-sha",
                    root / "plan.json", "plan-key", Path("ffmpeg"), lambda *_args: None)
                attempts = entries[0][3]["synthesis_attempts"]
                self.assertLessEqual(attempts, worker.MAX_GROUP_SYNTHESIS_ATTEMPTS)
                self.assertTrue(all(entry[3]["actual_speed_factor"] <= worker.MAX_GROUP_ACTUAL_SPEED
                                    for entry in entries))
                self.assertEqual(sum(entry[3]["frames"] for entry in entries), 2 * worker.SAMPLE_RATE)
        finally:
            worker.synthesize_cue = original

    def test_production_uses_native_whole_cue_then_dynamic_pitch_preserving_tempo(self):
        fit = inspect.getsource(worker.fit_cue)
        self.assertIn("range(1, MAX_SYNTHESIS_ATTEMPTS + 1)", fit)
        self.assertIn("synthesize_cue(voice, text, candidate, scale)", fit)
        self.assertIn("native_reference_frames / frames", fit)
        self.assertIn("on_attempt(attempt, scale)", fit)
        self.assertIn("output_frames != frames + padding", fit)
        self.assertIn("tempo_fit_clip", fit)
        synthesis = inspect.getsource(worker.synthesize_cue)
        self.assertIn("SynthesisConfig(length_scale=length_scale)", synthesis)
        self.assertIn("voice.synthesize(text, syn_config=syn_config)", synthesis)
        self.assertNotIn("split(", synthesis)
        source = inspect.getsource(worker)
        for forbidden in ('"-t"', "asetrate=", "rubberband=", "atrim="):
            self.assertNotIn(forbidden, source)
        main = inspect.getsource(worker.main)
        self.assertIn('end_sample = start_sample + record["frames"]', main)
        self.assertNotIn("end_sample = min(", main)
        self.assertIn('candidate_cues.append(fallback)', main)
        self.assertIn('"synthesis_calls": synthesis_calls', main)
        self.assertIn('"event": "attempt"', main)
        self.assertEqual(worker.TIMING_ALGORITHM, "whole-cue-piper-natural-first-v17")
        self.assertIn('"piper-atempo"', source)
        self.assertIn('atempo_filter(factor)', source)
        self.assertNotIn("resolve_tempo", main)
        self.assertEqual(worker.MIN_GROUP_RATE_SCALE, .30)
        self.assertEqual(worker.MIN_ACTUAL_SPEED, 1.0)
        self.assertEqual(worker.FIT_HEADROOM, .995)
        self.assertEqual(worker.MAX_GROUP_ACTUAL_SPEED, 100.0)
        self.assertEqual(worker.MAX_TEMPO_ATTEMPTS, 6)
        self.assertEqual(worker.MAX_GROUP_GAP_FRAMES, 0)
        self.assertEqual(worker.MAX_GROUP_DURATION_FRAMES, 300 * worker.SAMPLE_RATE)
        self.assertEqual(worker.MAX_GROUP_SYNTHESIS_ATTEMPTS, 12)
        self.assertIn("trim_trailing_silence", fit)
        self.assertIn("MIN_GROUP_RATE_SCALE if record.get(\"timing_source\") == \"sentence-group\"", source)
        self.assertIn('cue["timing_source"] == "srt-fallback"', source)
        self.assertIn('active_cue["timing_source"] in ("srt-fallback", "sentence-group")', source)
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("EditorVoiceCuePlanner.Build(request.Subtitle.Cues)", service)
        self.assertIn("EditorVoiceCuePlanner.RemoveUnspeakable(", service)
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != targetFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)
        self.assertIn("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate)", service)
        self.assertIn('cue.Start, cue.End, "srt-fallback"', service)
        whole = service.split("internal static TtsCueManifest BuildWholeCue", 1)[1].split(
            "private static string ResolveVoice", 1)[0]
        self.assertNotIn("timing.SpeechStart", whole)
        self.assertNotIn("timing.SpeechEnd", whole)
        self.assertIn('expected.TimingSource == "whisper" && cue.TimingSource == "srt-fallback"', service)
        self.assertIn('cue.TimingSource == "sentence-group"', service)
        self.assertIn("ValidateSentenceGroupWindows(result.Cues, expectedCues, expectedSampleRate)", service)
        self.assertIn('kind == "attempt"', service)
        self.assertIn("(index - 1) / (double)total * 53", service)
        timing = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.Timing.cs").read_text(encoding="utf-8")
        self.assertIn('const double minimumScale = .30', timing)
        self.assertIn('cue.ActualSpeedFactor > 100', timing)
        self.assertIn('cue.FitMethod is not ("piper-length-scale" or "piper-atempo")', timing)
        self.assertIn("cue.TempoInputFrames != cue.SourceFrames - cue.TrimmedSilenceFrames", timing)
        self.assertIn("cue.SynthesisAttempts < 1", timing)
        self.assertIn("cue.ActualSpeedFactor < 1", timing)
        self.assertIn("relativeScale is < .70 or > 1", timing)
        installer = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs").read_text(encoding="utf-8")
        self.assertIn('TimingAlgorithm = "whole-cue-piper-natural-first-v17"', installer)


if __name__ == "__main__":
    unittest.main()

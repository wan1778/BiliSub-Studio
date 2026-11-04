#!/usr/bin/env python3
"""Offline VieNeu duration/byte-preservation definitions; not a speech or runtime PASS."""
import importlib.util
import inspect
from pathlib import Path
import tempfile
import unittest
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("vieneu_duration_worker", ROOT / "internal/vieneu/worker.py")
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

    def test_overflow_budget_only_uses_the_real_gap_to_the_next_cue(self):
        cues = [
            {"id": "a", "cue_start": 0, "cue_end": 1, "voice_start": 0, "voice_end": 1,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "a"},
            {"id": "b", "cue_start": 1.5, "cue_end": 2.5, "voice_start": 1.5, "voice_end": 2.5,
             "timing_source": "srt-fallback", "voice": worker.VOICE_NAME, "text": "b"},
        ]
        self.assertEqual(worker.overflow_budget_frames(cues, 0, cues[0], 3 * worker.SAMPLE_RATE), 11025)
        self.assertEqual(worker.overflow_budget_frames(cues, 1, cues[1], 3 * worker.SAMPLE_RATE), 11025)
        touching = [cues[0], cues[1] | {"cue_start": 1, "voice_start": 1}]
        self.assertEqual(worker.overflow_budget_frames(touching, 0, touching[0], 3 * worker.SAMPLE_RATE), 0)
        overlaps = worker.clip_overlap_pairs([
            {"id": "a", "start_sample": 0, "frames": 22050, "window_end_sample": 22050},
            {"id": "b", "start_sample": 22050, "frames": 22050, "window_end_sample": 44100},
        ])
        self.assertEqual(overlaps, [])
        bad = worker.clip_overlap_pairs([
            {"id": "a", "start_sample": 0, "frames": 33075, "window_end_sample": 22050},
            {"id": "b", "start_sample": 22050, "frames": 22050, "window_end_sample": 44100},
        ])
        self.assertEqual(len(bad), 1)
        self.assertTrue(bad[0]["unexpected"])

    def test_native_metadata_accepts_natural_first_and_accelerated_whole_speech(self):
        natural = {"fit_method": worker.FIT_METHOD, "raw_duration": 30000 / worker.SAMPLE_RATE,
                   "frames": 30300, "target_frames": 30300, "source_frames": 30000,
                   "generated_frames": 30000, "trimmed_silence_frames": 0, "padding_frames": 300,
                   "base_length_scale": 1, "length_scale": 1, "synthesis_attempts": 1,
                   "status": "fit", "native_reference_frames": 30000, "group_native_frames": 0,
                   "actual_speed_factor": 1.0, "timing_source": "srt-fallback"}
        self.assertTrue(worker.native_record_valid(natural, 30300, False))
        self.assertFalse(worker.native_record_valid(natural | {"length_scale": .99}, 30300, False))
        self.assertFalse(worker.native_record_valid(natural | {"native_reference_frames": 30001}, 30300, False))
        tempo = {"fit_method": worker.ATEMPO_METHOD, "raw_duration": 45000 / worker.SAMPLE_RATE,
                 "frames": 30000, "target_frames": 30000, "source_frames": 45000,
                 "tempo_input_frames": 45000, "generated_frames": 30000,
                 "trimmed_silence_frames": 0, "padding_frames": 0,
                 "base_length_scale": 1, "length_scale": 1, "tempo_factor": 1.5,
                 "tempo_attempts": 1, "synthesis_attempts": 1, "status": "review",
                 "native_reference_frames": 45000, "group_native_frames": 0,
                 "actual_speed_factor": 1.5, "timing_source": "srt-fallback"}
        self.assertTrue(worker.native_record_valid(tempo, 30000, False))
        for change in ({"tempo_input_frames": 44999}, {"tempo_factor": 1.4},
                       {"tempo_attempts": 0}, {"tempo_attempts": 7},
                       {"generated_frames": 45000, "padding_frames": -15000}):
            self.assertFalse(worker.native_record_valid(tempo | change, 30000, False))
        overflow = natural | {"frames": 40300, "target_frames": 30300,
                              "generated_frames": 40000, "source_frames": 40000,
                              "native_reference_frames": 40000, "overflow_frames": 10000,
                              "status": "fit"}
        self.assertTrue(worker.native_record_valid(overflow, 30300, False))
        self.assertFalse(worker.native_record_valid(overflow | {"overflow_frames": 9999}, 30300, False))
        silence = {"fit_method": "silence-fallback", "status": "review", "source_frames": 0,
                   "generated_frames": 0, "trimmed_silence_frames": 0, "padding_frames": 30300,
                   "frames": 30300, "target_frames": 30300, "synthesis_attempts": 1}
        self.assertTrue(worker.native_record_valid(silence, 30300, False))
        self.assertFalse(worker.native_record_valid(silence | {"timing_source": "sample"}, 30300, True))

    def test_padding_copies_every_pcm_byte_and_refuses_cutting(self):
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

    def test_manifest_requires_complete_srt_fallback_and_sample_identity(self):
        manifest = {"schema": 2, "engine_version": worker.ENGINE_VERSION, "voice": worker.VOICE_NAME,
                    "timing_algorithm": worker.TIMING_ALGORITHM, "duration": 6, "cues": [self.cue()]}
        self.assertEqual(worker.validate_manifest(manifest, worker.VOICE_NAME), manifest["cues"])
        fallback = self.cue() | {"voice_start": 1, "voice_end": 5, "timing_source": "srt-fallback"}
        self.assertEqual(worker.validate_manifest(manifest | {"cues": [fallback]}, worker.VOICE_NAME), [fallback])
        for change in ({"voice_end": 1.5}, {"timing_source": "unknown"}, {"timing_source": "srt-fallback"}):
            with self.assertRaises(ValueError):
                worker.validate_manifest(manifest | {"cues": [self.cue() | change]}, worker.VOICE_NAME)
        # F1 (17/11): the bounded early start. A srt-fallback cue may begin up to
        # MAX_LEAD_SECONDS before its subtitle (no room on the right, so the sentence
        # is not cut), and one frame beyond the budget is refused.
        lead = worker.MAX_LEAD_SECONDS
        early = fallback | {"voice_start": fallback["cue_start"] - lead, "voice_end": fallback["cue_end"]}
        self.assertEqual(worker.validate_manifest(manifest | {"cues": [early]}, worker.VOICE_NAME), [early])
        too_early = fallback | {"voice_start": fallback["cue_start"] - lead - 0.01,
                                "voice_end": fallback["cue_end"]}
        with self.assertRaises(ValueError):
            worker.validate_manifest(manifest | {"cues": [too_early]}, worker.VOICE_NAME)
        sample = self.cue() | {"id": "voice-demo-cue", "cue_start": 0, "cue_end": 4,
                               "voice_start": 0, "voice_end": 4, "timing_source": "sample"}
        self.assertEqual(worker.validate_manifest(
            manifest | {"duration": 4, "cues": [sample]}, worker.VOICE_NAME), [sample])
        with self.assertRaises(ValueError):
            worker.validate_manifest(manifest | {"cues": [sample | {"id": "other"}]}, worker.VOICE_NAME)

    def test_srt_fallback_is_explicit_and_must_use_the_complete_cue(self):
        cue = self.cue()
        fallback = worker.srt_fallback_cue(cue)
        self.assertEqual(fallback["voice_start"], cue["cue_start"])
        self.assertEqual(fallback["voice_end"], cue["cue_end"])
        self.assertEqual(fallback["timing_source"], "srt-fallback")
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
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"cue_start": 31, "voice_start": 31}], texts[:2]),
                         [[0], [1]])
        self.assertEqual(worker.sentence_groups([cues[0], cues[1] | {"timing_source": "sample"}], texts[:2]),
                         [[0], [1]])
        self.assertEqual(worker.sentence_groups(
            [cues[0], cues[1] | {"cue_start": .801, "voice_start": .801}], texts[:2]), [[0], [1]])

    def test_production_uses_overflow_gap_guards_and_vieneu_timing(self):
        source = inspect.getsource(worker)
        for marker in ("overflow_budget_frames", "clip_overlap_pairs", "max_overflow_frames",
                       "def build_master(", "clip_overlap_pairs(", "atempo=", "vieneu-atempo",
                       "tempo_fit_clip", "MAX_TEMPO_ATTEMPTS = 6", "MIN_RATE_SCALE = .30",
                       "MIN_PREFERRED_RATE_SCALE = .70", "MAX_RATE_SCALE = 1.20",
                       "MAX_GROUP_GAP_FRAMES = 5512", "MAX_GROUP_DURATION_FRAMES = 300 * SAMPLE_RATE",
                       "MAX_GROUP_SYNTHESIS_ATTEMPTS = 12", "MIN_GROUP_RATE_SCALE = MIN_RATE_SCALE"):
            self.assertIn(marker, source)
        for forbidden in ('"-t"', "asetrate=", "rubberband=", "atrim="):
            self.assertNotIn(forbidden, source)
        self.assertEqual(worker.TIMING_ALGORITHM, "whole-cue-vieneu-natural-first-v1")
        self.assertEqual(worker.ENGINE_VERSION, "vieneu-v3-turbo-1")
        self.assertEqual(worker.MIN_ACTUAL_SPEED, 1.0)
        self.assertEqual(worker.FIT_HEADROOM, .995)
        self.assertEqual(worker.MAX_ACTUAL_SPEED, 100.0)
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs").read_text(encoding="utf-8")
        self.assertIn("EditorVoiceCuePlanner.Build(request.Subtitle.Cues)", service)
        self.assertIn("EditorVoiceCuePlanner.RemoveUnspeakable(", service)
        self.assertIn("BuildWholeCue(cue, voice, cueTiming[cue.Id])", service)
        self.assertIn("cue.Frames != wantFrames", service)
        self.assertIn("cue.Clipped is not false", service)
        self.assertIn("ReadClipFrames(cue.ClipPath, expectedSampleRate) != cue.Frames", service)
        self.assertIn("await HashAsync(cue.ClipPath, cancellationToken) != cue.ClipSha256", service)
        self.assertIn("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate, index)", service)
        self.assertIn('cue.Start, cue.End, "srt-fallback"', service)
        self.assertIn("ClipEndSample", service)
        self.assertIn("nextStartSample", service)
        timing = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.Timing.cs").read_text(encoding="utf-8")
        self.assertIn('const double minimumScale = .30', timing)
        self.assertIn("VieneuMaxTempoFactor = 1.51", timing)
        self.assertIn("VieneuMicroMaxTempoFactor = 3.01", timing)
        self.assertIn('cue.FitMethod is not ("vieneu-natural" or "vieneu-atempo")', timing)
        self.assertIn("cue.TempoInputFrames != cue.SourceFrames - cue.TrimmedSilenceFrames", timing)
        self.assertIn("cue.SynthesisAttempts < 1", timing)
        self.assertIn("relativeScale is < .70 or > 1", timing)
        installer = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs").read_text(encoding="utf-8")
        self.assertIn('TimingAlgorithm = "whole-cue-vieneu-natural-first-v1"', installer)
        self.assertIn('ModelRevision = "61b85e3d937fbbacb387714180e8182823512523"', installer)


if __name__ == "__main__":
    unittest.main()

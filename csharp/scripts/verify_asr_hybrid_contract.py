#!/usr/bin/env python3
"""Offline word-seam/control-flow regression; never imports Whisper or runs inference."""
import importlib.util
import inspect
import random
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("asr_worker", ROOT / "internal/asr/worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def reading(words):
    return [{"start": words[0][1], "end": words[-1][2], "text": "full overlap text must be rebuilt",
             "words": [{"text": text.strip(), "raw": text, "start": start, "end": end, "probability": .95}
                       for text, start, end in words]}]


class HybridContract(unittest.TestCase):
    def test_jittered_anchor_is_kept_once(self):
        left = reading([("你", 59.3, 59.6), ("好", 59.8, 60.1), ("吗", 60.3, 60.6)])
        right = reading([("好", 59.85, 60.15), ("吗", 60.35, 60.65), ("？", 60.65, 60.8)])
        upper, lower, frontier = worker.hybrid_seam(left, right, 60, 0)
        a = worker.hybrid_project(left, 0, upper, 0, frontier)
        b = worker.hybrid_project(right, lower, 4, frontier, 120)
        self.assertEqual("".join(item["text"] for item in a + b), "你好吗？")
        self.assertGreaterEqual(b[0]["start"], a[-1]["end"])

    def test_repeated_word_at_different_times_is_not_erased(self):
        left = reading([("走", 59, 59.2), ("走", 59.9, 60.1)])
        right = reading([("走", 59.95, 60.15), ("走", 60.9, 61.1)])
        upper, lower, frontier = worker.hybrid_seam(left, right, 60, 0)
        a = worker.hybrid_project(left, 0, upper, 0, frontier)
        b = worker.hybrid_project(right, lower, 2, frontier, 120)
        self.assertEqual("".join(item["text"] for item in a + b), "走走走")

    def test_silence_and_empty_chunk_preserve_frontier(self):
        left, right = reading([("前", 58, 58.4)]), reading([("后", 61, 61.4)])
        self.assertEqual(worker.hybrid_seam(left, right, 60, 0), (1, 0, 60))
        self.assertEqual(worker.hybrid_seam([], right, 60, 0), (0, 0, 60))
        self.assertEqual(worker.hybrid_project([], 0, 0, 0, 60), [])

    def test_trimmed_words_rebuild_text_and_preserve_spaces(self):
        result = reading([(" bỏ", 10, 11), ("Hello", 11, 12), (" world", 12, 13)])
        projected = worker.hybrid_project(result, 1, 3, 11, 13)
        self.assertEqual(projected[0]["text"], "Hello world")
        self.assertEqual(len(projected[0]["words"]), 2)

    def test_out_of_window_word_is_pinned_not_dropped(self):
        projected = worker.hybrid_project(reading([("字", 59, 59.2)]), 0, 1, 60, 61)
        self.assertEqual(projected[0]["text"], "字")
        self.assertEqual(projected[0]["words"][0]["start"], 60)
        self.assertGreater(projected[0]["words"][0]["end"], 60)

    def test_adjacent_segment_jitter_merges_without_losing_words(self):
        result = reading([("前", 10, 11.2)]) + reading([("后", 11.1, 12)])
        projected = worker.hybrid_project(result, 0, 2, 10, 12)
        self.assertEqual(len(projected), 1)
        self.assertEqual(projected[0]["text"], "前后")
        self.assertEqual([(word["start"], word["end"]) for word in projected[0]["words"]],
                         [(10, 11.2), (11.1, 12)])

    def test_deep_adjacent_segment_conflict_is_sorted_and_kept(self):
        result = reading([("甲", 10, 11), ("前", 11, 11.2)]) + reading([("后", 10.2, 10.8)])
        projected = worker.hybrid_project(result, 0, 3, 10, 12)
        self.assertEqual(len(projected), 1)
        self.assertEqual(projected[0]["text"], "甲后前")
        self.assertEqual([word["raw"] for word in projected[0]["words"]], ["甲", "后", "前"])
        self.assertEqual(projected[0]["start"], 10)
        self.assertEqual(projected[0]["end"], 11.2)

    def test_random_overlap_fuzz_never_loses_words_or_emits_overlapping_cues(self):
        rng = random.Random(20260829)
        for case in range(2_000):
            result, expected = [], []
            for segment_index in range(rng.randint(1, 10)):
                words = []
                for word_index in range(rng.randint(1, 8)):
                    identity = f"{case}:{segment_index}:{word_index}"
                    start = rng.uniform(-5, 35)
                    end = start + rng.uniform(.00001, 8)
                    words.append((identity, start, end))
                    expected.append(identity)
                result += reading(words)
            projected = worker.hybrid_project(result, 0, len(expected), 0, 30)
            actual = [word["raw"] for cue in projected for word in cue["words"]]
            self.assertCountEqual(actual, expected)
            self.assertFalse(any(right["start"] < left["end"] for left, right in zip(projected, projected[1:])))
            for cue in projected:
                self.assertGreater(cue["end"], cue["start"])
                self.assertTrue(all(cue["start"] <= word["start"] < word["end"] <= cue["end"] for word in cue["words"]))
                self.assertFalse(any(right["start"] < left["start"] for left, right in zip(cue["words"], cue["words"][1:])))

    def test_bounded_dynamic_scheduler_and_model_reuse(self):
        source = inspect.getsource(worker.run_hybrid)
        self.assertEqual(source.count("WhisperModel("), 2)
        self.assertLess(source.rindex("WhisperModel("), source.index("def process_chunk"))
        for marker in ("max_workers=2", "next_index < emit_index + 4", "idle.append(device)",
                       "return_when=FIRST_COMPLETED", '"chunk_complete"', '"cpu_chunks"', '"gpu_chunks"'):
            self.assertIn(marker, source)

    def test_checkpoint_commit_and_ui_routing(self):
        hybrid = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.Hybrid.cs").read_text(encoding="utf-8")
        service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs").read_text(encoding="utf-8")
        page = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
        segment_case = hybrid.split('case "segment":', 1)[1].split('case "chunk_complete":', 1)[0]
        self.assertNotIn("SaveCheckpointAsync", segment_case)
        for marker in ("expectedChunk", "staged.Clear()", "checkpoint.Frontier", "--core-start", "CancellationToken.None"):
            self.assertIn(marker, hybrid)
        self.assertIn('".hybrid-v1.json"', service)
        self.assertIn('if (executionMode == "cpu")', service)
        self.assertIn('selection.Device is "cuda" or "hybrid"', service)
        self.assertIn("new EditorAsrRequest(_project.Id, _path, _media.Duration, executionMode)", page)
        self.assertIn("AsrExecutionModeBox.IsEnabled = idle", page)
        self.assertIn("EnsureVoiceTimingAsync(force: true)", page)
        self.assertIn("continueVoice: !force", page)
        self.assertIn("Guid.NewGuid().ToString(\"N\") + \".speech.json\"", service)
        main_window = (ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertLess(main_window.index("await _application.InitializeAsync()"),
                        main_window.index('((EditorPage)_pages["editor"]).ApplyConfiguration()'))


if __name__ == "__main__":
    unittest.main()

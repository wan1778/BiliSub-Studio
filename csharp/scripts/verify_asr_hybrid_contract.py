#!/usr/bin/env python3
"""Offline word-seam/control-flow regression; never imports Whisper or runs inference."""
import importlib.util
import inspect
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

    def test_conflicting_word_is_not_silently_dropped(self):
        with self.assertRaises(RuntimeError):
            worker.hybrid_project(reading([("字", 59, 59.2)]), 0, 1, 60, 61)

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

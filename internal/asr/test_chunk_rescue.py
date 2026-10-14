"""Chunk-rescue regression for the 2026-10-14 ASR IndexError.

One bad 60s chunk inside faster-whisper must never kill an hours-long job:
rescue ladder is full-params -> no-VAD retry -> skip span with a warning.
Empty results must flow through the seam math as a silence gap.
"""
import contextlib
import io
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from worker import hybrid_project, hybrid_seam, transcribe_chunk_resilient


class FakeWord:
    def __init__(self, word, start, end, probability=0.9):
        self.word = word
        self.start = start
        self.end = end
        self.probability = probability


class FakeSegment:
    def __init__(self, text, start, end, words):
        self.text = text
        self.start = start
        self.end = end
        self.words = words
        self.avg_logprob = -0.2
        self.no_speech_prob = 0.01


class FakeInfo:
    language = "zh"
    language_probability = 1.0


def canned_segments():
    words = [FakeWord("你好", 0.0, 0.5), FakeWord("世界", 0.5, 1.0)]
    return [FakeSegment("你好世界", 0.0, 1.0, words)]


class FlakyModel:
    """Fails exactly like faster-whisper did on 2026-10-14 when VAD is on."""

    def __init__(self, always_fail=False):
        self.always_fail = always_fail
        self.calls = []

    def transcribe(self, audio, **kwargs):
        self.calls.append(kwargs.get("vad_filter"))
        if self.always_fail or kwargs.get("vad_filter"):
            raise IndexError(
                "boolean index did not match indexed array along axis 0; "
                "size of axis is 0 but size of corresponding boolean axis is 1")
        return iter(canned_segments()), FakeInfo()


def run_resilient(model):
    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        result, info = transcribe_chunk_resilient(model, None, 5, "test#0@0.0s", 100.0)
    return result, info, err.getvalue()


def test_retry_without_vad():
    result, info, err = run_resilient(FlakyModel())
    assert len(result) == 1, f"expected 1 rescued segment, got {len(result)}"
    assert len(result[0]["words"]) == 2, "rescued segment lost its words"
    assert abs(result[0]["start"] - 100.0) < 1e-9, "base offset not applied"
    assert isinstance(info, FakeInfo), "info must survive the retry"
    assert "ASR_CHUNK_RETRY test#0@0.0s: IndexError" in err, "retry not announced"
    assert "ASR_CHUNK_RETRY_OK test#0@0.0s" in err, "retry success not announced"
    print("PASS retry-without-vad")


def test_skip_when_both_paths_fail():
    result, info, err = run_resilient(FlakyModel(always_fail=True))
    assert result == [] and info is None, "total failure must yield an empty gap, not raise"
    assert "ASR_CHUNK_SKIP test#0@0.0s" in err, "skip not announced"
    print("PASS skip-when-both-paths-fail")


def test_wordless_segments_dropped_not_fatal():
    class WordlessModel:
        def transcribe(self, audio, **kwargs):
            return iter([FakeSegment("uh", 0.0, 0.5, [])]), FakeInfo()

    err = io.StringIO()
    with contextlib.redirect_stderr(err):
        result, info = transcribe_chunk_resilient(WordlessModel(), None, 5, "test#1@0.0s", 0.0)
    err = err.getvalue()
    assert result == [], "wordless segments must be dropped"
    assert isinstance(info, FakeInfo), "info must survive wordless filtering"
    assert "ASR_CHUNK_WORDLESS test#1@0.0s" in err, "wordless drop not announced"
    print("PASS wordless-segments-dropped-not-fatal")


def test_empty_chunk_flows_through_seam():
    words = [{"start": 61.0, "end": 61.4, "text": "好", "raw": "好", "probability": 0.9}]
    upper, next_lower, end = hybrid_seam([], [{"words": words}], 60.0, 0)
    assert upper == 0 and end == 60.0, f"empty current chunk broke the seam: {(upper, next_lower, end)}"
    owned = hybrid_project([], 0, upper, 59.0, end)
    assert owned == [] and end > 59.0, "frontier must advance past the skipped span"
    print("PASS empty-chunk-flows-through-seam")


if __name__ == "__main__":
    test_retry_without_vad()
    test_skip_when_both_paths_fail()
    test_wordless_segments_dropped_not_fatal()
    test_empty_chunk_flows_through_seam()
    print("test_chunk_rescue: 4/4 PASS")

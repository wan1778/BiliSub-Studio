from __future__ import annotations

import importlib.util
import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKER = ROOT / "internal" / "tts" / "worker.py"
TIMING_CS = (ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Editor"
             / "LocalTtsService.Timing.cs")
SERVICE_CS = (ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Editor"
              / "LocalTtsService.cs")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL TTS group policy contract: " + message)


def load_worker():
    spec = importlib.util.spec_from_file_location("tts_worker_under_test", WORKER)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def cue(start: float, end: float, timing_source: str = "srt-fallback") -> dict:
    return {
        "id": f"cue-{start}-{end}",
        "cue_start": start,
        "cue_end": end,
        "voice_start": start,
        "voice_end": end,
        "timing_source": timing_source,
    }


def record(scale: float, base: float = 1.0, fit_method: str = "piper-length-scale") -> dict:
    return {"base_length_scale": base, "length_scale": scale, "fit_method": fit_method}


def main() -> int:
    worker = load_worker()

    # Field case cue 40: 9 syllables squeezed into 0.834s needs ~2.4x speed
    # (relative scale ~0.42) and must yield to sentence-group synthesis.
    require(worker.single_needs_sentence_group(record(0.42)),
            "fast single (0.42x) did not trigger sentence-group override")
    require(worker.single_needs_sentence_group(record(0.30)),
            "floor-rate single (0.30x) did not trigger sentence-group override")
    require(worker.single_needs_sentence_group(record(1.0, fit_method="piper-atempo")),
            "atempo-fallback single did not trigger sentence-group override")
    require(not worker.single_needs_sentence_group(record(1.0)),
            "natural single (1.0x) must keep its individual fit")
    require(not worker.single_needs_sentence_group(record(1.1)),
            "slow single (1.1x) must keep its individual fit")
    require(not worker.single_needs_sentence_group(record(0.70)),
            "preferred-band edge (0.70x) must keep its individual fit")
    require(worker.single_needs_sentence_group(record(0.699)),
            "just below preferred band (0.699x) must trigger sentence-group override")
    require(not worker.single_needs_sentence_group({}),
            "missing rate metadata must fail open to old behavior")
    require(not worker.single_needs_sentence_group({"base_length_scale": 0, "length_scale": 0.5}),
            "degenerate base scale must fail open to old behavior")

    # Grouping structure pins (unchanged behavior): touching cues share a group
    # so the override has somewhere to go; gapped cues stay split.
    pair = [cue(12.333, 13.167), cue(13.167, 14.967)]
    groups = worker.sentence_groups(pair, ["a", "b"])
    require(groups == [[0, 1]], "touching cues must form one sentence group")
    split = [cue(12.333, 13.167), cue(13.5, 14.967)]
    require(worker.sentence_groups(split, ["a", "b"]) == [[0], [1]],
            "gapped cues must not group")
    sampled = [cue(12.333, 13.167, "sample"), cue(13.167, 14.967, "srt-fallback")]
    require(worker.sentence_groups(sampled, ["a", "b"]) == [[0], [1]],
            "sample-timed cues must not group")

    # Driver wiring pins: the override decision point must exist on the group
    # path (not only inside the individual-failure handler).
    driver = WORKER.read_text(encoding="utf-8")
    require("single_needs_sentence_group(entry[3])" in driver,
            "group driver does not consult the override on individual records")
    require("MIN_PREFERRED_RATE_SCALE" in driver,
            "override does not reuse the preferred-rate band constant")

    # Gap widening 0 -> 0.25s (5512 frames @22050Hz), mirrored exactly in C#.
    require(worker.MAX_GROUP_GAP_FRAMES == 5512,
            "worker group gap allowance is not 5512 frames")
    timing_cs = TIMING_CS.read_text(encoding="utf-8")
    require("MaxSentenceGroupGapFrames = 5512" in timing_cs,
            "C# validator gap constant does not mirror the worker")
    rapid = [cue(10.0, 11.0), cue(11.24, 12.0)]
    require(worker.sentence_groups(rapid, ["a", "b"]) == [[0, 1]],
            "0.24s breath gap must join one passage group")
    breather = [cue(10.0, 11.0), cue(11.26, 12.0)]
    require(worker.sentence_groups(breather, ["a", "b"]) == [[0], [1]],
            "0.26s real pause must stay split")

    # Watch-list classifiers (reporting only, never block synthesis).
    require(worker.PATHOLOGICAL_NEED_RATIO == 3.0, "pathological threshold moved")
    require(worker.FAST_SINGLE_NEED_RATIO == 1.43, "isolated-fast threshold moved")
    require(worker.GROUP_DRIFT_WARN_SECONDS == 0.5, "drift warn threshold moved")
    require(worker.is_exclamation_exempt("Đại Tông Sư!"),
            "short shout must be exclamation-exempt")
    require(worker.is_exclamation_exempt("Ký hiệu gì vậy?"),
            "short question must be exclamation-exempt")
    require(not worker.is_exclamation_exempt("Mở cửa."),
            "period-terminated line must not be exclamation-exempt")
    require(not worker.is_exclamation_exempt("Bệ hạ đã phế ngôi Thái tử của ta,"),
            "long fragment must not be exclamation-exempt")
    require(not worker.is_exclamation_exempt(""), "empty text must not be exempt")
    require(worker.need_ratio(44100, 22050) == 2.0, "need ratio miscomputed")
    require(worker.need_ratio(100, 0) == 0.0, "zero window must fail open")
    require(worker.need_ratio("x", 10) == 0.0, "bad input must fail open")
    require("report_group_rate(group_indices, group_entries, cues, texts, rate_tally)" in driver,
            "group rate report not wired into driver loop")
    require('"boundary-drift"' in driver and '"voice-rate-summary"' in driver,
            "notice event codes missing from driver")
    require("rate_tally" in driver, "run tally missing from driver")
    service = SERVICE_CS.read_text(encoding="utf-8")
    require('kind == "notice"' in service,
            "C# does not handle worker notice events (warnings would vanish)")

    print("PASS TTS group policy: fast singles yield to sentence-group, naturals keep individual fit, "
          "grouping structure pinned")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

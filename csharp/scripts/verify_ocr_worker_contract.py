from __future__ import annotations

import importlib.util
import pathlib
import sys
import tempfile
import types


ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKER = ROOT / "internal" / "ocr" / "worker.py"


def install_dependency_stubs() -> None:
    cv2 = types.ModuleType("cv2")
    sys.modules["cv2"] = cv2

    numpy = types.ModuleType("numpy")
    numpy.integer = int
    numpy.floating = float
    numpy.uint8 = int
    sys.modules["numpy"] = numpy

    paddle = types.ModuleType("paddle")
    paddle.__version__ = "fixture"
    paddle.is_compiled_with_cuda = lambda: False
    paddle.device = types.SimpleNamespace(cuda=types.SimpleNamespace(device_count=lambda: 0))
    paddle.set_device = lambda _device: None
    sys.modules["paddle"] = paddle

    paddleocr = types.ModuleType("paddleocr")
    paddleocr.PaddleOCR = object
    sys.modules["paddleocr"] = paddleocr


def load_worker():
    install_dependency_stubs()
    with tempfile.TemporaryDirectory() as cache:
        old_argv = sys.argv[:]
        try:
            sys.argv = [str(WORKER), "--model-cache", cache, "--device", "cpu"]
            spec = importlib.util.spec_from_file_location("bilisub_ocr_worker_contract", WORKER)
            if spec is None or spec.loader is None:
                raise RuntimeError("cannot create worker module spec")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            return module
        finally:
            sys.argv = old_argv


def assert_result(result: dict, text: str, confidence: float, box: list[int]) -> None:
    assert result["ok"] is True
    assert result["detected"] is True
    assert result["text"] == text
    assert abs(result["confidence"] - confidence) < 1e-9
    assert result["lines"] == [{"text": text, "confidence": confidence, "box": box}]


def assert_rejected(worker, prediction, expected: str) -> None:
    try:
        worker.parse_prediction(prediction)
    except RuntimeError as exc:
        assert expected in str(exc), str(exc)
        return
    raise AssertionError(f"invalid PaddleOCR result was accepted: {prediction!r}")


def verify_inline_row_compatibility(worker) -> None:
    separate = [
        ("V04", ["老头也是眉开眼笑", "明"], [0.99997, 0.99999],
         [[449, 212, 830, 267], [857, 197, 930, 283]]),
        ("V06", ["是否取消本次任务", "雪"], [0.99973, 0.7075],
         [[464, 186, 814, 245], [347, 196, 432, 281]]),
        ("height only", ["Caption", "7"], [0.99, 0.98],
         [[100, 100, 300, 160], [310, 87, 370, 173]]),
        ("center offset", ["Caption", "7"], [0.99, 0.98],
         [[100, 100, 300, 160], [310, 72, 370, 150]]),
        ("baseline offset", ["Caption", "7"], [0.99, 0.98],
         [[100, 100, 300, 160], [310, 104, 370, 182]]),
    ]
    for name, texts, scores, boxes in separate:
        for scale, dx, dy in [(1, 0, 0), (2, 73, 41)]:
            transformed = [
                [x1 * scale + dx, y1 * scale + dy, x2 * scale + dx, y2 * scale + dy]
                for x1, y1, x2, y2 in boxes
            ]
            expected = [
                {"text": text, "confidence": score, "box": box}
                for text, score, box in zip(texts, scores, transformed)
            ]
            expected.sort(key=lambda line: line["box"][1])
            for order in [(0, 1), (1, 0)]:
                result = worker.parse_prediction([{
                    "rec_texts": [texts[i] for i in order],
                    "rec_scores": [scores[i] for i in order],
                    "rec_boxes": [transformed[i] for i in order],
                }])
                assert result["ok"] is True and result["detected"] is True, name
                assert result["lines"] == expected, (name, result)
                assert result["text"] == "\n".join(line["text"] for line in expected), name
                assert abs(result["confidence"] - sum(scores) / len(scores)) < 1e-9, name

    legitimate = [
        (["是否取消", "本次任务"], [[100, 100, 300, 160], [310, 103, 510, 161]], "是否取消本次任务"),
        (["第", "12", "集"], [[100, 100, 150, 160], [158, 110, 215, 158], [223, 102, 273, 161]], "第12集"),
        (["字幕", "。"], [[100, 100, 220, 160], [228, 148, 240, 160]], "字幕。"),
        (["字幕", "……"], [[100, 100, 220, 160], [228, 125, 252, 135]], "字幕……"),
        (["“", "字幕"], [[100, 100, 112, 112], [120, 100, 240, 160]], "“字幕"),
    ]
    for texts, boxes, expected_text in legitimate:
        for order in [list(range(len(texts))), list(reversed(range(len(texts))))]:
            result = worker.parse_prediction([{
                "rec_texts": [texts[i] for i in order],
                "rec_scores": [0.99] * len(texts),
                "rec_boxes": [boxes[i] for i in order],
            }])
            assert_result(result, expected_text, 0.99, [
                min(box[0] for box in boxes), min(box[1] for box in boxes),
                max(box[2] for box in boxes), max(box[3] for box in boxes),
            ])

    # A small punctuation box can overlap both otherwise incompatible rows.
    bridged = worker.parse_prediction([{
        "rec_texts": ["Caption", ".", "7"],
        "rec_scores": [0.99, 0.99, 0.98],
        "rec_boxes": [[100, 100, 300, 160], [305, 135, 310, 145], [315, 120, 375, 180]],
    }])
    assert bridged["lines"] == [
        {"text": "Caption.", "confidence": 0.99, "box": [100, 100, 310, 160]},
        {"text": "7", "confidence": 0.98, "box": [315, 120, 375, 180]},
    ], bridged
    print("PASS OCR inline row compatibility: 20 separation, 10 legitimate merge, 1 punctuation bridge cases")


def main() -> int:
    worker = load_worker()
    source = WORKER.read_text(encoding="utf-8")
    assert "def visual_change_scores(images):" in source
    assert 'request.get("probe_images_base64")' in source
    assert "len(encoded_probe) > 65" in source
    assert '"change_scores": visual_change_scores(probe_images)' in source

    flat = {
        "rec_texts": ["你好"],
        "rec_scores": [0.91],
        "rec_boxes": [[1, 2, 30, 40]],
    }
    assert_result(worker.parse_prediction([flat]), "你好", 0.91, [1, 2, 30, 40])

    wrapped = {"res": {
        "rec_texts": ["世界"],
        "rec_scores": [0.87],
        "rec_boxes": [[5, 6, 50, 60]],
    }}
    assert_result(worker.parse_prediction([wrapped]), "世界", 0.87, [5, 6, 50, 60])

    class ResultFixture:
        @property
        def json(self):
            return {"res": {
                "rec_texts": ["测试"],
                "rec_scores": [0.95],
                "rec_polys": [[[10, 20], [40, 20], [40, 55], [10, 55]]],
            }}

    assert_result(worker.parse_prediction([ResultFixture()]), "测试", 0.95, [10, 20, 40, 55])

    overlapping = {
        "rec_texts": ["少主……是", "是不死丹帝，药逆命"],
        "rec_scores": [0.85924, 0.99997],
        "rec_boxes": [[313, 146, 531, 202], [501, 147, 953, 201]],
    }
    assert_result(
        worker.parse_prediction([overlapping]),
        "少主……是不死丹帝，药逆命",
        0.85924,
        [313, 146, 953, 202],
    )
    y_jittered = overlapping | {
        "rec_boxes": [[313, 147, 531, 202], [501, 145, 953, 201]],
    }
    assert_result(
        worker.parse_prediction([y_jittered]),
        "少主……是不死丹帝，药逆命",
        0.85924,
        [313, 145, 953, 202],
    )
    separate_rows = {
        "rec_texts": ["第一行", "第二行"],
        "rec_scores": [0.9, 0.8],
        "rec_boxes": [[10, 10, 100, 40], [10, 70, 100, 100]],
    }
    separated = worker.parse_prediction([separate_rows])
    assert separated["text"] == "第一行\n第二行" and len(separated["lines"]) == 2
    verify_inline_row_compatibility(worker)

    empty = worker.parse_prediction([{"res": {"rec_texts": [], "rec_scores": [], "rec_boxes": []}}])
    assert empty["ok"] is True and empty["detected"] is False and empty["text"] == ""

    assert_rejected(worker, [{"error": "backend failed"}], "backend failed")
    assert_rejected(worker, [{"rec_texts": [], "rec_scores": []}], "rec_boxes/rec_polys")
    assert_rejected(worker, [{
        "rec_texts": ["第一行", "第二行"],
        "rec_scores": [0.9],
        "rec_boxes": [[1, 2, 3, 4]],
    }], "2/1/1")
    assert_rejected(worker, [], "không trả kết quả")

    class TightRetry:
        def __init__(self, prediction):
            self.prediction, self.calls = prediction, 0

        def predict(self, image, **kwargs):
            self.calls += 1
            assert kwargs["text_det_unclip_ratio"] == 0.8
            if isinstance(self.prediction, Exception):
                raise self.prediction
            return [self.prediction]

    def reading(text, score, box=None):
        return {"rec_texts": [text], "rec_scores": [score], "rec_boxes": [box or [600, 130, 680, 220]]}

    wrong = worker.parse_prediction([reading("徒", .63)])
    engine = TightRetry(reading("走", .99, [610, 140, 670, 210]))
    assert worker.refine_short_text(engine, None, wrong)["text"] == "走"
    assert engine.calls == 1
    assert worker.refine_short_text(TightRetry(reading("啊", .99)), None, wrong)["text"] == "啊"
    for rejected in [reading("走", .85), reading("走吧", .99), reading("走", .99, [10, 10, 20, 20]), RuntimeError("retry failed")]:
        assert worker.refine_short_text(TightRetry(rejected), None, wrong) is wrong
    for original in [worker.parse_prediction([reading("走", .99)]), worker.parse_prediction([reading("你好", .60)]), empty]:
        engine = TightRetry(reading("走", .99))
        assert worker.refine_short_text(engine, None, original) is original and engine.calls == 0
    assert worker.refine_short_text(TightRetry(reading("走", .99)), None, empty, recover_blank=True)["text"] == "走"
    assert worker.refine_short_text(TightRetry(reading("走", .85)), None, empty, True, "走")["text"] == "走"
    assert worker.refine_short_text(TightRetry(reading("徒", .85)), None, empty, True, "走") is empty
    assert worker.refine_short_text(TightRetry({"rec_texts": [], "rec_scores": [], "rec_boxes": []}), None, empty, True, "走") is empty

    print("PASS OCR worker PaddleOCR 3 result and failure contract")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

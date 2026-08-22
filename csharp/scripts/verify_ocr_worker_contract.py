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


def main() -> int:
    worker = load_worker()

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

    empty = worker.parse_prediction([{"res": {"rec_texts": [], "rec_scores": [], "rec_boxes": []}}])
    assert empty["ok"] is True and empty["detected"] is False and empty["text"] == ""

    print("PASS OCR worker PaddleOCR 3 result contract")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

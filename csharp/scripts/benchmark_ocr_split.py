"""Opt-in real installed-runtime comparison; never run by the stdlib gate.

Run baseline and split in separate processes with identical --models and --device.
Reports include parsed outputs so differences cannot hide behind timing averages.
"""
import argparse
import json
import os
import pathlib
import time
import types
import sys

from verify_ocr_pixel_cache_contract import load_definitions


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--compare":
        baseline, split = [json.loads(pathlib.Path(path).read_text(encoding="utf-8")) for path in sys.argv[2:]]
        assert baseline["frames"] == split["frames"]
        differences = [i for i, (left, right) in enumerate(zip(baseline["outputs"], split["outputs"])) if left != right]
        print(json.dumps(dict(exact_output_matches=baseline["frames"] - len(differences),
            differences=differences, baseline_ms=baseline["total_ms"], split_ms=split["total_ms"],
            speedup=baseline["total_ms"] / split["total_ms"])))
        assert not differences, "parsed OCR output changed"
        return
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("baseline", "split"), required=True)
    parser.add_argument("--models", required=True)
    parser.add_argument("--device", default="gpu:0")
    parser.add_argument("--output", required=True)
    parser.add_argument("--background", choices=("pixel", "patch"), default="pixel")
    args = parser.parse_args()
    os.environ["PADDLE_PDX_CACHE_HOME"] = str(pathlib.Path(args.models).resolve())
    os.environ["PADDLE_PDX_MODEL_SOURCE"] = "BOS"
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    import numpy as np
    import paddle
    import paddleocr
    import paddlex
    from PIL import Image, ImageDraw, ImageFont

    worker = load_definitions()
    worker.update(np=np, ARGS=types.SimpleNamespace(device=args.device),
        DETECTION_MODEL="PP-OCRv6_medium_det", RECOGNITION_MODEL="PP-OCRv6_medium_rec")
    font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 36)
    captions = [(["\u4f60\u597d\u4e16\u754c"], 120)] * 4 + [
        (["\u4f60\u597d\u4e2d\u56fd"], 120), (["\u4f60\u597d\u4e16\u754c"], 220),
        (["\u7b2c\u4e00\u884c\u5b57\u5e55", "\u7b2c\u4e8c\u884c"], 120),
        (["\u7b2c\u4e00\u884c\u5b57\u5e55", "\u7b2c\u4e09\u884c"], 120),
        ([], 120), (["\u8d70"], 120),
        (["\u7b2c\u4e8c\u884c", "\u7b2c\u4e00\u884c\u5b57\u5e55"], 120),
        (["\u4f60\u597d\u4e16\u754c"], 120)]
    images = []
    for repeat in range(3):
        for index, (lines, x) in enumerate(captions):
            image = Image.new("RGB", (640, 240), (24, 24, 24))
            draw = ImageDraw.Draw(image)
            for row, text in enumerate(lines):
                draw.text((x, 65 + 65 * row), text, font=font, fill="white")
            # Every decoded frame differs, but pixels around the caption stay fixed.
            if args.background == "patch":
                draw.rectangle((0, 0, 20, 20), fill=(30 + repeat * 12 + index, 24, 24))
            else:
                draw.point((639, 0), fill=(30 + repeat * 12 + index, 24, 24))
            images.append(np.asarray(image)[:, :, ::-1].copy())

    started = time.perf_counter()
    if args.mode == "baseline":
        engine = paddleocr.PaddleOCR(text_detection_model_name="PP-OCRv6_medium_det",
            text_recognition_model_name="PP-OCRv6_medium_rec", device=args.device,
            use_doc_orientation_classify=False, use_doc_unwarping=False, use_textline_orientation=False)
    else:
        engine = worker["SplitOCREngine"]()
    startup = time.perf_counter() - started
    engine.predict(images[0], text_det_box_thresh=.55, text_rec_score_thresh=.45)

    class Count:
        def __init__(self, model):
            self.model, self.calls, self.images = model, 0, 0

        def predict(self, images, **kwargs):
            self.calls += 1
            self.images += len(images) if isinstance(images, list) else 1
            return self.model.predict(images, **kwargs)

    if args.mode == "split":
        engine.entries.clear()
        engine.byte_count = 0
        engine.detector, engine.recognizer = Count(engine.detector), Count(engine.recognizer)
    cache = worker["BasePredictionCache"](engine)
    outputs, milliseconds = [], []
    for image in images:
        started = time.perf_counter()
        base = cache.predict([image])[0]
        outputs.append(worker["refine_short_text"](engine, image, base, True, "\u8d70"))
        milliseconds.append((time.perf_counter() - started) * 1000)
    report = dict(mode=args.mode, background=args.background, device=args.device, paddle=paddle.__version__,
        paddleocr=paddleocr.__version__, paddlex=paddlex.__version__, frames=len(images),
        startup_seconds=startup, total_ms=sum(milliseconds), per_frame_ms=milliseconds,
        outputs=outputs)
    if args.mode == "split":
        report.update(detection_calls=engine.detector.calls, recognition_calls=engine.recognizer.calls,
            recognized_crops=engine.recognizer.images, recognition_cache_entries=len(engine.entries),
            recognition_cache_bytes=engine.byte_count)
    pathlib.Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key not in ("outputs", "per_frame_ms")}))


if __name__ == "__main__":
    main()

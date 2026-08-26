import argparse
import base64
import json
import os
import sys
import traceback


def parse_args():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--model-cache", required=True)
    parser.add_argument("--device", required=True, choices=("cpu", "gpu:0"))
    return parser.parse_args()


ARGS = parse_args()
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.abspath(ARGS.model_cache)
os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "BOS")
if ARGS.device == "cpu":
    os.environ.setdefault("FLAGS_use_mkldnn", "1")

import cv2
import numpy as np
import paddle
from paddleocr import PaddleOCR


ENGINE_NAME = "PaddleOCR"
DETECTION_MODEL = "PP-OCRv6_small_det"
RECOGNITION_MODEL = "PP-OCRv6_small_rec"


def emit(payload):
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def as_list(value):
    if value is None:
        return []
    if hasattr(value, "tolist"):
        return value.tolist()
    if isinstance(value, (list, tuple)):
        return list(value)
    return list(value)


def as_mapping(item):
    """Normalize PaddleOCR 3 Result/json representations to the inner OCR result dict."""
    payload = item if isinstance(item, dict) else getattr(item, "json", None)
    if callable(payload):
        payload = payload()
    if isinstance(payload, str):
        payload = json.loads(payload)
    if not isinstance(payload, dict):
        try:
            payload = dict(item)
        except (TypeError, ValueError):
            payload = {}
    # PaddleOCR 3 pipeline Result.json/print output is {"res": {...}}.
    # Keep compatibility with a flat dict representation as well.
    inner = payload.get("res") if isinstance(payload, dict) else None
    return inner if isinstance(inner, dict) else payload


def decode_image(encoded):
    raw = base64.b64decode(encoded, validate=True)
    arr = np.frombuffer(raw, dtype=np.uint8)
    image = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if image is None or image.size == 0:
        raise ValueError("không giải mã được ảnh OCR")
    return image


def normalize_box(box):
    if hasattr(box, "tolist"):
        box = box.tolist()
    if not isinstance(box, list):
        try:
            box = list(box)
        except TypeError:
            return []
    # rec_boxes is normally [x_min, y_min, x_max, y_max]. If a polygon is
    # returned instead, collapse it to the same rectangular contract expected by C#.
    if len(box) >= 4 and all(isinstance(value, (int, float, np.integer, np.floating)) for value in box[:4]):
        return [int(round(float(value))) for value in box[:4]]
    points = []
    for point in box:
        if isinstance(point, (list, tuple)) and len(point) >= 2:
            points.append((float(point[0]), float(point[1])))
    if not points:
        return []
    xs = [point[0] for point in points]
    ys = [point[1] for point in points]
    return [int(round(min(xs))), int(round(min(ys))), int(round(max(xs))), int(round(max(ys)))]


def parse_prediction(prediction):
    lines = []
    for item in prediction:
        data = as_mapping(item)
        texts = as_list(data.get("rec_texts"))
        scores = as_list(data.get("rec_scores"))
        boxes = as_list(data.get("rec_boxes"))
        if not boxes:
            boxes = as_list(data.get("rec_polys"))
        for index, text in enumerate(texts):
            text = str(text or "").strip()
            if not text:
                continue
            confidence = float(scores[index]) if index < len(scores) else 0.0
            box = normalize_box(boxes[index]) if index < len(boxes) else []
            lines.append({"text": text, "confidence": confidence, "box": box})
    text = "\n".join(line["text"] for line in lines)
    confidence = sum(line["confidence"] for line in lines) / len(lines) if lines else 0.0
    return {
        "ok": True,
        "detected": bool(lines),
        "text": text,
        "confidence": confidence,
        "lines": lines,
    }


def validate_device():
    if ARGS.device == "cpu":
        return False
    if not paddle.is_compiled_with_cuda():
        raise RuntimeError("PaddlePaddle GPU runtime không được biên dịch với CUDA")
    count = int(paddle.device.cuda.device_count())
    if count < 1:
        raise RuntimeError("PaddlePaddle không thấy NVIDIA GPU khả dụng")
    paddle.set_device(ARGS.device)
    return True


def main():
    cuda_available = validate_device()
    engine = PaddleOCR(
        text_detection_model_name=DETECTION_MODEL,
        text_recognition_model_name=RECOGNITION_MODEL,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        device=ARGS.device,
    )
    emit({
        "type": "ready",
        "engine": ENGINE_NAME,
        "paddleocr": "3.7.0",
        "paddle": str(paddle.__version__),
        "models": [DETECTION_MODEL, RECOGNITION_MODEL],
        "device": ARGS.device,
        "cuda_available": cuda_available,
    })
    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue
        request_id = None
        try:
            request = json.loads(raw_line)
            request_id = request.get("id")
            encoded_batch = request.get("images_base64")
            if encoded_batch is not None:
                if not isinstance(encoded_batch, list) or not encoded_batch or len(encoded_batch) > 4:
                    raise ValueError("images_base64 phải là batch 1-4 ảnh")
                images = [decode_image(str(encoded or "").strip()) for encoded in encoded_batch]
                # Subtitle glyphs are often thin, outlined and partially covered by
                # motion. Keep borderline detections here; the C# tracker requires
                # consecutive frames before they can enter the SRT.
                predictions = list(engine.predict(images, text_det_box_thresh=0.55, text_rec_score_thresh=0.45))
                if len(predictions) != len(images):
                    raise RuntimeError(f"PaddleOCR batch trả {len(predictions)}/{len(images)} kết quả")
                emit({
                    "id": request_id,
                    "ok": True,
                    "results": [parse_prediction([prediction]) for prediction in predictions],
                })
                continue
            encoded = str(request.get("image_base64") or "").strip()
            if not encoded:
                raise ValueError("image_base64 rỗng")
            image = decode_image(encoded)
            result = parse_prediction(engine.predict(image, text_det_box_thresh=0.55, text_rec_score_thresh=0.45))
            result["id"] = request_id
            emit(result)
        except Exception as exc:
            emit({
                "id": request_id,
                "ok": False,
                "detected": False,
                "text": "",
                "confidence": 0.0,
                "lines": [],
                "error": str(exc),
            })


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        emit({"type": "fatal", "error": str(exc), "trace": traceback.format_exc(limit=8)})
        raise

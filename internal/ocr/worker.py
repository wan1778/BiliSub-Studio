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
DETECTION_MODEL = "PP-OCRv6_medium_det"
RECOGNITION_MODEL = "PP-OCRv6_medium_rec"


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


def visual_change_scores(images):
    """Measure concentrated edge changes between consecutive decoded frames.

    This is intentionally a cheap OpenCV probe, not OCR. Subtitle appearance and
    disappearance change many edges inside a narrow horizontal band, so the C#
    scanner can send only those nearby native frames through PaddleOCR.
    """
    signatures = []
    for image in images:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        gray = cv2.resize(gray, (320, 80), interpolation=cv2.INTER_AREA)
        gray = cv2.GaussianBlur(gray, (3, 3), 0)
        signatures.append(cv2.Canny(gray, 50, 150))

    scores = []
    for before, after in zip(signatures, signatures[1:]):
        changed = cv2.absdiff(before, after)
        row_counts = np.count_nonzero(changed, axis=1)
        band_height = max(4, changed.shape[0] // 8)
        max_band = max(
            int(np.sum(row_counts[start:start + band_height]))
            for start in range(0, changed.shape[0] - band_height + 1)
        )
        scores.append(max_band / float(changed.shape[1] * band_height))
    return scores


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


def _same_text_row(left, right):
    left_box, right_box = left["box"], right["box"]
    if len(left_box) != 4 or len(right_box) != 4:
        return False
    overlap = min(left_box[3], right_box[3]) - max(left_box[1], right_box[1])
    left_height = max(1, left_box[3] - left_box[1])
    right_height = max(1, right_box[3] - right_box[1])
    return overlap >= min(left_height, right_height) * 0.55


def _stitch_overlapping_text(left, right):
    left, right = left.rstrip(), right.lstrip()
    maximum = min(len(left), len(right))
    for overlap in range(maximum, 0, -1):
        if left[-overlap:] == right[:overlap]:
            return left + right[overlap:]
    return left + right


def merge_inline_lines(lines):
    """Join detector fragments that occupy one subtitle baseline.

    PP-OCRv6 can return overlapping boxes such as ``少主……是`` and
    ``是不死丹帝，药逆命``. Joining raw texts with a newline duplicates the shared
    glyph after C# whitespace normalization. Geometry plus exact suffix/prefix
    overlap removes only text that Paddle observed twice; it never invents text.
    """
    rows = []
    for line in sorted(
        lines,
        key=lambda item: (
            (item["box"][1] + item["box"][3]) / 2 if len(item["box"]) == 4 else 0,
            item["box"][0] if len(item["box"]) == 4 else 0,
        ),
    ):
        row = next((candidate for candidate in rows if any(_same_text_row(member, line) for member in candidate)), None)
        if row is None:
            rows.append([line])
        else:
            row.append(line)
    rows.sort(key=lambda row: min(item["box"][1] if len(item["box"]) == 4 else 0 for item in row))

    output = []
    for row in rows:
        row_output = []
        for line in sorted(row, key=lambda item: item["box"][0] if len(item["box"]) == 4 else 0):
            if not row_output:
                row_output.append(line)
                continue
            before, after = row_output[-1], line
            before_box, after_box = before["box"], after["box"]
            row_height = max(before_box[3] - before_box[1], after_box[3] - after_box[1], 1)
            horizontal_gap = after_box[0] - before_box[2]
            if horizontal_gap > max(24, int(round(row_height * 0.45))):
                row_output.append(line)
                continue
            row_output[-1] = {
                "text": _stitch_overlapping_text(before["text"], after["text"]),
                "confidence": min(before["confidence"], after["confidence"]),
                "box": [
                    min(before_box[0], after_box[0]),
                    min(before_box[1], after_box[1]),
                    max(before_box[2], after_box[2]),
                    max(before_box[3], after_box[3]),
                ],
            }
        output.extend(row_output)
    return output


def parse_prediction(prediction):
    lines = []
    result_count = 0
    for item in prediction:
        result_count += 1
        data = as_mapping(item)
        if data.get("error"):
            raise RuntimeError(f"PaddleOCR trả lỗi: {data['error']}")
        missing = [key for key in ("rec_texts", "rec_scores") if key not in data]
        if "rec_boxes" not in data and "rec_polys" not in data:
            missing.append("rec_boxes/rec_polys")
        if missing:
            raise RuntimeError(f"PaddleOCR trả schema không hợp lệ, thiếu: {', '.join(missing)}")
        texts = as_list(data.get("rec_texts"))
        scores = as_list(data.get("rec_scores"))
        boxes = as_list(data.get("rec_boxes"))
        if not boxes:
            boxes = as_list(data.get("rec_polys"))
        if len(scores) != len(texts) or len(boxes) != len(texts):
            raise RuntimeError(
                "PaddleOCR trả số lượng text/score/box không khớp: "
                f"{len(texts)}/{len(scores)}/{len(boxes)}"
            )
        for index, text in enumerate(texts):
            text = str(text or "").strip()
            if not text:
                continue
            confidence = float(scores[index])
            box = normalize_box(boxes[index])
            lines.append({"text": text, "confidence": confidence, "box": box})
    if result_count == 0:
        raise RuntimeError("PaddleOCR không trả kết quả cho ảnh")
    lines = merge_inline_lines(lines)
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


def refine_short_text(engine, image, result, recover_blank=False, active_text=None):
    # Expanded detection boxes can swallow background strokes around a lone
    # glyph. Retry this same image with a tighter box, not a guessed character.
    lines = result["lines"]
    weak_glyph = len(lines) == 1 and len(lines[0]["text"]) == 1 and lines[0]["confidence"] < 0.90
    if not weak_glyph and not (recover_blank and not lines):
        return result
    try:
        tighter = parse_prediction(engine.predict(image, text_det_box_thresh=0.55,
            text_rec_score_thresh=0.45, text_det_unclip_ratio=0.8))
        candidates = tighter["lines"]
        if len(candidates) != 1 or len(candidates[0]["text"]) != 1:
            return result
        candidate = candidates[0]
        # A prior confirmed glyph may corroborate an actual matching reread;
        # never fill a blank from the hint alone or accept a different weak glyph.
        minimum = 0.80 if candidate["text"] == active_text else 0.90
        if candidate["confidence"] < minimum or candidate["confidence"] < result["confidence"] + 0.05:
            return result
        if lines:
            before, after = lines[0]["box"], candidate["box"]
            if len(before) != 4 or len(after) != 4:
                return result
            # A different high-confidence glyph elsewhere is not corroboration.
            if min(before[2], after[2]) <= max(before[0], after[0]) or min(before[3], after[3]) <= max(before[1], after[1]):
                return result
        return tighter
    except Exception:
        # A failed optional retry must not erase a valid first-pass result.
        return result


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
            encoded_probe = request.get("probe_images_base64")
            if encoded_probe is not None:
                if not isinstance(encoded_probe, list) or len(encoded_probe) < 2 or len(encoded_probe) > 65:
                    raise ValueError("probe_images_base64 phải có từ 2 đến 65 ảnh")
                probe_images = [decode_image(str(encoded or "").strip()) for encoded in encoded_probe]
                emit({
                    "id": request_id,
                    "ok": True,
                    "change_scores": visual_change_scores(probe_images),
                })
                continue
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
                    "results": [refine_short_text(engine, image, parse_prediction([prediction]),
                        bool(request.get("recover_short_blank", False)), request.get("active_short_text")) for image, prediction in zip(images, predictions)],
                })
                continue
            encoded = str(request.get("image_base64") or "").strip()
            if not encoded:
                raise ValueError("image_base64 rỗng")
            image = decode_image(encoded)
            result = parse_prediction(engine.predict(image, text_det_box_thresh=0.55, text_rec_score_thresh=0.45))
            result = refine_short_text(engine, image, result, bool(request.get("recover_short_blank", False)), request.get("active_short_text"))
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

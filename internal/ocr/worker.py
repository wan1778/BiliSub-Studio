import argparse
import base64
import hashlib
import json
import os
import sys
import traceback
from collections import OrderedDict


def parse_args():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--model-cache", required=True)
    parser.add_argument("--device", required=True, choices=("cpu", "gpu:0"))
    # Model tier (2026-09-26): Accurate scans keep Medium; Balanced/Fast use
    # Small (2.8x cheaper per image on field hardware, dialogue-equivalent —
    # A/B 2026-09-26). Defaults preserve the old single-tier behavior.
    parser.add_argument("--det-model", default="PP-OCRv6_medium_det")
    parser.add_argument("--rec-model", default="PP-OCRv6_medium_rec")
    return parser.parse_args()


ARGS = parse_args()
os.environ["PADDLE_PDX_CACHE_HOME"] = os.path.abspath(ARGS.model_cache)
os.environ.setdefault("PADDLE_PDX_MODEL_SOURCE", "BOS")
if ARGS.device == "cpu":
    os.environ.setdefault("FLAGS_use_mkldnn", "1")

import cv2
import numpy as np
import paddle


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


def edge_signatures(images):
    """Canny signatures tren gray thu nho (320x80), dung chung cho change/band/density."""
    signatures = []
    for image in images:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        gray = cv2.resize(gray, (320, 80), interpolation=cv2.INTER_AREA)
        gray = cv2.GaussianBlur(gray, (3, 3), 0)
        signatures.append(cv2.Canny(gray, 50, 150))
    return signatures


def pairwise_band_scores(signatures, row_from, row_to):
    """Diem doi giua 2 anh lien tiep, chi tren dai hang [row_from, row_to)."""
    height = signatures[0].shape[0]
    width = signatures[0].shape[1]
    band_height = max(4, height // 8)
    scores = []
    for before, after in zip(signatures, signatures[1:]):
        changed = cv2.absdiff(before, after)
        row_counts = np.count_nonzero(changed, axis=1)
        lo = max(0, row_from)
        hi = min(height - band_height + 1, row_to)
        if hi <= lo:
            scores.append(0.0)
            continue
        max_band = max(
            int(np.sum(row_counts[start:start + band_height]))
            for start in range(lo, hi)
        )
        scores.append(max_band / float(changed.shape[1] * band_height))
    return scores


def visual_change_scores(images):
    """Measure concentrated edge changes between consecutive decoded frames.

    This is intentionally a cheap OpenCV probe, not OCR. Subtitle appearance and
    disappearance change many edges inside a narrow horizontal band, so the C#
    scanner can send only those nearby native frames through PaddleOCR.
    """
    return pairwise_band_scores(edge_signatures(images), 0, 80)


def visual_band_change_scores(images):
    """Bien the shadow (E.3): nhu change_scores nhung chi nua duoi khung hinh
    (vung sub). Khong anh huong quyet dinh production, chi de hieu chuan offline."""
    return pairwise_band_scores(edge_signatures(images), 40, 80)


def visual_edge_density(images):
    """Mat do edge trung binh tung anh (tier-1 C proxy, shadow)."""
    densities = []
    for signature in edge_signatures(images):
        densities.append(float(np.count_nonzero(signature)) / float(signature.size))
    return densities


def style_frame_scores(image):
    """Do kieu sub 1 khung ROI (16/09, tu nhan dien khoi gat tay): ti le pixel
    sang (>=225, coi la net chu trang) + do toi trung vi cua vanh quanh chu
    (darkness = 255 - gray: vien den ~215-255, bong nhe ~75-135, nen sang <55).
    Tra (bright_ratio, ring_darkness|None). Khong bao gio nem."""
    try:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        bright = (gray >= 225).astype(np.uint8) * 255
        bright_ratio = float((bright > 0).mean())
        if bright_ratio < 0.003:
            return (bright_ratio, None)
        dilated = cv2.dilate(bright, np.ones((5, 5), np.uint8), iterations=1)
        ring = (dilated > 0) & (bright == 0)
        if not bool(ring.any()):
            return (bright_ratio, None)
        darkness = float(255.0 - float(np.median(gray[ring])))
        return (bright_ratio, darkness)
    except Exception:
        return (0.0, None)


def apply_style_filter(image, mode):
    """Loc chu khoi nen truoc det (16/09): giu pixel chu, nen thanh den.
    - strong (chi sub cung vien den): white top-hat + nguong 50 VA chi giu
      pixel co ring darkness >= 170 (vien den dam). Loai bo het chu chi co
      bong nhe / khong vien / chu trong sach/bien bao.
    - medium (bong nhe): giu pixel sang >=170 VA sang hon nen quanh (>=18).
    Fail-open: loi la tra anh goc (C# cung chi bat loc khi do chac chan)."""
    try:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        if mode == "strong":
            height = gray.shape[0]
            size = max(9, min(25, (height // 8) | 1))
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (size, size))
            background = cv2.morphologyEx(gray, cv2.MORPH_OPEN, kernel)
            flat = cv2.subtract(gray, background)
            # Tinh ring darkness (do toi cua vanh quanh pixel sang)
            bright = (flat >= 50).astype(np.uint8) * 255
            if not bool(bright.any()):
                return np.zeros_like(image)
            dilated = cv2.dilate(bright, np.ones((5, 5), np.uint8), iterations=1)
            ring = (dilated > 0) & (bright == 0)
            if not bool(ring.any()):
                return np.zeros_like(image)
            ring_vals = gray[ring]
            darkness = 255.0 - float(np.median(ring_vals))
            # CHI GIU NEU VIEN DEN DAM (darkness >= 170)
            if darkness < 170.0:
                return np.zeros_like(image)
            mask = bright
        elif mode == "medium":
            blur = cv2.GaussianBlur(gray, (0, 0), 3)
            contrast = cv2.subtract(gray, blur)
            mask = ((gray >= 170) & (contrast >= 18)).astype(np.uint8) * 255
        else:
            return image
        mask = cv2.medianBlur(mask, 3)
        out = np.zeros_like(image)
        out[mask > 0] = image[mask > 0]
        return out
    except Exception:
        return image


# ---------------------------------------------------------------------------
# Phan loai scene + tien xu ly thich ung theo case (17/09, y tuong user):
# moi frame tu phan loai nen/chu (chi dung OpenCV, vai ms) roi tien xu ly
# dung kieu truoc khi OCR: chu trang/nen trang -> CLAHE khuech dai shadow;
# nen nhieu -> bilateral + sharpen; tuong phan tot -> giu nguyen.
# KHONG tao 3 engine PaddleOCR rieng (6 instance x 2 worker tren VRAM 4GB =
# OOM): SplitOCREngine nhan det params theo tung goi (thresh/box_thresh/
# unclip_ratio) nen 1 engine dung chung + params theo case la du.
# ROI phan loai = 30% duoi khung (vung sub), worker tu cat (khong doi protocol).
# Fail-open: loi la anh goc + params mac dinh (hanh vi cu y het).
# ---------------------------------------------------------------------------
SCENE_HIGH_CONTRAST = "high_contrast"
SCENE_NOISY_BACKGROUND = "noisy_background"
SCENE_WHITE_ON_WHITE = "white_on_white"

# Det params theo case. high_contrast GIU NGUYEN mac dinh hien tai (thresh 0.3 /
# box 0.55 / unclip mac dinh) de khong regression khung thuong; noisy them
# tien xu ly la chinh; white_on_white mo rong theo thiet ke goc (unclip 2.0
# tat engine rec-cache Rieng frame do - hiem nen chap nhan duoc; cache ngoai
# BasePredictionCache van hit binh thuong).
SCENE_DET_PARAMS = {
    SCENE_HIGH_CONTRAST: {"thresh": 0.3, "box_thresh": 0.55, "unclip_ratio": None},
    SCENE_NOISY_BACKGROUND: {"thresh": 0.3, "box_thresh": 0.55, "unclip_ratio": None},
    SCENE_WHITE_ON_WHITE: {"thresh": 0.2, "box_thresh": 0.4, "unclip_ratio": 2.0},
}


def classify_scene(image, sep_thresh=40.0, highlight_thresh=0.45,
        texture_thresh=150.0, sep_thresh_noisy=70.0):
    """Phan loai nen/chu 1 frame. Khong bao gio nem (loi -> high_contrast)."""
    try:
        height = int(image.shape[0])
        region = image[int(height * 0.70):, :] if height > 0 else image
        gray = cv2.cvtColor(region, cv2.COLOR_BGR2GRAY)
        thresh_val, _ = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
        fg_mask = gray > thresh_val
        bg_mask = ~fg_mask
        fg_mean = float(gray[fg_mask].mean()) if bool(fg_mask.any()) else 0.0
        bg_mean = float(gray[bg_mask].mean()) if bool(bg_mask.any()) else 0.0
        separation = abs(fg_mean - bg_mean)
        highlight_ratio = float((gray > 220).sum()) / float(gray.size)
        texture_score = float(cv2.Laplacian(gray, cv2.CV_64F).var())
        if separation < sep_thresh and highlight_ratio > highlight_thresh:
            return SCENE_WHITE_ON_WHITE
        if texture_score > texture_thresh and separation < sep_thresh_noisy:
            return SCENE_NOISY_BACKGROUND
        return SCENE_HIGH_CONTRAST
    except Exception:
        return SCENE_HIGH_CONTRAST


def preprocess_scene(image, case):
    """Tien xu ly theo case. Loi la tra anh goc (khong resize -> box giu nguyen)."""
    try:
        if case == SCENE_WHITE_ON_WHITE:
            gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
            clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8))
            return cv2.cvtColor(clahe.apply(gray), cv2.COLOR_GRAY2BGR)
        if case == SCENE_NOISY_BACKGROUND:
            denoised = cv2.bilateralFilter(image, d=9, sigmaColor=75, sigmaSpace=75)
            gray = cv2.cvtColor(denoised, cv2.COLOR_BGR2GRAY)
            blur = cv2.GaussianBlur(gray, (0, 0), 2)
            sharpened = cv2.addWeighted(gray, 1.4, blur, -0.4, 0)
            return cv2.cvtColor(sharpened, cv2.COLOR_GRAY2BGR)
        return image
    except Exception:
        return image


def adapt_scene(image):
    """Phan loai + tien xu ly 1 frame. Tra (anh, case). Khong bao gio nem."""
    try:
        case = classify_scene(image)
        return preprocess_scene(image, case), case
    except Exception:
        return image, SCENE_HIGH_CONTRAST


# NgUu nguong do dam vet do (R vuot G,B). Vach laser/effect thuong dam (>40);
# chu do that hiem (sub Trung thuong trang/vang). Guard max_ratio: vet laser
# MA?NH (<25% khung) moi inpaint — do hon la chu do/canh do that -> giu nguyen.
RED_DOMINANCE_THRESH = 40
RED_MAX_RATIO = 0.25


def remove_red_artifact(image):
    """Phat hien vung bi nhuom do bat thuong (vet sang/laser de len chu) va
    inpaint tai tao, thay vi de model doan bua ky tu bi che (VD: nua cau dung,
    nua cau rac). Fail-open: khong vet do / loi la tra anh goc."""
    try:
        pixels = image.astype(np.int16)
        b, g, r = pixels[:, :, 0], pixels[:, :, 1], pixels[:, :, 2]
        red_mask = ((r - g) > RED_DOMINANCE_THRESH) & ((r - b) > RED_DOMINANCE_THRESH)
        if not bool(red_mask.any()):
            return image
        if float(red_mask.mean()) > RED_MAX_RATIO:
            return image
        mask = (red_mask.astype(np.uint8)) * 255
        kernel = np.ones((3, 3), np.uint8)
        mask = cv2.dilate(mask, kernel, iterations=1)
        return cv2.inpaint(image, mask, inpaintRadius=3, flags=cv2.INPAINT_TELEA)
    except Exception:
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


def _same_text_row(left, right):
    left_box, right_box = left["box"], right["box"]
    if len(left_box) != 4 or len(right_box) != 4:
        return False
    overlap = min(left_box[3], right_box[3]) - max(left_box[1], right_box[1])
    left_height = max(1, left_box[3] - left_box[1])
    right_height = max(1, right_box[3] - right_box[1])
    short_height = min(left_height, right_height)
    if overlap < short_height * 0.55:
        return False
    # Small punctuation may sit at the top, middle or bottom of a text row.
    # Letters and digits must also agree on font height and vertical alignment.
    smaller = left if left_height <= right_height else right
    if any(char.isalnum() for char in smaller["text"]):
        center_delta = abs((left_box[1] + left_box[3]) - (right_box[1] + right_box[3])) / 2
        baseline_delta = abs(left_box[3] - right_box[3])
        return (short_height >= max(left_height, right_height) * 0.75
                and center_delta <= short_height * 0.25
                and baseline_delta <= short_height * 0.25)
    return True


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
            if (not _same_text_row(before, after)
                    or horizontal_gap > max(24, int(round(row_height * 0.45)))):
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


class SplitOCREngine:
    """PaddleOCR 3.7 with true detector batching and stable recognition context."""

    DET_BATCH_SIZE = 8
    REC_BATCH_SIZE = 6
    MAX_ENTRIES = 32
    MAX_BYTES = 8 * 1024 * 1024

    def __init__(self, det_model=None, rec_model=None):
        from paddleocr import TextDetection, TextRecognition
        from paddlex.inference.pipelines.components import CropByPolys, SortQuadBoxes, convert_points_to_boxes

        det_name = det_model or DETECTION_MODEL
        rec_name = rec_model or RECOGNITION_MODEL
        self.detector = TextDetection(model_name=det_name, device=ARGS.device)
        self.recognizer = TextRecognition(model_name=rec_name, device=ARGS.device)
        self.crop = CropByPolys(det_box_type="quad")
        self.sort = SortQuadBoxes()
        self.boxes = convert_points_to_boxes
        self.entries = OrderedDict()
        self.byte_count = 0

    def predict(self, images, *, text_det_box_thresh=0.55, text_rec_score_thresh=0.45,
            text_det_unclip_ratio=None, text_det_thresh=0.3):
        images = images if isinstance(images, list) else [images]
        output = []
        # Optional contextual rereads always execute recognition, even on exact pixels.
        use_cache = text_det_unclip_ratio is None
        perf = {"det_ms": 0.0, "rec_ms": 0.0, "det_boxes": 0, "rec_crops": 0, "cache_hits": 0}
        try:
            detections = []
            for start in range(0, len(images), self.DET_BATCH_SIZE):
                detection_images = images[start:start + self.DET_BATCH_SIZE]
                detector_input = detection_images[0] if len(detection_images) == 1 else detection_images
                tick = __import__("time").perf_counter()
                detected = list(self.detector.predict(detector_input, batch_size=len(detection_images),
                    limit_side_len=64, limit_type="min", max_side_limit=4000,
                    thresh=text_det_thresh, box_thresh=text_det_box_thresh,
                    unclip_ratio=1.5 if text_det_unclip_ratio is None else text_det_unclip_ratio))
                perf["det_ms"] += (__import__("time").perf_counter() - tick) * 1000.0
                if len(detected) != len(detection_images):
                    raise RuntimeError("PaddleOCR detection batch result count mismatch")
                detections.extend(detected)

            for image, detection in zip(images, detections):
                data = as_mapping(detection)
                if data.get("error") or "dt_polys" not in data:
                    raise RuntimeError("PaddleOCR invalid detection batch result")
                polys = self.sort(data["dt_polys"])
                crops = list(self.crop(image, polys)) if len(polys) else []
                if len(crops) != len(polys):
                    raise RuntimeError("PaddleOCR crop count mismatch")
                pairs = [(poly, crop) for poly, crop in zip(polys, crops)
                    if crop.size > 0 and crop.shape[0] > 0 and crop.shape[1] > 0]
                perf["det_boxes"] += len(pairs)
                order = sorted(range(len(pairs)),
                    key=lambda index: pairs[index][1].shape[1] / float(pairs[index][1].shape[0]))
                readings = {}
                for start in range(0, len(order), self.REC_BATCH_SIZE):
                    indices = order[start:start + self.REC_BATCH_SIZE]
                    batch = [pairs[index][1] for index in indices]
                    key_parts = []
                    if use_cache:
                        for crop in batch:
                            metadata = json.dumps([crop.shape, crop.dtype.str], separators=(",", ":")).encode("ascii")
                            pixels = crop.tobytes(order="C")
                            digest = hashlib.sha256(metadata)
                            digest.update(pixels)
                            key_parts.append((metadata, digest.digest(), pixels))
                    key = tuple(key_parts)
                    if use_cache and key in self.entries:
                        self.entries.move_to_end(key)
                        serialized = self.entries[key][0]
                        perf["cache_hits"] += len(batch)
                    else:
                        # Keep each frame's ratio-sorted crop group intact: Paddle
                        # pads recognition to the widest crop in a batch, and mixing
                        # frames measurably changes confidence used by the tracker.
                        perf["rec_crops"] += len(batch)
                        tick = __import__("time").perf_counter()
                        results = list(self.recognizer.predict(batch,
                            batch_size=self.REC_BATCH_SIZE, return_word_box=False))
                        perf["rec_ms"] += (__import__("time").perf_counter() - tick) * 1000.0
                        if len(results) != len(batch):
                            raise RuntimeError("PaddleOCR recognition result count mismatch")
                        fresh = []
                        for result in results:
                            result = as_mapping(result)
                            if (result.get("error") or not isinstance(result.get("rec_text"), str)
                                    or "rec_score" not in result):
                                raise RuntimeError("PaddleOCR invalid recognition result")
                            score = float(result["rec_score"])
                            if not 0 <= score <= 1:
                                raise RuntimeError("PaddleOCR invalid recognition score")
                            fresh.append([result["rec_text"], score])
                        serialized = json.dumps(fresh, ensure_ascii=False, allow_nan=False,
                            separators=(",", ":")).encode("utf-8")
                        size = sum(len(part) for crop_key in key for part in crop_key) + len(serialized)
                        if use_cache and size <= self.MAX_BYTES:
                            while self.entries and (len(self.entries) >= self.MAX_ENTRIES
                                    or self.byte_count + size > self.MAX_BYTES):
                                _, (_, removed_size) = self.entries.popitem(last=False)
                                self.byte_count -= removed_size
                            self.entries[key] = (serialized, size)
                            self.byte_count += size
                    readings.update(zip(indices, json.loads(serialized)))
                accepted = [i for i in range(len(pairs)) if readings[i][1] >= text_rec_score_thresh]
                output.append({"rec_texts": [readings[i][0] for i in accepted],
                    "rec_scores": [readings[i][1] for i in accepted],
                    "rec_boxes": self.boxes([pairs[i][0] for i in accepted])})
            self.last_perf = dict(perf, images=len(images))
            return output
        except Exception:
            # Do not retain partial successes from a failed request. The outer
            # worker/client retain their existing error and restart/retry paths.
            self.entries.clear()
            self.byte_count = 0
            raise


class BasePredictionCache:
    """Worker-local base OCR only; contextual retries must never enter this cache."""

    MAX_ENTRIES = 8
    MAX_BYTES = 8 * 1024 * 1024

    def __init__(self, engine):
        self.engine = engine
        self.entries = OrderedDict()
        self.byte_count = 0

    def predict(self, images):
        if not 1 <= len(images) <= 8:
            raise ValueError("base prediction requires 1-8 images")
        keys, misses, resolved = [], {}, {}
        for image in images:
            metadata = json.dumps([image.shape, image.dtype.str], separators=(",", ":")).encode("ascii")
            pixels = image.tobytes(order="C")
            digest = hashlib.sha256(metadata)
            digest.update(pixels)
            # Retain exact bytes as a collision guard, not just a digest match.
            key = (metadata, digest.digest(), pixels)
            keys.append(key)
            if key in self.entries:
                self.entries.move_to_end(key)
                resolved[key] = self.entries[key][0]
            else:
                misses.setdefault(key, image)

        # Trung thuc timing: request phuc vu 100% tu cache khong goi engine.predict,
        # last_perf cu se la cua request truoc. Reset ve 0 + dem cache hit that.
        if not misses:
            self.engine.last_perf = {"det_ms": 0.0, "rec_ms": 0.0, "det_boxes": 0,
                "rec_crops": 0, "cache_hits": len(images), "images": len(images)}
        if misses:
            # Thich ung scene theo tung anh: phan loai + tien xu ly, gom nhom
            # theo case, moi nhom goi engine.predict rieng voi det params cua
            # nhom (high_contrast giu params mac dinh cu). Cache ngoai key theo
            # pixel GOC (phan loai tat-dinh nen khong ket qua cu).
            groups = {}
            for key, image in zip(misses, misses.values()):
                adapted, case = adapt_scene(image)
                groups.setdefault(case, []).append((key, adapted))
            predictions_by_key = {}
            for case, items in groups.items():
                params = SCENE_DET_PARAMS[case]
                group_keys = [item_key for item_key, _ in items]
                group_images = [item_image for _, item_image in items]
                group_predictions = list(self.engine.predict(
                    group_images[0] if len(group_images) == 1 else group_images,
                    text_det_box_thresh=params["box_thresh"], text_rec_score_thresh=0.45,
                    text_det_thresh=params["thresh"], text_det_unclip_ratio=params["unclip_ratio"]))
                if len(group_predictions) != len(group_keys):
                    raise RuntimeError(f"PaddleOCR base prediction count mismatch: {len(group_predictions)}/{len(group_keys)}")
                predictions_by_key.update(zip(group_keys, group_predictions))
            predictions = [predictions_by_key[key] for key in misses]
            # Validate the whole miss batch before publishing anything to the cache.
            fresh = {key: json.dumps(parse_prediction([prediction]), ensure_ascii=False,
                allow_nan=False, separators=(",", ":")).encode("utf-8")
                for key, prediction in zip(misses, predictions)}
            resolved.update(fresh)
            for key, result in fresh.items():
                size = sum(len(part) for part in key) + len(result)
                if size > self.MAX_BYTES:
                    continue
                while self.entries and (len(self.entries) >= self.MAX_ENTRIES
                        or self.byte_count + size > self.MAX_BYTES):
                    _, (_, removed_size) = self.entries.popitem(last=False)
                    self.byte_count -= removed_size
                self.entries[key] = (result, size)
                self.byte_count += size

        # Fresh nested objects for every occurrence, including duplicates in a batch.
        return [json.loads(resolved[key]) for key in keys]


def main():
    cuda_available = validate_device()
    engine = SplitOCREngine(ARGS.det_model, ARGS.rec_model)
    base_predictions = BasePredictionCache(engine)
    emit({
        "type": "ready",
        "engine": ENGINE_NAME,
        "paddleocr": "3.7.0",
        "paddle": str(paddle.__version__),
        "models": [ARGS.det_model, ARGS.rec_model],
        "det_model": ARGS.det_model,
        "rec_model": ARGS.rec_model,
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
            encoded_style = request.get("style_probe_images_base64")
            if encoded_style is not None:
                if not isinstance(encoded_style, list) or len(encoded_style) < 2 or len(encoded_style) > 65:
                    raise ValueError("style_probe_images_base64 phải có từ 2 đến 65 ảnh")
                style_images = [decode_image(str(encoded or "").strip()) for encoded in encoded_style]
                emit({
                    "id": request_id,
                    "ok": True,
                    "style_scores": [{"bright": bright, "ring": ring}
                        for bright, ring in (style_frame_scores(img) for img in style_images)],
                })
                continue
            encoded_probe = request.get("probe_images_base64")
            if encoded_probe is not None:
                if not isinstance(encoded_probe, list) or len(encoded_probe) < 2 or len(encoded_probe) > 65:
                    raise ValueError("probe_images_base64 phải có từ 2 đến 65 ảnh")
                probe_images = [decode_image(str(encoded or "").strip()) for encoded in encoded_probe]
                emit({
                    "id": request_id,
                    "ok": True,
                    "change_scores": visual_change_scores(probe_images),
                    # E.3 shadow (additive, production khong doc): diem doi dai sub
                    # + mat do edge tung anh, de C# ghi histogram hieu chuan offline.
                    # Worker cu khong co 2 field nay — C# phai chiu vang mat.
                    "band_change_scores": visual_band_change_scores(probe_images),
                    "edge_density": visual_edge_density(probe_images),
                })
                continue
            encoded_batch = request.get("images_base64")
            if encoded_batch is not None:
                if not isinstance(encoded_batch, list) or not encoded_batch or len(encoded_batch) > 8:
                    raise ValueError("images_base64 phải là batch 1-8 ảnh")
                images = [decode_image(str(encoded or "").strip()) for encoded in encoded_batch]
                style_mode = str(request.get("style_filter") or "").strip().lower()
                if style_mode in ("medium", "strong"):
                    images = [apply_style_filter(img, style_mode) for img in images]
                # Vet sang do (laser/effect) de len chu -> inpaint truoc khi OCR
                # de model khong doan bua ky tu bi che (fail-open: khong vet la giu nguyen).
                images = [remove_red_artifact(img) for img in images]
                predictions = base_predictions.predict(images)
                # Snapshot NGAY sau base predict: refine ben duoi co the goi engine.predict
                # lai (retry) va ghi de last_perf. So nay la pass chinh (retry hiem, co bound).
                perf_snapshot = dict(engine.last_perf)
                refined = [refine_short_text(engine, image, prediction,
                    bool(request.get("recover_short_blank", False)), request.get("active_short_text")) for image, prediction in zip(images, predictions)]
                emit({
                    "id": request_id,
                    "ok": True,
                    "results": refined,
                    # D.1 det/rec split: C# tich luy de quyet Phase 2. Old C# bo qua field.
                    "perf_ms": perf_snapshot,
                })
                continue
            encoded = str(request.get("image_base64") or "").strip()
            if not encoded:
                raise ValueError("image_base64 rỗng")
            image = decode_image(encoded)
            style_mode = str(request.get("style_filter") or "").strip().lower()
            if style_mode in ("medium", "strong"):
                image = apply_style_filter(image, style_mode)
            image = remove_red_artifact(image)
            result = base_predictions.predict([image])[0]
            perf_snapshot = dict(engine.last_perf)
            result = refine_short_text(engine, image, result, bool(request.get("recover_short_blank", False)), request.get("active_short_text"))
            result["id"] = request_id
            result["perf_ms"] = perf_snapshot
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

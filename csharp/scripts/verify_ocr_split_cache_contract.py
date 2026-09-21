"""Stdlib fake-model contracts for the production two-stage OCR engine."""
import json
import types
from collections import OrderedDict

from verify_ocr_pixel_cache_contract import Image, load_definitions


class Crop(Image):
    def __init__(self, value, shape=(10, 20, 3), dtype="|u1"):
        super().__init__(bytes([value]) * 12, shape, dtype)
        self.size = 12 if shape[0] and shape[1] else 0


class Detector:
    def __init__(self):
        self.calls = []
        self.failure = None

    def predict(self, image, **kwargs):
        self.calls.append(kwargs)
        images = image if isinstance(image, list) else [image]
        assert kwargs == dict(batch_size=len(images), limit_side_len=64, limit_type="min",
            max_side_limit=4000, thresh=.3, box_thresh=.55,
            unclip_ratio=kwargs["unclip_ratio"])
        if self.failure is not None:
            yield self.failure
        else:
            for source in images:
                yield {"dt_polys": source.pairs}


class Recognizer:
    def __init__(self):
        self.calls = []
        self.failure = None
        self.score = .99

    def predict(self, crops, **kwargs):
        assert kwargs == dict(batch_size=6, return_word_box=False)
        self.calls.append(crops)
        if isinstance(self.failure, Exception):
            raise self.failure
        if self.failure is not None:
            yield from self.failure
            return
        for crop in crops:
            yield {"rec_text": chr(0x4e00 + crop.pixels[0]), "rec_score": self.score}


def frame(*crops, offset=0, background=0):
    image = Image(bytes([background]) * 12)
    image.pairs = [([offset, i * 30, offset + 20, i * 30 + 10], crop)
        for i, crop in enumerate(crops)]
    return image


def engine(worker):
    result = worker["SplitOCREngine"].__new__(worker["SplitOCREngine"])
    result.detector, result.recognizer = Detector(), Recognizer()
    result.sort = lambda pairs: pairs
    result.crop = lambda image, pairs: [crop for _, crop in pairs]
    result.boxes = lambda pairs: [list(box) for box, _ in pairs]
    result.entries, result.byte_count = OrderedDict(), 0
    return result


def verify():
    worker = load_definitions()
    a, b, c = Crop(1), Crop(2), Crop(3)
    split = engine(worker)
    cache = worker["BasePredictionCache"](split)
    first = cache.predict([frame(a, background=1)])[0]
    second = cache.predict([frame(a, background=2, offset=100)])[0]
    assert first["text"] == second["text"]
    assert second["lines"][0]["box"][0] == 100, "boxes must come from current detection"
    assert len(split.detector.calls) == 2 and len(split.recognizer.calls) == 1
    second["lines"][0]["box"][0] = 900
    cache.predict([frame(b, background=3)])
    assert len(split.recognizer.calls) == 2, "new glyph must recognize"
    split.predict(frame(a, b))
    assert len(split.recognizer.calls) == 3, "new line must recognize the original whole batch"
    split.predict(frame(a, c))
    assert len(split.recognizer.calls) == 4, "partial hit must not change padding context"

    split = engine(worker)
    wide = Crop(4, shape=(10, 90, 3))
    result = split.predict([frame(wide, a, b), frame(wide, a, b, offset=50)])
    assert len(split.detector.calls) == 1 and split.detector.calls[0]["batch_size"] == 2
    assert len(split.recognizer.calls) == 1
    assert split.recognizer.calls[0] == [a, b, wide], "stable aspect-ratio ordering"
    assert result[0]["rec_texts"] == [chr(0x4e04), chr(0x4e01), chr(0x4e02)]
    result[0]["rec_texts"][0] = "mutated"
    assert result[1]["rec_texts"][0] == chr(0x4e04)
    for changed in [Crop(4, (10, 91, 3)), Crop(4, (9, 100, 3)), Crop(4, dtype="|i1"), Crop(5)]:
        before = len(split.recognizer.calls)
        split.predict(frame(changed, a, b))
        assert len(split.recognizer.calls) == before + 1, "crop jitter/shape/dtype/pixels must miss"
    assert split.predict(frame())[0]["rec_texts"] == []
    assert split.predict(frame(Crop(1, (0, 20, 3))))[0]["rec_texts"] == []

    # A changed seventh line must not invalidate the preceding exact six-line batch.
    split = engine(worker)
    six = [Crop(i) for i in range(6)]
    split.predict(frame(*six, Crop(8)))
    split.predict(frame(*six, Crop(9)))
    assert [len(batch) for batch in split.recognizer.calls] == [6, 1, 1]
    assert len(split.detector.calls) == 2
    print("PASS split fixture: 2 detections retained, 14 -> 8 recognized crops (six-line batch + changed line)")

    # Byte/entry LRU, oversized bypass, immutable serialization, model lifetime.
    split = engine(worker)
    split.MAX_ENTRIES = 2
    for crop in (a, b, a, c):
        split.predict(frame(crop))
    assert len(split.recognizer.calls) == 3 and len(split.entries) == 2
    split.predict(frame(b))
    assert len(split.recognizer.calls) == 4
    assert split.byte_count == sum(sum(len(part) for crop_key in key for part in crop_key) + len(value[0])
        for key, value in split.entries.items())
    entry_size = next(iter(split.entries.values()))[1]
    split = engine(worker)
    split.MAX_BYTES = entry_size
    for crop in (a, b, c):
        split.predict(frame(crop))
        assert len(split.entries) == 1 and split.byte_count == entry_size
    split = engine(worker)
    split.MAX_BYTES = 1
    split.predict([frame(a), frame(a)])
    assert len(split.recognizer.calls) == 2 and not split.entries and split.byte_count == 0
    assert not engine(worker).entries

    # Exact bytes remain part of identity even if the digest collides.
    worker["hashlib"] = types.SimpleNamespace(sha256=lambda _: types.SimpleNamespace(
        update=lambda _: None, digest=lambda: bytes(32)))
    split = engine(worker)
    split.predict([frame(a), frame(b)])
    assert len(split.detector.calls) == 1 and len(split.recognizer.calls) == 2

    for failure in [RuntimeError("failed"), [], [{}], [{"error": "failed"}],
            [{"rec_text": "x", "rec_score": float("nan")}],
            [{"rec_text": "x", "rec_score": 2}], [{"rec_text": [], "rec_score": .9}]]:
        split = engine(worker)
        split.predict(frame(a))
        split.recognizer.failure = failure
        try:
            split.predict(frame(b))
        except (RuntimeError, ValueError):
            pass
        else:
            raise AssertionError("invalid recognition accepted")
        assert not split.entries and split.byte_count == 0
        split.recognizer.failure = None
        split.predict(frame(b))
        assert len(split.recognizer.calls) == 3, "failure must retry inference"
    split = engine(worker)
    split.detector.failure = {"error": "failed"}
    try:
        split.predict(frame(a))
    except RuntimeError:
        pass
    else:
        raise AssertionError("invalid detection accepted")
    assert not split.entries and not split.recognizer.calls

    # Contextual retry is fresh each time and never publishes a refined reading.
    split = engine(worker)
    split.recognizer.score = .4
    image = frame(a)
    base = worker["parse_prediction"](split.predict(image))
    assert not base["detected"]
    split.recognizer.score = .85
    for hint, expected in [(chr(0x4e01), chr(0x4e01)), (chr(0x4e02), "")]:
        refined = worker["refine_short_text"](split, image, base, True, hint)
        assert refined["text"] == expected
    assert len(split.recognizer.calls) == 3
    assert worker["parse_prediction"](split.predict(image))["text"] == ""
    assert len(split.recognizer.calls) == 3
    assert [call["unclip_ratio"] for call in split.detector.calls] == [1.5, .8, .8, 1.5]
    print("PASS split OCR: detection, exact crop groups, batch context/order, jitter, isolation, bounds, collision, errors, refinement")


if __name__ == "__main__":
    verify()

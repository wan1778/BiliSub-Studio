"""Execute production worker definitions without importing Paddle/OpenCV/NumPy."""
from __future__ import annotations

import ast
import hashlib
import io
import json
import pathlib
import sys
import types
from collections import OrderedDict


WORKER = pathlib.Path(__file__).resolve().parents[2] / "internal" / "ocr" / "worker.py"


def load_definitions():
    tree = ast.parse(WORKER.read_text(encoding="utf-8"))
    definitions = ast.Module(body=[node for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.ClassDef))], type_ignores=[])
    namespace = {"hashlib": hashlib, "json": json, "OrderedDict": OrderedDict,
        "np": types.SimpleNamespace(integer=int, floating=float)}
    exec(compile(definitions, str(WORKER), "exec"), namespace)
    return namespace


class Image:
    def __init__(self, pixels, shape=(2, 2, 3), dtype="|u1"):
        self.pixels, self.shape = pixels, shape
        self.dtype = types.SimpleNamespace(str=dtype)

    def tobytes(self, order):
        assert order == "C"
        return self.pixels


class Engine:
    def __init__(self):
        self.calls, self.images, self.retries = 0, 0, 0
        self.failure = None
        self.blank = False

    def predict(self, images, **kwargs):
        assert kwargs["text_det_box_thresh"] == .55
        assert kwargs["text_rec_score_thresh"] == .45
        if "text_det_unclip_ratio" in kwargs:
            assert kwargs["text_det_unclip_ratio"] == .8
            self.retries += 1
            return [{"rec_texts": ["\u8d70"], "rec_scores": [.85], "rec_boxes": [[1, 1, 8, 8]]}]
        self.calls += 1
        images = images if isinstance(images, list) else [images]
        self.images += len(images)
        if isinstance(self.failure, Exception):
            raise self.failure
        if self.failure is not None:
            return self.failure
        return [{"rec_texts": [] if self.blank else [str(image.tobytes(order="C")[0])],
            "rec_scores": [] if self.blank else [.99],
            "rec_boxes": [] if self.blank else [[1, 1, 8, 8]]} for image in images]


def verify():
    worker = load_definitions()
    Cache = worker["BasePredictionCache"]
    a, b, c = [Image(bytes([value]) + bytes(11)) for value in (1, 2, 3)]
    engine = Engine()
    cache = Cache(engine)
    assert Cache.MAX_ENTRIES == 8 and Cache.MAX_BYTES == 8 * 1024 * 1024
    first = cache.predict([a])[0]
    first["id"] = 100
    first["lines"][0]["box"][0] = 999
    first["text"] = "contaminated"
    repeated = cache.predict([Image(a.pixels)])[0]
    assert repeated["text"] == "1" and "id" not in repeated and repeated["lines"][0]["box"][0] == 1
    assert (engine.calls, engine.images) == (1, 1)
    assert cache.predict([b])[0]["text"] == "2"  # One changed pixel must miss.
    assert (engine.calls, engine.images) == (2, 2)
    for changed_metadata in [Image(a.pixels, shape=(1, 4, 3)), Image(a.pixels, dtype="|i1")]:
        cache.predict([changed_metadata])
    assert engine.images == 4, "shape and dtype must participate in identity"

    engine = Engine()
    cache = Cache(engine)
    baseline = Engine()
    expected = [[worker["parse_prediction"]([prediction]) for prediction in baseline.predict(images,
        text_det_box_thresh=.55, text_rec_score_thresh=.45)] for images in ([b, a, b, c], [c, b, a, b])]
    batch = cache.predict([b, a, b, c])
    assert batch == expected[0]
    batch[0]["lines"][0]["box"][0] = 999
    assert batch[2]["lines"][0]["box"][0] == 1, "duplicate results must not alias"
    assert cache.predict([c, b, a, b]) == expected[1]
    assert (baseline.calls, baseline.images) == (2, 8)
    assert (engine.calls, engine.images) == (1, 3)
    print("PASS exact-pixel fixture: two four-frame batches, 8 -> 3 base inference images; 2 -> 1 predict calls")

    # Force a digest collision: differing bytes must still miss.
    class Collision:
        def update(self, _pixels):
            pass

        def digest(self):
            return bytes(32)

    worker["hashlib"] = types.SimpleNamespace(sha256=lambda _metadata: Collision())
    collision_engine = Engine()
    collision_cache = Cache(collision_engine)
    assert [r["text"] for r in collision_cache.predict([a, b, a])] == ["1", "2", "1"]
    assert collision_engine.images == 2
    worker["hashlib"] = hashlib

    # Entry LRU, byte LRU, oversize bypass, and exact accounting of retained data.
    cache = Cache(Engine())
    for value in range(8):
        cache.predict([Image(bytes([value]) + bytes(11))])
    cache.predict([Image(bytes(12))])  # Refresh oldest entry.
    cache.predict([Image(bytes([8]) + bytes(11))])
    assert len(cache.entries) == 8 and cache.byte_count <= cache.MAX_BYTES
    assert cache.byte_count == sum(sum(len(part) for part in key) + len(value[0])
        for key, value in cache.entries.items())
    before = cache.engine.images
    cache.predict([Image(bytes(12))])
    assert cache.engine.images == before
    cache.predict([a])  # Entry 1, not the refreshed entry 0, was evicted.
    assert cache.engine.images == before + 1
    entry_size = next(iter(cache.entries.values()))[1]
    cache = Cache(Engine())
    cache.MAX_BYTES = entry_size
    cache.predict([a, b, c])
    assert len(cache.entries) == 1 and cache.byte_count == entry_size
    cache.predict([a])
    assert cache.engine.images == 4 and cache.byte_count == entry_size
    assert [r["text"] for r in cache.predict([a, b, c, a])] == ["1", "2", "3", "1"], \
        "evicting a hit during a mixed batch must not lose its result"
    # A fresh worker starts empty and bypasses entries larger than its byte budget.
    cache = Cache(Engine())
    cache.MAX_BYTES = entry_size - 1
    cache.predict([a, a])
    cache.predict([a])
    assert not cache.entries and cache.byte_count == 0 and cache.engine.images == 2

    for failure in [RuntimeError("inference failed"), [], [{"error": "invalid"}],
            [{"rec_texts": ["bad"], "rec_scores": [], "rec_boxes": []}],
            [{"rec_texts": ["bad"], "rec_scores": [float("nan")], "rec_boxes": [[1, 1, 8, 8]]}]]:
        engine = Engine()
        engine.failure = failure
        cache = Cache(engine)
        try:
            cache.predict([a])
        except (RuntimeError, ValueError):
            pass
        else:
            raise AssertionError("invalid prediction accepted")
        assert not cache.entries and cache.byte_count == 0
        engine.failure = None
        cache.predict([a])
        assert engine.calls == 2, "error must not become a cache hit"
    engine = Engine()
    engine.failure = [Engine().predict(a, text_det_box_thresh=.55, text_rec_score_thresh=.45)[0], {}]
    cache = Cache(engine)
    try:
        cache.predict([a, b])
    except RuntimeError:
        pass
    else:
        raise AssertionError("partially invalid batch accepted")
    assert not cache.entries and cache.byte_count == 0

    # Drive the actual request loop: repeated blank pixels with different hints
    # must still run the real contextual refinement on each requested frame.
    engine = Engine()
    engine.blank = True
    requests = [
        {"id": 1, "image_base64": "a"},
        {"id": 2, "image_base64": "a", "recover_short_blank": True, "active_short_text": "\u8d70"},
        {"id": 3, "image_base64": "a", "recover_short_blank": True, "active_short_text": "\u5f92"},
        {"id": 4, "images_base64": ["a", "a"], "recover_short_blank": True, "active_short_text": "\u8d70"},
        {"id": 5, "image_base64": "a"},
        {"id": 6, "image_base64": "invalid"},
        {"id": 7, "image_base64": "a"},
    ]
    output = []
    worker.update({"validate_device": lambda: False, "SplitOCREngine": lambda: engine,
        "ARGS": types.SimpleNamespace(device="cpu"), "ENGINE_NAME": "fixture",
        "DETECTION_MODEL": "fixture", "RECOGNITION_MODEL": "fixture",
        "paddle": types.SimpleNamespace(__version__="fixture"),
        "sys": types.SimpleNamespace(stdin=io.StringIO("\n".join(json.dumps(r) for r in requests))),
        "decode_image": lambda encoded: {"a": a}[encoded], "emit": output.append})
    worker["main"]()
    assert [r["id"] for r in output[1:]] == list(range(1, 8))
    assert [output[i]["text"] for i in (1, 2, 3, 5, 7)] == ["", "\u8d70", "", "", ""]
    assert [r["text"] for r in output[4]["results"]] == ["\u8d70", "\u8d70"]
    assert output[6]["ok"] is False
    assert (engine.calls, engine.images, engine.retries) == (1, 1, 4)
    worker["sys"].stdin = io.StringIO(json.dumps(requests[0]))
    worker["main"]()
    assert engine.calls == 2, "new model/worker lifetime must not reuse prior cache"
    print("PASS exact-pixel cache: reuse, one-pixel/metadata misses, collision guard, ordering, isolation, LRU/bytes, errors, per-frame refinement and lifetime")


def verify_real_arrays():
    # Optional local check; the normal Windows contract gate needs only stdlib.
    import cv2
    import numpy as np

    worker = load_definitions()
    worker.update({"np": np, "cv2": cv2})
    engine = Engine()
    cache = worker["BasePredictionCache"](engine)
    pixels = np.zeros((320, 1280, 3), dtype=np.uint8)
    ok, encoded = cv2.imencode(".jpg", pixels)
    assert ok
    image = cv2.imdecode(encoded, cv2.IMREAD_COLOR)
    cache.predict([image])
    cache.predict([cv2.imdecode(encoded, cv2.IMREAD_COLOR)])
    # Equal values in a non-contiguous array must serialize in logical C order.
    noncontiguous = np.asfortranarray(image)
    assert not noncontiguous.flags.c_contiguous
    cache.predict([noncontiguous])
    assert engine.images == 1
    changed = image.copy()
    changed[-1, -1, -1] = 1
    cache.predict([changed])
    assert engine.images == 2, "one changed channel at the last pixel must miss"
    for value in range(1, 9):
        pixels[0, 0, 0] = value
        cache.predict([pixels])
        assert cache.byte_count <= cache.MAX_BYTES and len(cache.entries) <= cache.MAX_ENTRIES
    assert len(cache.entries) == 6, "8 MiB must cap retained 1280x320 BGR images before entry limit"
    print("PASS real NumPy/OpenCV fixtures: independent JPEG decode reuse, C-order identity, last-pixel miss, 8 MiB eviction")


if __name__ == "__main__":
    verify()
    if "--numpy" in sys.argv[1:]:
        verify_real_arrays()

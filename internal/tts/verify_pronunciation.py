"""Pronunciation regression lock for Vietnamese TTS normalization.

Runs the exact normalization path from internal/tts/worker.py
(TTS_DICTIONARY_* overrides + single-letter spelling + the packaged
vietnormalizer) and fails loudly on any mispronunciation.

Usage: py -3 internal/tts/verify_pronunciation.py
"""
import re
import sys
import unicodedata
from pathlib import Path

WORKER = Path(__file__).resolve().parent / "worker.py"
VENV_SITE = (
    Path(r"E:\du_an\BiliSub-Studio\csharp\publish\BiliSubStudio.App")
    / "Tools" / "TTS" / "runtime-piper" / "venv" / "Lib" / "site-packages"
)
sys.path.insert(0, str(VENV_SITE))

import ast

source = WORKER.read_text(encoding="utf-8")
tree = ast.parse(source)
by_name = {}
for node in tree.body:
    if isinstance(node, (ast.Assign, ast.AnnAssign)):
        targets = [node.targets[0]] if isinstance(node, ast.Assign) else [node.target]
        for target in targets:
            if isinstance(target, ast.Name):
                by_name[target.id] = node
    elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
        by_name[node.name] = node


def extract(name):
    if name not in by_name:
        raise AssertionError(f"cannot find {name} in worker.py")
    return ast.get_source_segment(source, by_name[name])


namespace = {"re": re, "unicodedata": unicodedata}
exec(extract("TTS_DICTIONARY_DROPS"), namespace)
exec(extract("TTS_DICTIONARY_FIXES"), namespace)
exec(extract("_SINGLE_UPPER_LETTER_PATTERN"), namespace)
exec(extract("normalize_vietnamese_text"), namespace)
exec(extract("apply_tts_dictionary_overrides"), namespace)

from vietnormalizer import VietnameseNormalizer

normalizer = VietnameseNormalizer()
namespace["apply_tts_dictionary_overrides"](normalizer)
normalize = namespace["normalize_vietnamese_text"]

CASES = [
    # Vietnamese words hijacked by the packaged dictionary must keep spelling.
    ("Thăng Long", "thăng long"),
    ("song ca", "song ca"),
    ("hoa sen", "hoa sen"),
    ("đan len", "đan len"),
    ("con dao", "con dao"),
    ("kia đó", "kia đó"),
    # Fixed English/loanword pronunciations.
    ("gout", "gút"),
    ("coin", "cô-in"),
    ("touch", "tớt"),
    ("sup", "xúp"),
    ("okay", "ô-kê"),
    ("ok", "ô-kê"),
    ("hello", "hê-lô"),
    # Isolated uppercase single letters are spelled out, Vietnamese-style.
    # (The pipeline turns hyphens into spaces; syllables stay correct.)
    ("R", "e rờ"),
    ("X quang", "ích quang"),
    ("Lớp B", "lớp bê"),
    # Untouched behavior: real Vietnamese, numbers, dates, English loans.
    ("Ngọc Huyền đọc truyện", "ngọc huyền đọc truyện"),
    ("rõ ràng", "rõ ràng"),
    ("đang ăn", "đang ăn"),
    ("25", "hai mươi lăm"),
    ("livestream", "lai-trim"),
    ("CEO", "xê ê o"),
]

failures = 0
for text, expected in CASES:
    actual = normalize(normalizer, text)
    status = "OK  " if actual == expected else "FAIL"
    if actual != expected:
        failures += 1
    print(f"{status} {text!r} -> {actual!r} (muốn {expected!r})")

if failures:
    raise SystemExit(f"{failures} ca phát âm sai")
print(f"Xong: {len(CASES)} ca đều đúng.")

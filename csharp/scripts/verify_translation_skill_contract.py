#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import re
import zipfile
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[2]
ARCHIVE = ROOT / "internal" / "translation" / "dich-trung-tu-tien.zip"
SERVICE = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Editor" / "LocalSubtitleTranslationService.cs"
SKILL_LOADER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Editor" / "TranslationSkillBundle.cs"
APP_PROJECT = ROOT / "csharp" / "src" / "BiliSubStudio.App" / "BiliSubStudio.App.csproj"

EXPECTED_ARCHIVE_SHA = "2969340edd47d3d860fc2bd7b4e0211723d5b8cad6a670d44dac707243e18213"
EXPECTED_MODEL_SHA = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785"
EXPECTED_RUNTIME_SHA = "68e15a0a0d07df55a695ec4d81465cf57400431d54ae19fadcb51dc919724042"
REQUIRED = {
    "dich-trung-tu-tien/SKILL.md",
    "dich-trung-tu-tien/references/character-names.md",
    "dich-trung-tu-tien/references/dialogue-voice.md",
    "dich-trung-tu-tien/references/forms-of-address.md",
    "dich-trung-tu-tien/references/research-audit.md",
    "dich-trung-tu-tien/references/tu-tien-glossary.md",
    "dich-trung-tu-tien/references/world-systems.md",
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL: " + message)


def main() -> int:
    require(ARCHIVE.is_file(), "missing integrated translation skill ZIP")
    archive_sha = hashlib.sha256(ARCHIVE.read_bytes()).hexdigest()
    require(archive_sha == EXPECTED_ARCHIVE_SHA, "integrated translation skill SHA-256 drift")
    with zipfile.ZipFile(ARCHIVE) as bundle:
        names = {info.filename.replace("\\", "/") for info in bundle.infolist()}
        require(len(names) <= 32, "translation skill ZIP has too many entries")
        require(REQUIRED <= names, "translation skill ZIP is missing required instructions/references")
        require(sum(info.file_size for info in bundle.infolist()) <= 8 * 1024 * 1024, "translation skill expands past safety limit")
        for name in names:
            path = PurePosixPath(name)
            require(not path.is_absolute() and ".." not in path.parts and ":" not in name, "translation skill contains unsafe path")

    service = SERVICE.read_text(encoding="utf-8")
    loader = SKILL_LOADER.read_text(encoding="utf-8")
    project = APP_PROJECT.read_text(encoding="utf-8")
    require(EXPECTED_ARCHIVE_SHA in loader, "runtime skill SHA pin drift")
    require(EXPECTED_MODEL_SHA in service and "5_027_783_488" in service, "Qwen model manifest/hash pin drift")
    require(EXPECTED_RUNTIME_SHA in service and "34_937_857" in service, "llama.cpp runtime manifest/hash pin drift")
    require("resolve/7c41481f57cb95916b40956ab2f0b139b296d974/" in service, "Qwen download is not commit-pinned")
    require(re.search(r'Link="Assets\\Translation\\dich-trung-tu-tien\.zip"', project) is not None, "published app does not package translation skill")
    print("PASS: pinned local translation model/runtime and exact translation skill bundle")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

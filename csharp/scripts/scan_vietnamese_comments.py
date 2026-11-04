#!/usr/bin/env python3
"""Audit-only: list comment-bearing files/lines for Phase F translation scope."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VIET = re.compile(r"[ăâđêôơưáàảãạấầẩẫậắằẳẵặéèẻẽẹếềểễệíìỉĩịóòỏõọốồổỗộớờởỡợúùủũụứừửữựýỳỷỹỵ]", re.IGNORECASE)
INCLUDED = {".cs": "csharp", ".py": "python", ".xaml": "xaml", ".ps1": "powershell"}
EXCLUDE_DIRS = {".git", ".vs", "bin", "obj", "artifacts", "publish", "publish-backup", "docs", "fixtures"}
EXCLUDE_FILES = {"AGENTS.md"}

rows: list[str] = []
for path in sorted(ROOT.rglob("*")):
    if not path.is_file() or path.suffix not in INCLUDED:
        continue
    relative = path.relative_to(ROOT)
    if any(part in EXCLUDE_DIRS for part in relative.parts):
        continue
    if path.name in EXCLUDE_FILES:
        continue
    text = path.read_text(encoding="utf-8", errors="replace")
    hits: list[tuple[int, str]] = []
    if path.suffix == ".cs":
        for index, line in enumerate(text.splitlines(), start=1):
            stripped = line.strip()
            if ("//" in stripped or '"""' in stripped) and VIET.search(stripped):
                hits.append((index, stripped))
    elif path.suffix == ".py":
        for index, line in enumerate(text.splitlines(), start=1):
            stripped = line.strip()
            if stripped.startswith("#") and VIET.search(stripped):
                hits.append((index, stripped))
    elif path.suffix == ".xaml":
        for index, line in enumerate(text.splitlines(), start=1):
            stripped = line.strip()
            if stripped.startswith("<!--") and stripped.endswith("-->") and VIET.search(stripped):
                hits.append((index, stripped))
            elif stripped.startswith("<!--") and VIET.search(stripped):
                hits.append((index, stripped))
    elif path.suffix == ".ps1":
        for index, line in enumerate(text.splitlines(), start=1):
            stripped = line.strip()
            if stripped.startswith("#") and VIET.search(stripped):
                hits.append((index, stripped))
    if hits:
        rel = relative.as_posix()
        rows.append(f"{rel}: {len(hits)}")
        for index, line in hits:
            rows.append(f"  {index}: {line[:220]}")

print("\n".join(rows))
print(f"FILES={len([row for row in rows if not row.startswith('  ')])}  HITS~={sum(int(row.rsplit(' ', 1)[1]) for row in rows if not row.startswith('  '))}")

#!/usr/bin/env python3
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(text: str, needle: str, message: str) -> None:
    if needle not in text:
        raise AssertionError(message)


def forbid(text: str, needle: str, message: str) -> None:
    if needle in text:
        raise AssertionError(message)


source = STORE.read_text(encoding="utf-8")

require(source, "catch (ProjectCorruptArchiveException) { throw; }",
        "PROJECT-07 must never reinterpret a quarantine failure as ordinary corruption.")
require(source, "catch (Exception error) when (IsProjectCorruption(error))",
        "PROJECT-07 must recover only from explicit corruption classes.")
require(source, "error is JsonException",
        "Malformed JSON must be classified as project corruption.")
require(source, "or InvalidDataException",
        "Invalid persisted project data must be classified as corruption.")
require(source, "or ArgumentException",
        "Malformed persisted paths/arguments must be classified as corruption.")
forbid(source, "error is IOException",
       "Transient filesystem IO must not be classified as project corruption.")
forbid(source, "error is UnauthorizedAccessException",
       "Access failures must not be classified as project corruption.")
forbid(source, "private static void Quarantine(",
       "The old best-effort quarantine that swallowed failures must not return.")

sidecar = "ArchiveCorruptState(ImageSidecarPath(id));"
project = "ArchiveCorruptState(projectPath);"
require(source, sidecar, "Corrupt project recovery must isolate its image sidecar.")
require(source, project, "Corrupt project JSON must be isolated before fresh recovery.")
if source.index(sidecar) > source.index(project):
    raise AssertionError("PROJECT-07 must archive the sidecar before moving the corrupt project.")
require(source, 'path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N")',
        "Corrupt archive names must be collision-resistant.")
require(source, "File.Move(path, archive, overwrite: false);",
        "Corrupt recovery must first attempt an atomic move.")
require(source, "File.Copy(path, archive, overwrite: false);",
        "Corrupt recovery must have a copy/delete fallback.")
require(source, "File.Delete(path);",
        "Copy fallback must remove the active corrupt file before fresh recovery.")
require(source, "throw new ProjectCorruptArchiveException(",
        "Failure to isolate corrupt state must fail closed.")
require(source, "|| loaded.Source is null)",
        "A structurally missing source fingerprint must be corruption, not a source-change migration.")


@dataclass
class Fixture:
    project_exists: bool = True
    sidecar_exists: bool = True
    project_archived: bool = False
    sidecar_archived: bool = False
    fresh_created: bool = False


class TransientIo(Exception):
    pass


class Corrupt(Exception):
    pass


class ArchiveFailure(Exception):
    pass


def recover(f: Fixture, error: Exception, fail_sidecar: bool = False, fail_project: bool = False) -> Fixture:
    if isinstance(error, TransientIo):
        raise error
    if not isinstance(error, Corrupt):
        raise error

    if f.sidecar_exists:
        if fail_sidecar:
            raise ArchiveFailure("sidecar")
        f.sidecar_exists = False
        f.sidecar_archived = True

    if f.project_exists:
        if fail_project:
            raise ArchiveFailure("project")
        f.project_exists = False
        f.project_archived = True

    f.fresh_created = True
    return f


good = recover(Fixture(), Corrupt())
assert good.project_archived and good.sidecar_archived and good.fresh_created
assert not good.project_exists and not good.sidecar_exists

no_sidecar = recover(Fixture(sidecar_exists=False), Corrupt())
assert no_sidecar.project_archived and not no_sidecar.sidecar_archived and no_sidecar.fresh_created

io = Fixture()
try:
    recover(io, TransientIo())
except TransientIo:
    pass
else:
    raise AssertionError("Transient IO must propagate.")
assert io.project_exists and io.sidecar_exists and not io.fresh_created

sidecar_fail = Fixture()
try:
    recover(sidecar_fail, Corrupt(), fail_sidecar=True)
except ArchiveFailure:
    pass
else:
    raise AssertionError("Sidecar quarantine failure must propagate.")
assert sidecar_fail.project_exists and sidecar_fail.sidecar_exists and not sidecar_fail.fresh_created

project_fail = Fixture()
try:
    recover(project_fail, Corrupt(), fail_project=True)
except ArchiveFailure:
    pass
else:
    raise AssertionError("Project quarantine failure must propagate.")
assert project_fail.project_exists and project_fail.sidecar_archived and not project_fail.fresh_created

print("PROJECT-07 corrupt project recovery contract: PASS")

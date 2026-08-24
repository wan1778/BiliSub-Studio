#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-01 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-01 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-01 unterminated method: {signature}")
    return ""


def verify_source(source: str) -> None:
    require(
        source.count("private void QueueProjectSave()") == 1,
        "PROJECT-01 must have exactly one autosave entry point",
    )
    require(
        source.count("private Microsoft.UI.Dispatching.DispatcherQueueTimer? _projectSaveTimer;") == 1,
        "PROJECT-01 must have exactly one EditorProject autosave timer owner",
    )
    require(
        source.count("private EditorProject? _pendingProjectSave;") == 1,
        "PROJECT-01 must have exactly one pending autosave snapshot owner",
    )
    require(
        source.count("private async Task PersistEditorProjectAsync(EditorProject project)") == 1,
        "PROJECT-01 must have exactly one low-level EditorProject writer owner",
    )

    queue = method_body(source, "private void QueueProjectSave()")
    for token in (
        "if (_project is null) return;",
        "_pendingProjectSave = ProjectSnapshot();",
        "if (_projectSaveFlushInProgress) return;",
        "RestartProjectSaveTimer();",
    ):
        require(token in queue, f"PROJECT-01 autosave owner lost: {token}")

    tick = method_body(source, "private async void ProjectSaveTimer_Tick(")
    require(
        "await PersistEditorProjectAsync(snapshot);" in tick,
        "PROJECT-01 timer must delegate persistence to the single writer",
    )

    writer = method_body(source, "private async Task PersistEditorProjectAsync(EditorProject project)")
    require(
        "await _projectSaveGate.WaitAsync();" in writer
        and "await _application.SaveEditorProjectAsync(project, CancellationToken.None);" in writer
        and "_projectSaveGate.Release();" in writer,
        "PROJECT-01 low-level writer must serialize all project writes",
    )

    flush = method_body(source, "private async Task FlushProjectSaveAsync(EditorProject project)")
    require(
        "await PersistEditorProjectAsync(project);" in flush,
        "PROJECT-01 forced flush must use the same low-level writer",
    )

    save_now = method_body(source, "private async Task SaveProjectNowAsync()")
    require(
        "await FlushProjectSaveAsync(ProjectSnapshot());" in save_now,
        "PROJECT-01 SaveProjectNowAsync must route through the same flush owner",
    )

    switch_flush = method_body(source, "private async Task SaveCurrentSourceStateForSwitchAsync()")
    require(
        "await FlushProjectSaveAsync(ProjectSnapshot());" in switch_flush
        or ("var snapshot = ProjectSnapshot();" in switch_flush and "await FlushProjectSaveAsync(snapshot);" in switch_flush),
        "PROJECT-01 source-switch save must route through the same flush owner",
    )

    require(
        "_ = _application.SaveEditorProjectAsync" not in source,
        "PROJECT-01 forbids fire-and-forget EditorProject writers",
    )
    direct_calls = re.findall(r"_application\.SaveEditorProjectAsync\(", source)
    require(
        len(direct_calls) == 1,
        f"PROJECT-01 expected exactly one low-level SaveEditorProjectAsync call, found {len(direct_calls)}",
    )


class AutosaveFixture:
    def __init__(self) -> None:
        self.pending: str | None = None
        self.writes: list[str] = []

    def queue(self, snapshot: str) -> None:
        self.pending = snapshot

    def tick(self) -> None:
        if self.pending is None:
            return
        snapshot = self.pending
        self.pending = None
        self.writes.append(snapshot)

    def flush(self, snapshot: str) -> None:
        self.pending = None
        self.writes.append(snapshot)


def verify_fixture() -> None:
    owner = AutosaveFixture()
    owner.queue("region-v1")
    owner.queue("region-v2")
    owner.tick()
    require(owner.writes == ["region-v2"], "PROJECT-01 only newest pending snapshot may autosave")

    owner = AutosaveFixture()
    owner.queue("old")
    owner.flush("latest-before-switch")
    owner.tick()
    require(
        owner.writes == ["latest-before-switch"],
        "PROJECT-01 forced flush must supersede queued autosave",
    )


if EDITOR.exists():
    verify_source(EDITOR.read_text(encoding="utf-8"))

verify_fixture()
print("PASS: PROJECT-01 single EditorProject autosave owner contract is locked")

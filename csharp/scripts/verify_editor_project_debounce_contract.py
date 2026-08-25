#!/usr/bin/env python3
from __future__ import annotations

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
    require(start >= 0, f"PROJECT-02 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-02 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-02 unterminated method: {signature}")
    return ""


def verify_source(source: str) -> None:
    require(
        "_saveCancellation" not in source,
        "PROJECT-02 EditorProject debounce must not allocate/rotate CancellationTokenSource instances",
    )
    require(
        "SaveProjectLaterAsync" not in source,
        "PROJECT-02 must not spawn a delayed Task per change",
    )
    require(
        source.count("private Microsoft.UI.Dispatching.DispatcherQueueTimer? _projectSaveTimer;") == 1,
        "PROJECT-02 requires exactly one reusable DispatcherQueueTimer",
    )
    require(
        source.count("private readonly SemaphoreSlim _projectSaveGate = new(1, 1);") == 1,
        "PROJECT-02 requires one writer serialization gate",
    )
    require(
        source.count("private EditorProject? _pendingProjectSave;") == 1,
        "PROJECT-02 requires one coalesced pending snapshot",
    )

    ensure = method_body(source, "private Microsoft.UI.Dispatching.DispatcherQueueTimer EnsureProjectSaveTimer()")
    for token in (
        "DispatcherQueue.CreateTimer()",
        "timer.Interval = TimeSpan.FromMilliseconds(450);",
        "timer.IsRepeating = false;",
        "timer.Tick += ProjectSaveTimer_Tick;",
    ):
        require(token in ensure, f"PROJECT-02 timer setup lost: {token}")
    require(source.count("timer.Tick += ProjectSaveTimer_Tick;") == 1,
            "PROJECT-02/CLEAN-02 project timer Tick must bind once for the persistent EditorPage")
    require("_projectSaveTimer.Tick -= ProjectSaveTimer_Tick;" not in source,
            "PROJECT-02/CLEAN-02 project timer must not detach/rebind across tab lifecycle")

    queue = method_body(source, "private void QueueProjectSave()")
    require(
        "_pendingProjectSave = ProjectSnapshot();" in queue,
        "PROJECT-02 queue must coalesce to one latest snapshot",
    )
    require(
        "RestartProjectSaveTimer();" in queue,
        "PROJECT-02 each edit must restart the same debounce timer",
    )
    require(
        "new CancellationTokenSource" not in queue and "Task.Delay" not in queue,
        "PROJECT-02 QueueProjectSave must not create CTS/tasks per change",
    )

    restart = method_body(source, "private void RestartProjectSaveTimer()")
    require(
        "timer.Stop();" in restart and "timer.Start();" in restart,
        "PROJECT-02 debounce must restart the same non-repeating timer",
    )

    tick = method_body(source, "private async void ProjectSaveTimer_Tick(")
    require(
        "var snapshot = _pendingProjectSave;" in tick
        and "_pendingProjectSave = null;" in tick
        and "await PersistEditorProjectAsync(snapshot);" in tick,
        "PROJECT-02 timer tick must consume one coalesced snapshot through the writer gate",
    )
    require(
        "if (_projectSaveFlushInProgress) return;" in tick,
        "PROJECT-02 autosave must yield while a forced flush owns persistence",
    )

    writer = method_body(source, "private async Task PersistEditorProjectAsync(EditorProject project)")
    wait = writer.find("await _projectSaveGate.WaitAsync();")
    save = writer.find("_application.SaveEditorProjectAsync(project, CancellationToken.None)")
    release = writer.find("_projectSaveGate.Release();")
    require(
        0 <= wait < save < release,
        "PROJECT-02 all physical project writes must be serialized by one gate",
    )

    flush = method_body(source, "private async Task FlushProjectSaveAsync(EditorProject project)")
    require(
        "_projectSaveFlushInProgress = true;" in flush
        and "StopProjectSaveTimer();" in flush
        and "_pendingProjectSave = null;" in flush
        and "await PersistEditorProjectAsync(project);" in flush
        and "_projectSaveFlushInProgress = false;" in flush,
        "PROJECT-02 forced flush must suppress debounce and use the same writer",
    )
    require(
        "if (_pendingProjectSave is not null) RestartProjectSaveTimer();" in flush,
        "PROJECT-02 edits arriving during flush must be debounced after the flush",
    )

    unload = method_body(source, "private async void EditorPage_Unloaded(")
    require(
        "StopProjectSaveTimer();" in unload
        and "await SaveProjectNowAsync();" in unload
        and "CleanupProjectAutosave();" in unload,
        "PROJECT-02 unload must stop timer, flush once, then clear pending autosave state",
    )

    cleanup = method_body(source, "private void CleanupProjectAutosave()")
    require(
        "StopProjectSaveTimer();" in cleanup and "_pendingProjectSave = null;" in cleanup,
        "PROJECT-02 unload cleanup must stop debounce and clear pending snapshot",
    )
    require(
        "Tick -=" not in cleanup and "_projectSaveTimer = null;" not in cleanup,
        "PROJECT-02/CLEAN-02 persistent autosave timer must retain its one Tick binding across tab reopen",
    )


class DebounceFixture:
    def __init__(self) -> None:
        self.pending: str | None = None
        self.timer_restarts = 0
        self.timer_bindings = 1
        self.timer_running = False
        self.flush = False
        self.writes: list[str] = []

    def queue(self, snapshot: str) -> None:
        self.pending = snapshot
        if not self.flush:
            self.timer_restarts += 1
            self.timer_running = True

    def tick(self) -> None:
        self.timer_running = False
        if self.flush or self.pending is None:
            return
        snapshot = self.pending
        self.pending = None
        self.writes.append(snapshot)

    def forced_flush(self, snapshot: str, arriving_during_flush: str | None = None) -> None:
        self.flush = True
        self.timer_running = False
        self.pending = None
        self.writes.append(snapshot)
        if arriving_during_flush is not None:
            self.queue(arriving_during_flush)
        self.flush = False
        if self.pending is not None:
            self.timer_restarts += 1
            self.timer_running = True

    def unload(self) -> None:
        self.timer_running = False
        self.pending = None


def verify_fixture() -> None:
    owner = DebounceFixture()
    owner.queue("v1")
    owner.queue("v2")
    owner.queue("v3")
    owner.tick()
    require(owner.writes == ["v3"], "PROJECT-02 burst edits must coalesce to newest snapshot only")
    require(owner.timer_restarts == 3, "PROJECT-02 burst edits restart one timer instead of spawning workers")

    owner = DebounceFixture()
    owner.queue("old")
    owner.forced_flush("flush-current", arriving_during_flush="new-after-flush")
    require(owner.writes == ["flush-current"], "PROJECT-02 pending old autosave must not race forced flush")
    owner.tick()
    require(
        owner.writes == ["flush-current", "new-after-flush"],
        "PROJECT-02 edit arriving during forced flush must persist after flush, never before it",
    )

    owner = DebounceFixture()
    owner.queue("before-unload")
    owner.unload()
    owner.queue("after-reopen")
    owner.tick()
    require(owner.timer_bindings == 1,
            "PROJECT-02/CLEAN-02 tab reopen must reuse one timer handler binding")
    require(owner.writes == ["after-reopen"],
            "PROJECT-02/CLEAN-02 stopped retained timer must resume normal debounce after reopen")


if EDITOR.exists():
    verify_source(EDITOR.read_text(encoding="utf-8"))

verify_fixture()
print("PASS: PROJECT-02 clean single-timer debounce, retained Tick owner and serialized writer contract are locked")

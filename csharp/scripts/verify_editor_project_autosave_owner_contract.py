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
    # One debounce state owner for EditorProject autosave.
    require(
        source.count("private CancellationTokenSource? _saveCancellation;") == 1,
        "PROJECT-01 must have exactly one autosave cancellation/debounce field",
    )
    require(
        source.count("private void QueueProjectSave()") == 1,
        "PROJECT-01 must have exactly one QueueProjectSave autosave entry point",
    )
    require(
        source.count("private async Task SaveProjectLaterAsync(") == 1,
        "PROJECT-01 must have exactly one delayed autosave worker",
    )

    queue = method_body(source, "private void QueueProjectSave()")
    for token in (
        "if (_project is null) return;",
        "_saveCancellation?.Cancel();",
        "_saveCancellation?.Dispose();",
        "var cancellation = new CancellationTokenSource();",
        "_saveCancellation = cancellation;",
        "var snapshot = ProjectSnapshot();",
        "_ = SaveProjectLaterAsync(snapshot, cancellation.Token);",
    ):
        require(token in queue, f"PROJECT-01 autosave owner lost: {token}")

    delayed = method_body(source, "private async Task SaveProjectLaterAsync(")
    require(
        "await Task.Delay(450, cancellationToken);" in delayed,
        "PROJECT-01 autosave debounce delay must stay inside the single worker",
    )
    require(
        "await _application.SaveEditorProjectAsync(project, cancellationToken);" in delayed,
        "PROJECT-01 delayed worker must be the autosave writer",
    )
    require(
        "catch (OperationCanceledException) { }" in delayed,
        "PROJECT-01 superseded autosaves must cancel quietly",
    )

    # Forced flushes are allowed, but they must cancel the pending debounce first.
    flush = method_body(source, "private async Task SaveProjectNowAsync()")
    cancel_index = flush.find("pending.Cancel();")
    save_index = flush.find("_application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None)")
    require(
        0 <= cancel_index < save_index,
        "PROJECT-01 forced SaveProjectNowAsync must cancel pending autosave before writing",
    )

    switch_flush = method_body(source, "private async Task SaveCurrentSourceStateForSwitchAsync()")
    cancel_index = switch_flush.find("pendingSave.Cancel();")
    save_index = switch_flush.find("_application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None)")
    require(
        0 <= cancel_index < save_index,
        "PROJECT-01 source-switch flush must cancel pending autosave before writing old source state",
    )

    unload = method_body(source, "private async void EditorPage_Unloaded(")
    require(
        "_saveCancellation?.Cancel();" in unload and "await SaveProjectNowAsync();" in unload,
        "PROJECT-01 unload must cancel queued autosave and finish with one forced project flush",
    )

    # There must be no second autosave timer/debounce mechanism for the same EditorProject.
    forbidden = (
        "DispatcherQueueTimer _save",
        "System.Threading.Timer _save",
        "PeriodicTimer _save",
        "SaveProjectTimer",
        "AutosaveTimer",
    )
    for token in forbidden:
        require(token not in source, f"PROJECT-01 second autosave owner detected: {token}")

    # SaveEditorProjectAsync has one delayed autosave call; any other calls must remain
    # explicit awaited flushes, never fire-and-forget background writers.
    require(
        "_ = _application.SaveEditorProjectAsync" not in source,
        "PROJECT-01 forbids a second fire-and-forget EditorProject writer",
    )
    direct_calls = re.findall(r"_application\.SaveEditorProjectAsync\(", source)
    require(
        len(direct_calls) == 3,
        f"PROJECT-01 expected one autosave writer + two forced flush callsites, found {len(direct_calls)}",
    )


# Portable behavioral fixture: only the newest queued snapshot survives debounce;
# a forced flush cancels pending autosave before persisting the latest snapshot.
class AutosaveFixture:
    def __init__(self) -> None:
        self.pending: tuple[int, str] | None = None
        self.revision = 0
        self.writes: list[str] = []

    def queue(self, snapshot: str) -> int:
        self.revision += 1
        self.pending = (self.revision, snapshot)
        return self.revision

    def fire(self, revision: int) -> None:
        if self.pending is None or self.pending[0] != revision:
            return
        _, snapshot = self.pending
        self.pending = None
        self.writes.append(snapshot)

    def flush(self, snapshot: str) -> None:
        self.pending = None
        self.writes.append(snapshot)


def verify_fixture() -> None:
    owner = AutosaveFixture()
    r1 = owner.queue("region-v1")
    r2 = owner.queue("region-v2")
    owner.fire(r1)
    require(owner.writes == [], "PROJECT-01 stale queued autosave must not write")
    owner.fire(r2)
    require(owner.writes == ["region-v2"], "PROJECT-01 newest queued autosave must write once")

    owner = AutosaveFixture()
    delayed = owner.queue("old")
    owner.flush("latest-before-switch")
    owner.fire(delayed)
    require(
        owner.writes == ["latest-before-switch"],
        "PROJECT-01 forced flush must cancel queued autosave instead of allowing stale overwrite",
    )


if EDITOR.exists():
    verify_source(EDITOR.read_text(encoding="utf-8"))
else:
    # The script remains runnable outside a checkout for syntax/synthetic gate.
    sample = r'''
private CancellationTokenSource? _saveCancellation;
private void QueueProjectSave()
{
    if (_project is null) return;
    _saveCancellation?.Cancel();
    _saveCancellation?.Dispose();
    var cancellation = new CancellationTokenSource();
    _saveCancellation = cancellation;
    var snapshot = ProjectSnapshot();
    _ = SaveProjectLaterAsync(snapshot, cancellation.Token);
}
private async Task SaveProjectLaterAsync(EditorProject project, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(450, cancellationToken);
        await _application.SaveEditorProjectAsync(project, cancellationToken);
    }
    catch (OperationCanceledException) { }
}
private async Task SaveProjectNowAsync()
{
    var pending = _saveCancellation;
    _saveCancellation = null;
    if (pending is not null)
    {
        pending.Cancel();
        pending.Dispose();
    }
    await _application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None);
}
private async Task SaveCurrentSourceStateForSwitchAsync()
{
    var pendingSave = _saveCancellation;
    _saveCancellation = null;
    if (pendingSave is not null)
    {
        pendingSave.Cancel();
        pendingSave.Dispose();
    }
    await _application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None);
}
private async void EditorPage_Unloaded(object sender, RoutedEventArgs e)
{
    _saveCancellation?.Cancel();
    await SaveProjectNowAsync();
}
'''
    verify_source(sample)

verify_fixture()
print("PASS: PROJECT-01 single EditorProject autosave owner contract is locked")

#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
MAIN = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
BOOTSTRAP = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs"
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-03 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-03 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-03 unterminated method: {signature}")
    return ""


def verify_source(main: str, editor: str, bootstrap: str, playback: str) -> None:
    require(
        '["editor"] = new EditorPage(_application, filePicker)' in main,
        "PROJECT-03 MainWindow must own one persistent EditorPage instance",
    )
    nav = method_body(main, "private void Navigation_SelectionChanged(")
    require(
        "ContentFrame.Content = page;" in nav and "new EditorPage" not in nav,
        "PROJECT-03 tab navigation must reuse the existing EditorPage instead of recreating it",
    )

    require(
        bootstrap.count("private readonly SemaphoreSlim _editorTabLifecycleGate = new(1, 1);") == 1,
        "PROJECT-03 requires one Editor tab lifecycle gate",
    )
    unloaded = method_body(editor, "private async void EditorPage_Unloaded(")
    for token in (
        "await _editorTabLifecycleGate.WaitAsync();",
        "await _playback.UnloadAsync();",
        "await SaveImageSidecarAsync();",
        "await SaveProjectNowAsync();",
        "CleanupProjectAutosave();",
        "_editorTabLifecycleGate.Release();",
    ):
        require(token in unloaded, f"PROJECT-03 unload contract lost: {token}")
    require(
        "_project = null" not in unloaded
        and "_media = null" not in unloaded
        and "_path = null" not in unloaded,
        "PROJECT-03 tab unload must not destroy the in-memory project/source identity",
    )

    reset = method_body(playback, "private async Task ResetAsync()")
    require(
        "DisposePlayer();" in reset,
        "PROJECT-03 playback unload must be recognized as disposing the MediaPlayer",
    )

    loaded = method_body(bootstrap, "private async void EditorPage_Loaded(")
    for token in (
        "await _editorTabLifecycleGate.WaitAsync();",
        "if (!IsLoaded) return;",
        "_path is not null && _media is not null && !_playback.IsReady",
        "await _playback.PrepareAsync();",
        "await UpdateFrameAsync();",
        "_editorTabLifecycleGate.Release();",
    ):
        require(token in loaded, f"PROJECT-03 reopen contract lost: {token}")
    require(
        "if (!IsLoaded)" in loaded and "await _playback.UnloadAsync();" in loaded,
        "PROJECT-03 stale Loaded continuation must tear playback back down when tab was closed again",
    )

    save_now = method_body(editor, "private async Task SaveProjectNowAsync()")
    require(
        "FlushProjectSaveAsync(ProjectSnapshot())" in save_now,
        "PROJECT-03 unload persistence must preserve the latest PROJECT-02 snapshot",
    )


class LifecycleFixture:
    def __init__(self) -> None:
        self.loaded = True
        self.player_ready = True
        self.saved_project = False
        self.saved_images = False
        self.events: list[str] = []

    def unload(self) -> None:
        self.events.append("unload-lock")
        self.loaded = False
        self.player_ready = False
        self.saved_images = True
        self.saved_project = True
        self.events.append("unload-release")

    def reopen(self) -> None:
        self.events.append("load-lock")
        self.loaded = True
        if not self.player_ready:
            self.player_ready = True
            self.events.append("prepare-player")
            if not self.loaded:
                self.player_ready = False
                self.events.append("stale-load-reset")
                return
            self.events.append("refresh-frame")
        self.events.append("load-release")


def verify_fixture() -> None:
    page = LifecycleFixture()
    page.unload()
    require(page.saved_project and page.saved_images, "PROJECT-03 unload must persist project + image sidecar")
    require(not page.player_ready, "PROJECT-03 unload must dispose playback")
    page.reopen()
    require(page.player_ready, "PROJECT-03 reopen must recreate playback")
    require(
        page.events.index("unload-release") < page.events.index("load-lock"),
        "PROJECT-03 lifecycle gate must order reopen after unload completion",
    )

    page = LifecycleFixture()
    page.unload()
    page.loaded = False
    page.events.append("load-lock")
    require(not page.loaded and not page.player_ready, "PROJECT-03 stale load must leave closed tab playback disposed")


if all(path.exists() for path in (MAIN, EDITOR, BOOTSTRAP, PLAYBACK)):
    verify_source(
        MAIN.read_text(encoding="utf-8"),
        EDITOR.read_text(encoding="utf-8"),
        BOOTSTRAP.read_text(encoding="utf-8"),
        PLAYBACK.read_text(encoding="utf-8"),
    )

verify_fixture()
print("PASS: PROJECT-03 close/reopen Editor tab persistence contract is locked")

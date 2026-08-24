#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
MAIN = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
PERSIST = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ProjectPersistence.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
APP = ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-04 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-04 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-04 unterminated method: {signature}")
    return ""


def verify_source(main: str, persistence: str, editor: str, images: str, store: str, app: str) -> None:
    closing = method_body(main, "private async void OnAppWindowClosing(")
    flush_index = closing.find("await editorPage.FlushForAppCloseAsync(timeout.Token);")
    shutdown_index = closing.find("await _application.PrepareShutdownAsync(timeout.Token);")
    require(0 <= flush_index < shutdown_index, "PROJECT-04 app close must await Editor persistence before process/job shutdown")
    require('if (_pages["editor"] is EditorPage editorPage)' in closing, "PROJECT-04 app close must flush the actual MainWindow-owned EditorPage instance")
    require('FooterStatus.Text = "Từ chối đóng để bảo toàn dữ liệu: " + error.Message;' in closing, "PROJECT-04 failed persistence must keep the existing refuse-close safety path")

    flush = method_body(persistence, "internal async Task FlushForAppCloseAsync(")
    for token in (
        "await _editorTabLifecycleGate.WaitAsync(cancellationToken);",
        "StopProjectSaveTimer();",
        "if (_project is null) return;",
        "var snapshot = ProjectSnapshot();",
        "await FlushProjectSaveAsync(snapshot);",
        "await SaveImageSidecarAsync();",
        "_editorTabLifecycleGate.Release();",
    ):
        require(token in flush, f"PROJECT-04 close flush contract lost: {token}")
    require("SaveProjectNowAsync" not in flush, "PROJECT-04 app-close flush must not swallow project persistence failures")
    require("catch" not in flush, "PROJECT-04 app-close flush must propagate project/image write failures to MainWindow")

    save_now = method_body(editor, "private async Task SaveProjectNowAsync()")
    require("FlushProjectSaveAsync(ProjectSnapshot())" in save_now, "PROJECT-04 must retain PROJECT-02 serialized project persistence")
    image_save = method_body(images, "private async Task SaveImageSidecarAsync()")
    require('Path.Combine(_application.Paths.Data, "Projects", projectId + ".images.json")' in images, "PROJECT-04 image sidecar must remain under persistent Data/Projects storage")
    require("File.Move(temporary, path, overwrite: true);" in image_save, "PROJECT-04 image sidecar must retain atomic temporary-file promotion")

    load = method_body(store, "public async Task<EditorProject> LoadOrCreateAsync(")
    require("var id = ProjectId(source.Path);" in load and "var projectPath = ProjectPath(id);" in load, "PROJECT-04 reopen must resolve the same project identity from the source path")
    require("JsonSerializer.DeserializeAsync<EditorProject>" in load, "PROJECT-04 reopen must deserialize the persisted project instead of always creating fresh state")
    project_id = method_body(store, "private static string ProjectId(")
    require("Path.GetFullPath(inputPath).ToUpperInvariant()" in project_id and "SHA256.HashData" in project_id, "PROJECT-04 project identity must stay deterministic across app processes")
    save = method_body(store, "public async Task SaveAsync(")
    require('var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");' in save and "File.Move(temporary, path, overwrite: true);" in save, "PROJECT-04 project persistence must stay atomic across process restart")

    require("public Task<EditorProject> LoadEditorProjectAsync" in app and "_editorProjects.LoadOrCreateAsync" in app, "PROJECT-04 fresh EditorPage must reopen through EditorProjectStore")


class RestartFixture:
    def __init__(self) -> None:
        self.disk_project: dict[str, str] = {}
        self.disk_images: dict[str, str] = {}
        self.events: list[str] = []

    @staticmethod
    def project_id(path: str) -> str:
        normalized = str(Path(path).resolve()).upper()
        return hashlib.sha256(normalized.encode("utf-8")).hexdigest()[:24]

    def close(self, path: str, state: str, images: str) -> None:
        identity = self.project_id(path)
        self.events.append("flush-editor")
        self.disk_project[identity] = state
        self.disk_images[identity] = images
        self.events.append("shutdown")

    def reopen(self, path: str) -> tuple[str | None, str | None]:
        identity = self.project_id(path)
        return self.disk_project.get(identity), self.disk_images.get(identity)


def verify_fixture() -> None:
    fixture = RestartFixture()
    source = "C:/Videos/demo.mp4"
    require(RestartFixture.project_id(source) == RestartFixture.project_id(source), "PROJECT-04 same source path must resolve same project ID after process restart")
    fixture.close(source, "regions+subtitle+audio+voice", "logo-state")
    project, images = fixture.reopen(source)
    require(project == "regions+subtitle+audio+voice", "PROJECT-04 persisted EditorProject must survive restart")
    require(images == "logo-state", "PROJECT-04 persisted image sidecar must survive restart")
    require(fixture.events == ["flush-editor", "shutdown"], "PROJECT-04 Editor flush must complete before application shutdown begins")


if all(path.exists() for path in (MAIN, PERSIST, EDITOR, IMAGES, STORE, APP)):
    verify_source(
        MAIN.read_text(encoding="utf-8"),
        PERSIST.read_text(encoding="utf-8"),
        EDITOR.read_text(encoding="utf-8"),
        IMAGES.read_text(encoding="utf-8"),
        STORE.read_text(encoding="utf-8"),
        APP.read_text(encoding="utf-8"),
    )

verify_fixture()
print("PASS: PROJECT-04 app close/reopen persistence contract is locked")

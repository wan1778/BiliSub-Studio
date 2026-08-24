#!/usr/bin/env python3
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sys

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
IMAGES = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Images.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-05 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-05 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-05 unterminated method: {signature}")
    return ""


def verify_core(core: str) -> None:
    matches = method_body(core, "public static bool SourceFingerprintMatchesCurrent(")
    require("SourceFingerprintChanged(previous, Fingerprint(inputPath, width, height, duration))" in matches,
            "PROJECT-05 public fingerprint check must reuse the store fingerprint contract")
    require("catch" in matches and "return false;" in matches,
            "PROJECT-05 missing/unreadable source must never be treated as matching")

    load = method_body(core, "public async Task<EditorProject> LoadOrCreateAsync(")
    change = load.find("if (SourceFingerprintChanged(loaded.Source, source))")
    sidecar = load.find("ArchiveSourceChanged(ImageSidecarPath(id));", change)
    project = load.find("ArchiveSourceChanged(projectPath);", change)
    fresh = load.find("CreateFreshProject(source, id, loaded.Name, loaded.FileName)", change)
    require(0 <= change < sidecar < project < fresh,
            "PROJECT-05 changed source must archive image sidecar then project before creating fresh state")
    require("catch (SourceChangeArchiveException) { throw; }" in load,
            "PROJECT-05 archive failure must propagate instead of silently restoring stale artifacts")

    save = method_body(core, "public async Task SaveAsync(")
    fingerprint = save.find("var normalizedSource = Fingerprint(")
    guard = save.find("if (SourceFingerprintChanged(project.Source, normalizedSource))")
    write = save.find("JsonSerializer.SerializeAsync")
    require(0 <= fingerprint < guard < write,
            "PROJECT-05 SaveAsync must reject a replaced source before writing any project state")
    require("Video nguồn đã thay đổi ngoài ứng dụng" in save,
            "PROJECT-05 save rejection must explain the external source replacement")

    archive = method_body(core, "private static void ArchiveSourceChanged(")
    for token in ("if (!File.Exists(path)) return;", "File.Move(path, archive", "File.Copy(path, archive", "File.Delete(path)", "throw new SourceChangeArchiveException"):
        require(token in archive, f"PROJECT-05 robust archive contract lost: {token}")
    require('private string ImageSidecarPath(string id) => Path.Combine(_directory, id + ".images.json")' in core,
            "PROJECT-05 Core must know the path-derived image sidecar when invalidating a replaced source")


def verify_editor(editor: str) -> None:
    open_video = method_body(editor, "private async Task OpenVideoAsync()")
    same = open_video.find("var sameSourcePath = EditorSourceSelection.IsSameSource(_path, candidatePath);")
    probe = open_video.find("candidateMedia = await _application.Media.ProbeAsync")
    changed = open_video.find("var currentSourceChanged =")
    noop = open_video.find("if (sameSourcePath && !currentSourceChanged)")
    require(0 <= same < probe < changed < noop,
            "PROJECT-05 same-path selection must probe and fingerprint-check before taking the no-op path")

    stop = open_video.find("StopProjectSaveTimer();", changed)
    clear = open_video.find("_pendingProjectSave = null;", changed)
    wait = open_video.find("await WaitForProjectSaveIdleAsync();", changed)
    archive = open_video.find("ArchiveImageSidecarForSourceChange(_project!.Id);", changed)
    load = open_video.find("candidateProject = await _application.LoadEditorProjectAsync", changed)
    require(0 <= stop < clear < wait < archive < load,
            "PROJECT-05 must quiesce stale autosave and image state before loading a replacement source")

    guarded_save = "if (!currentSourceChanged)\n            await SaveCurrentSourceStateForSwitchAsync();"
    require(guarded_save in open_video,
            "PROJECT-05 must never flush old edit state onto a source already replaced outside the app")
    require("project cũ đã được lưu trữ và state dẫn xuất đã reset" in open_video,
            "PROJECT-05 must surface replacement-source reset status")

    idle = method_body(editor, "private async Task WaitForProjectSaveIdleAsync()")
    require("await _projectSaveGate.WaitAsync();" in idle and "_projectSaveGate.Release();" in idle,
            "PROJECT-05 source replacement must serialize behind an already-running PROJECT-02 writer")

    require(
        "private bool CurrentSourceFingerprintMatches() =>" in editor
        and "EditorProjectStore.SourceFingerprintMatchesCurrent(" in editor,
        "PROJECT-05 Editor fingerprint owner must delegate to EditorProjectStore",
    )
    ensure = method_body(editor, "private void EnsureCurrentSourceFingerprint()")
    require("if (!CurrentSourceFingerprintMatches())" in ensure and "Preview/Export" in ensure,
            "PROJECT-05 stale source must block preview/export requests")

    request = method_body(editor, "private VideoEditRequest CurrentEditRequest(")
    require("EnsureCurrentSourceFingerprint();" in request,
            "PROJECT-05 processed Preview/base Export request must reject stale source state")


def verify_images(images: str) -> None:
    save = method_body(images, "private async Task SaveImageSidecarAsync()")
    require("if (!CurrentSourceFingerprintMatches()) return;" in save,
            "PROJECT-05 image sidecar must not be rewritten after source replacement")

    archive = method_body(images, "private void ArchiveImageSidecarForSourceChange(")
    for token in ("File.Move(path, archive", "File.Copy(path, archive", "File.Delete(path)", "if (File.Exists(path))", "_imageProjectId = null;", "_imageOverlays.Clear();", "_imageBitmaps.Clear();"):
        require(token in archive, f"PROJECT-05 live image reset contract lost: {token}")

    render = method_body(images, "private async Task RenderProjectAsync()")
    null_check = render.find("if (_path is null || _media is null || _project is null)")
    source_guard = render.find("EnsureCurrentSourceFingerprint();")
    subtitle = render.find("var subtitle = CompletedSubtitleBurn();")
    require(0 <= null_check < source_guard < subtitle,
            "PROJECT-05 final export, including Image-only, must reject a replaced source before composing")


@dataclass(frozen=True)
class Fingerprint:
    path: str
    size: int
    ticks: int
    width: int
    height: int
    duration: float


def same(a: Fingerprint, b: Fingerprint) -> bool:
    return (
        a.path.upper() == b.path.upper()
        and a.size == b.size
        and a.ticks == b.ticks
        and a.width == b.width
        and a.height == b.height
        and abs(a.duration - b.duration) <= 0.05
    )


def verify_fixture() -> None:
    old = Fingerprint("C:/video/demo.mp4", 1000, 10, 1920, 1080, 120.0)
    unchanged = Fingerprint("c:/VIDEO/demo.mp4", 1000, 10, 1920, 1080, 120.0)
    replaced = Fingerprint("C:/video/demo.mp4", 1400, 11, 1920, 1080, 120.0)
    replaced_dimensions = Fingerprint("C:/video/demo.mp4", 1000, 10, 1280, 720, 95.0)

    require(same(old, unchanged), "PROJECT-05 unchanged source must keep its project")
    require(not same(old, replaced), "PROJECT-05 size/mtime replacement must invalidate the project")
    require(not same(old, replaced_dimensions), "PROJECT-05 media-shape replacement must invalidate the project")

    events: list[str] = []
    stale_project = {"name": "Demo", "file": "custom.mp4", "regions": "old", "voice": "old"}
    stale_images = ["logo-old"]
    if not same(old, replaced):
        events += ["stop-debounce", "wait-writer", "archive-images", "archive-project"]
        fresh = {"name": stale_project["name"], "file": stale_project["file"], "regions": None, "voice": None}
        stale_images = []
    else:
        fresh = stale_project
    require(events == ["stop-debounce", "wait-writer", "archive-images", "archive-project"],
            "PROJECT-05 replacement reset ordering is wrong")
    require(fresh["name"] == "Demo" and fresh["file"] == "custom.mp4",
            "PROJECT-05 harmless project/output names should survive source replacement")
    require(fresh["regions"] is None and fresh["voice"] is None and stale_images == [],
            "PROJECT-05 source-derived regions/voice/images must not survive replacement")

    save_allowed = same(old, replaced)
    require(not save_allowed, "PROJECT-05 stale snapshot must not be stamped with the replacement fingerprint")


if all(path.exists() for path in (CORE, EDITOR, IMAGES)):
    verify_core(CORE.read_text(encoding="utf-8"))
    verify_editor(EDITOR.read_text(encoding="utf-8"))
    verify_images(IMAGES.read_text(encoding="utf-8"))

verify_fixture()
print("PASS: PROJECT-05 external source replacement contract is locked")

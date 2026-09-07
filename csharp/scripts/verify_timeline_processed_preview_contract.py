#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
CORE = ROOT / "csharp/src/BiliSubStudio.Core/Timeline"
CONTROLLER = CORE / "TimelinePreviewController.cs"
SERVICE = CORE / "TimelineProcessedPreviewService.cs"
TEST = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/TimelineProcessedPreviewContract.cs"
PROGRAM = ROOT / "csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs"
VERIFY = ROOT / "csharp/scripts/verify.ps1"
EDITOR_STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


controller = CONTROLLER.read_text(encoding="utf-8")
service = SERVICE.read_text(encoding="utf-8")
test = TEST.read_text(encoding="utf-8")
program = PROGRAM.read_text(encoding="utf-8")
verify = VERIFY.read_text(encoding="utf-8")
editor_store = EDITOR_STORE.read_text(encoding="utf-8")

for token in (
    "ITimelineProcessedPreviewGenerator",
    "checked(_revision + 1)",
    "previousCompletion",
    "RequestCancellation(previousCancellation)",
    "generated.Revision != revision",
    "ReferenceEquals(_currentCancellation, requestCancellation)",
    "generator.DeleteAsync(generated, CancellationToken.None)",
    "CancelCoreAsync()",
    "IAsyncDisposable",
):
    require(token in controller, f"G9 controller ownership token missing: {token}")

for token in (
    "TimelineProcessedPreviewRequest",
    "TimelineProcessedPreviewArtifact",
    "ITimelineProcessedPreviewRenderer",
    "TimelineRenderPlan Plan",
    "TimelineProjectStoreV2.TestRootFolderName",
    '"Preview"',
    '".rendering.mp4"',
    "Guid.NewGuid()",
    "new OwnedProcessGroup()",
    "processes.ActiveCount != 0",
    "File.Move(renderingPath, finalPath)",
    "finally",
    "processes.StopAsync()",
    "DeleteManagedFileAsync(renderingPath, CancellationToken.None)",
    "EnsureManagedPath",
    "CleanupCacheAsync",
):
    require(token in service, f"G9 proxy/process token missing: {token}")

for body, name in ((controller, "controller"), (service, "service")):
    for token in ("EditorPage", "EditorProjectStore", "VideoEditorService", "TimelineOneClipParity"):
        require(token not in body, f"G9 {name} coupled to classic owner: {token}")
require("ProcessRunner" not in controller and "File." not in controller and "Directory." not in controller,
        "G9 controller must keep media IO and processes behind the generator")
require("Timeline" not in editor_store,
        "classic schema-7 store must not reference Timeline")

for token in (
    "LatestRequestWaitsForCleanupAsync",
    "CancellationResistantStaleResultIsDeletedAsync",
    "FailedReplacementKeepsCurrentArtifactAsync",
    "ServiceOwnsAtomicV2ArtifactsAsync",
    "CancellationReapsOwnedProcessTreeAsync",
    '"--fixture-spawn-child"',
    "observedOwner.ActiveCount == 0",
    "outside-root file",
):
    require(token in test, f"G9 runtime contract missing: {token}")
require("TimelineProcessedPreviewContract.RunAsync" in program,
        "G9 runtime contract is not registered")
require("verify_timeline_processed_preview_contract.py" in verify,
        "official Windows verification does not run the G9 static gate")

print("PASS: G9 revisioned processed preview serializes cleanup, rejects stale results and reaps owned processes")

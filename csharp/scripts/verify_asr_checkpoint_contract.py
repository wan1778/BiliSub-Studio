#!/usr/bin/env python3
"""Source-only ASR publication contract. Not a file-lock/runtime PASS."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
writer = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/AsrCheckpointFile.cs").read_text(encoding="utf-8")
service = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs").read_text(encoding="utf-8")
hybrid = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.Hybrid.cs").read_text(encoding="utf-8")


def require(condition, message):
    if not condition:
        raise SystemExit("FAIL: " + message)


for marker in ("FileMode.CreateNew", "FileOptions.WriteThrough", "stream.Flush(flushToDisk: true)",
               'File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false)',
               "File.Move(temporary, path);", "[100, 200, 400, 800, 1000]",
               "attempt < RetryDelaysMilliseconds.Length", "IsRetryable(error)",
               "!IsProtectedTarget(path)", 'IsProtectedTarget(path + ".bak")',
               "Task.Delay(RetryDelaysMilliseconds[attempt], cancellationToken)", "HRESULT=0x",
               "prepared && File.Exists(temporary)"):
    require(marker in writer, "ASR checkpoint resilience missing " + marker)
require(writer.index("stream.Flush(flushToDisk: true)") < writer.index("prepared = true;")
        < writer.index("await PublishAsync("), "publish must follow close/flush of the full snapshot")
require("if (!prepared)" in writer and "File.Delete(temporary);" in writer,
        "only unfinished serialization may be cleaned; complete denied snapshots must survive")
for forbidden in ("File.Delete(path)", "File.Copy(", "FileMode.Truncate", "overwrite: true", "File.SetAttributes", "SetAccessControl"):
    require(forbidden not in writer, "unsafe checkpoint replacement/permission mutation: " + forbidden)
require("await AsrCheckpointFile.WriteAsync(path, checkpoint, _json, cancellationToken, warning)" in service,
        "ASR must use the new writer")
for owner in (service, hybrid):
    require("SaveCheckpointAsync(checkpointPath, checkpoint, CancellationToken.None, job.Warn)" in owner,
            "accepted segment/chunk must keep cleanup-aware durable save and warnings")
loader = service.split("private async Task<AsrCheckpoint> LoadCheckpointAsync", 1)[1].split("private static bool ValidCue", 1)[0]
for marker in ('new[] { path, path + ".bak" }', "FileShare.Read | FileShare.Delete", "loaded.Schema != CheckpointSchema",
               "loaded.Key != key", "loaded.ModelRevision != LocalAsrInstaller.ModelRevision", "ValidHybridCue",
               "catch (OperationCanceledException) { throw; }"):
    require(marker in loader, "backup loading lost validation/cancel rule " + marker)
require("EnumerateFiles" not in loader and "Directory.GetFiles" not in loader,
        "unpublished temporary files must not be silently loaded as checkpoints")
print("PASS: ASR checkpoint atomic replace/retry/recovery source contract (not a Windows field result)")

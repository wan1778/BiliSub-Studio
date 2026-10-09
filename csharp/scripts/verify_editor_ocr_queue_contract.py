#!/usr/bin/env python3
"""OCR scan queue contract (09/10): one Scan button does everything.

User model (must stay simple): set up a video, press Quet, it drops into the
queue below and processes sequentially. While it runs, picking another video,
setting it up and pressing Quet appends it to the queue. Failure skips to the
next video, session-only, scan mode only. No separate Add/Run/Stop buttons.
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml"
QUEUE = ROOT / "csharp/src/BiliSubStudio.Core/Ocr/OcrScanQueue.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


require(QUEUE.exists(), "QUEUE-01 Core OcrScanQueue.cs is missing")
queue = read(QUEUE)
page = read(PAGE)
xaml = read(XAML)

# Core state machine: FIFO, one scan at a time, paused resumes first,
# failure never blocks the queue, per-video request builder.
for token in (
    "public OcrQueueItem? TakeNext()",
    "public void MarkDone(Guid id, string? outputSrtPath, string message)",
    "public void MarkFailed(Guid id, string error)",
    "public void MarkCancelled(Guid id, string message)",
    "public void MarkPaused(Guid id)",
    "public int ClearFinished()",
    "public OcrScanRequest ToRequest()",
):
    require(token in queue, f"QUEUE-01 queue state machine lost: {token}")
require("Status == OcrQueueItemStatus.Paused" in queue,
        "QUEUE-01 TakeNext must resume paused items before new waiting items")
require("public int QueueNumber" in queue and "NumberedName" in queue,
        "QUEUE-01 items must carry auto-compacting 1-based numbers")

# One-button model: Scan enqueues the current setup and ensures the driver.
for token in (
    "private async Task RunQueueAsync()",
    "_ocrQueue.TakeNext()",
    "_ocrQueue.MarkFailed(item.Id,",
    "OcrQueueItem? queueItem = null",
    "Tiếp tục mục tạm dừng",
    "Đã cho vào hàng chờ",
    "Video này đã có trong hàng chờ rồi",
    "Video này đang quét trong hàng rồi.",
):
    require(token in page, f"QUEUE-01 one-button queue flow lost: {token}")

# No separate Add/Run/Stop buttons may exist (user: too complicated).
for token in (
    "QueueAddButton",
    "QueueRunButton",
    "QueueStopButton",
    "QueueAdd_Click",
    "QueueRun_Click",
    "QueueStop_Click",
):
    require(token not in page and token not in xaml,
            f"QUEUE-01 separate queue button came back: {token}")

# Setup-during-run: picking another video mid-queue must be allowed, and the
# export must follow the scanned item (not whatever is on screen now).
require("if (_jobId is not null && !_queueRunning)" in page,
        "QUEUE-01 PickVideo must stay available while the queue runs")
require("OcrQueueItem? queueItem = null, bool queueMode = false" in page
        and "queueMode: true" in page,
        "QUEUE-01 queue scans must skip the single-scan control lockdown")
require("PickVideoButton.IsEnabled = queueMode;" in page
        and "ScanButton.IsEnabled = queueMode;" in page,
        "QUEUE-01 setup controls must stay unlocked for queue scans")
require("private OcrScanRequest? _queueActiveRequest;" in page
        and "_activeRequest ?? _checkpointRequest ?? _queueActiveRequest" in page,
        "QUEUE-01 cancel must target the running queue item")
require("OcrScanRequest? exportRequest = null" in page
        and "exportRequest: request" in page,
        "QUEUE-01 auto-export must follow the scanned request")

# Scan loop must report its ending so the driver can advance or stop.
require("private enum QueueScanEnd" in page
        and "Task<(QueueScanEnd End, string? ExportedPath, string? Detail)> StartScanAsync(" in page,
        "QUEUE-01 StartScanAsync must return its ending for the queue driver")

# Queue UI: list + reorder/remove/clear only.
for token in (
    'x:Name="QueueClearButton"',
    'x:Name="QueueList"',
    'x:Name="QueueUpButton"',
    'x:Name="QueueDownButton"',
    'x:Name="QueueRemoveButton"',
    'x:Name="QueueCountText"',
    'x:Name="QueueStatusText"',
    "{Binding NumberedName}",
    "{Binding StatusText}",
    "{Binding SetupSummary}",
    "Value=\"{Binding Progress}\"",
):
    require(token in xaml, f"QUEUE-01 queue UI lost: {token}")

print("PASS: QUEUE-01 OCR scan queue contract")

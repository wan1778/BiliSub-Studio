#!/usr/bin/env python3
"""OCR scan queue contract (09/10): multiple videos scan sequentially.

Gates the queue feature the user asked for: each video keeps its own setup
(path + ROI + mode + device + lanes) captured at enqueue time, failure skips
to the next video, queue lives only in the app session, scan mode only.
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

# Page driver: enqueue from current single-video state, sequential runner,
# stop/pause/cancel semantics, per-item progress feed.
for token in (
    "private readonly OcrScanQueue _ocrQueue = new();",
    "private void SyncQueueView()",
    "private void QueueAdd_Click",
    "private async void QueueRun_Click",
    "private async Task RunQueueAsync()",
    "private void QueueStop_Click",
    "private void QueueClear_Click",
    "private void QueueRemove_Click",
    "private void QueueUp_Click",
    "private void QueueDown_Click",
    "_ocrQueue.TakeNext()",
    "_ocrQueue.MarkFailed(item.Id,",
    "OcrQueueItem? queueItem = null",
):
    require(token in page, f"QUEUE-01 page driver lost: {token}")

# Scan loop must report its ending so the driver can advance or stop.
require("private enum QueueScanEnd" in page
        and "Task<(QueueScanEnd End, string? ExportedPath, string? Detail)> StartScanAsync(" in page,
        "QUEUE-01 StartScanAsync must return its ending for the queue driver")

# Single-scan entry points must yield to a running queue (page state is shared).
for token in (
    "if (_jobId is not null || _queueRunning)",
    'if (_path is null || _media is null || _jobId is not null || _queueRunning) return;',
    'if (_queueRunning)\n        {\n            StatusText.Text = "Hàng chờ đang chạy;',
):
    require(token in page, f"QUEUE-01 single-scan guard lost: {token[:60]}")

# Queue UI exists and binds per-item state.
for token in (
    'x:Name="QueueAddButton"',
    'x:Name="QueueRunButton"',
    'x:Name="QueueStopButton"',
    'x:Name="QueueClearButton"',
    'x:Name="QueueList"',
    'x:Name="QueueUpButton"',
    'x:Name="QueueDownButton"',
    'x:Name="QueueRemoveButton"',
    'x:Name="QueueCountText"',
    'x:Name="QueueStatusText"',
    "{Binding DisplayName}",
    "{Binding StatusText}",
    "{Binding SetupSummary}",
    "Value=\"{Binding Progress}\"",
):
    require(token in xaml, f"QUEUE-01 queue UI lost: {token}")

print("PASS: QUEUE-01 OCR scan queue contract")

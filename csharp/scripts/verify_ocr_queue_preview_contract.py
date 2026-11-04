#!/usr/bin/env python3
"""OCR queue preview contract (QUEE-02 / 10/10): clicking a queue item shows
exactly that video, preview-only.

User model: press any queue entry and the preview jumps to that video whether
it is scanning or not; the scan keeps running in order. Preview follows a
separate view context (_viewPath/_viewMedia); the setup context (_path/_media,
ROI, mode/device/lanes boxes) must never move on a mere view. Clicking a live
cue while the preview sits on another video must first jump back to the
scanning video, then seek (previously it seeked the wrong player).
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs"
XAML = ROOT / "csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


require(PAGE.exists(), "QUEUE-02 OcrPage.xaml.cs is missing")
require(XAML.exists(), "QUEUE-02 OcrPage.xaml is missing")
page = read(PAGE)
xaml = read(XAML)

# Queue list click must drive the preview.
require('SelectionChanged="QueueList_SelectionChanged"' in xaml,
        "QUEUE-02 QueueList must wire SelectionChanged")
require('x:Name="QueueViewScanningButton"' in xaml
        and 'Click="QueueViewScanning_Click"' in xaml,
        "QUEUE-02 back-to-scanning button is missing")
require("private async void QueueList_SelectionChanged(" in page,
        "QUEUE-02 queue selection handler is missing")
require("private async void QueueViewScanning_Click(" in page,
        "QUEUE-02 back-to-scanning handler is missing")

# View-only proof: the view loader must never reassign setup state.
marker = "private async Task ShowQueueVideoAsync("
require(marker in page, "QUEUE-02 ShowQueueVideoAsync is missing")
start = page.index(marker)
rest = page[start:]
next_private = rest.find("\n    private ", 1)
body = rest[:next_private] if next_private > 0 else rest
require("_path =" not in body,
        "QUEUE-02 ShowQueueVideoAsync must not reassign the setup video path")
require("_region =" not in body,
        "QUEUE-02 ShowQueueVideoAsync must not touch the setup ROI")
require("_viewPath = item.VideoPath" in body,
        "QUEUE-02 ShowQueueVideoAsync must move the preview to the clicked item")

# The driver must track the scanning item; the cue click must jump back first.
require("_queueScanningItem = item" in page,
        "QUEUE-02 driver must track the scanning queue item")
require("await ShowQueueVideoAsync(_queueScanningItem" in page,
        "QUEUE-02 live-cue click must jump back to the scanning video first")
require("_lastSafeFrontier = result.SafeFrontierSeconds" in page,
        "QUEUE-02 scan loop must record the frontier for seek-back")

# 17/10: rebuilding on Up/Down may only restore the highlight under the guard
# flag — a bare `SelectedItem` assignment in SyncQueueView used to reopen the
# preview and hide the ROI overlay.
require("_syncingQueueSelect = true;\n        try { QueueList.SelectedItem = item; }\n        finally { _syncingQueueSelect = false; }" in page,
        "QUEUE-02 SyncQueueView must gate programmatic re-selection so rebuilds never fire preview")

print("PASS: QUEUE-02 OCR queue preview contract")

#!/usr/bin/env python3
"""Tab-switch visual contract (09/10): two bugs, one player lifecycle root.

Bug B (ghost): swapping tabs while the OCR tree is heavy (thousands of cue
rows + preview bitmap + bound MediaPlayer) left the old page composited over
the new one. Fix: two-phase swap (unmount old tree one tick before mounting
the new page) + OCR detaches its player surface before unmount and rebinds
on return, without losing video/preview state.

Bug A (black playback): disposing the old player without detaching it from
the element (unlike the Editor tab) left a dead surface behind, so rebinding
played audio over black video with no error. Fix: full detach before dispose
+ re-assert binding on play + MediaOpened diagnostic instead of silent black.
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
MAIN = ROOT / "csharp/src/BiliSubStudio.App/MainWindow.xaml.cs"
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/OcrPage.xaml.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


main = read(MAIN)
page = read(PAGE)

# Two-phase swap: unmount old content first, mount new content next tick.
require("outgoing.PrepareForTabSwitch()" in main,
        "TABSWITCH-01 swap must prepare the outgoing OCR page before unmount")
require("ContentFrame.Content = null;" in main
        and "DispatcherQueue.TryEnqueue(ProcessPendingNavigation);" in main,
        "TABSWITCH-01 swap must unmount in one tick and mount in the next")

# PrepareForTabSwitch: detach surface only, never dispose the player.
start = page.find("internal void PrepareForTabSwitch()")
require(start >= 0, "TABSWITCH-01 PrepareForTabSwitch is missing")
prepare = page[start:start + 900]
require("PreviewPlayer.SetMediaPlayer(null);" in prepare,
        "TABSWITCH-01 prepare must detach the player surface")
require("_player.Dispose()" not in prepare and "_player = null" not in prepare,
        "TABSWITCH-01 prepare must keep the player object (no state loss)")
require("Loaded += OcrPage_Loaded;" in page
        and "private void OcrPage_Loaded" in page,
        "TABSWITCH-01 returning to the tab must rebind the player")

# Black playback: full Editor-style detach before dispose + re-assert on play.
prep = page[page.find("private async Task PreparePlayerAsync"):]
prep = prep[:prep.find("private async Task SetPlaybackModeAsync")]
for token in (
    "_player.Source = null;",
    "PreviewPlayer.SetMediaPlayer(null);",
    "_player.Dispose();",
    "player.MediaOpened +=",
    "NaturalVideoHeight == 0",
):
    require(token in prep, f"TABSWITCH-01 player lifecycle lost: {token}")
mode = page[page.find("private async Task SetPlaybackModeAsync"):]
mode = mode[:mode.find("private void PlayerPositionChanged")]
require(mode.count("PreviewPlayer.SetMediaPlayer(_player);") >= 1,
        "TABSWITCH-01 entering playback must re-assert the player binding")

print("PASS: TABSWITCH-01 tab-switch visual contract")

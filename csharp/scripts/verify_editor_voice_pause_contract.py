#!/usr/bin/env python3
"""Voice pause contract (10/10): pause works for both TTS and ASR timing.

Background: the pause button used to hide during ASR ("no clip-resume"),
leaving a lone half-width cancel button. Research showed ASR resumes from
its frontier checkpoint after a cancel, same pattern as the TTS fake-pause
(cancel + keep progress + resume from cache). So the busy row always shows
both Pause + Cancel side by side with a fixed layout (no collapsing or
column-span juggling mid-run, per Microsoft progress UX guidance).
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PAGE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


page = read(PAGE)

# Pause targets either job owner, never nothing.
pause = page.split("private void PauseVoice_Click(", 1)[1].split(
    "private void CancelVoice_Click(", 1)[0]
require("if (_ttsJobId is { } tts)" in pause and "if (_asrJobId is { } asr)" in pause,
        "VOICE-PAUSE pause must target the TTS job or the ASR job")
require("_asrPausedByUser = true;" in pause and "_application.CancelJob(asr);" in pause,
        "VOICE-PAUSE ASR pause must flag and cancel like the TTS pause")
require("AsrPausedResumeHint" in page,
        "VOICE-02b paused ASR must show a resume hint naming Analyze/Tao voice")

# Paused ASR resumes through the existing timing entry points (no new engine).
require("_asrPausedByUser = false;" in page,
        "VOICE-PAUSE pause flag must reset when a new ASR run starts")

# Busy row is fixed: both buttons always visible, never collapsed or spanned.
refresh = page.split("private void RefreshEditorActions()", 1)[1].split(
    "private static string FormatClock", 1)[0]
require("PauseVoiceButton.Visibility = Visibility.Visible;" in refresh,
        "VOICE-PAUSE pause button must stay visible while voice work runs")
require("PauseVoiceButton.IsEnabled = voiceBusy;" in refresh,
        "VOICE-PAUSE pause must enable for ASR as well as TTS")
require("SetColumnSpan(CancelVoiceButton" not in refresh
        and "PauseVoiceButton.Visibility = ttsRunning" not in refresh,
        "VOICE-PAUSE layout must not collapse/span buttons mid-run")

print("PASS: VOICE-PAUSE voice pause contract")

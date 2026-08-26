#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
PLAYBACK = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.Playback.cs"


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PREVIEW-UNLOAD missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PREVIEW-UNLOAD method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PREVIEW-UNLOAD unterminated method: {signature}")
    return ""


source = PLAYBACK.read_text(encoding="utf-8")

require("private bool _isUnloading;" in source,
        "PREVIEW-UNLOAD controller needs one teardown state owner")

prepare = method_body(source, "internal async Task PrepareAsync()")
require("_isUnloading = false;" in prepare,
        "PREVIEW-UNLOAD reopening Editor must re-enable the controller")

toggle = method_body(source, "internal async Task ToggleAsync()")
require("var wasPlaying = IsPlaying;" in toggle
        and "if (IsPlaying) PauseAtCurrentFrame();" in toggle
        and "if (wasPlaying) await SetModeAsync(false, false);" in toggle,
        "PREVIEW-UNLOAD stopping processed preview must hold the current frame then exit preview mode so editor controls unlock")

unload = method_body(source, "internal async Task UnloadAsync()")
require("_isUnloading = true;" in unload and "IsPreviewMode = false;" in unload
        and "await ResetAsync(skipPresentation: true);" in unload,
        "PREVIEW-UNLOAD must suppress callbacks and visual updates before teardown awaits")

reset = method_body(source, "private async Task ResetAsync(bool skipPresentation = false)")
require("ClearFullscreenTracking(unregisterCallback: true, updateElement: !skipPresentation);" in reset
        and "if (!skipPresentation) ApplyPresentation(processed: false);" in reset,
        "PREVIEW-UNLOAD page teardown must not write presentation state after Unloaded")

dispose = method_body(source, "private void DisposePlayer()")
require(dispose.index("_page.PreviewPlayer.SetMediaPlayer(null);") < dispose.index("_player.Dispose();"),
        "PREVIEW-UNLOAD must detach MediaPlayerElement before disposing MediaPlayer")

position = method_body(source, "private void PlayerPositionChanged(")
require("if (_isUnloading || !IsPreviewMode) return;" in position
        and "if (_isUnloading || !IsPreviewMode || _page._media is null) return;" in position,
        "PREVIEW-UNLOAD must reject both incoming and queued position callbacks")

ended = method_body(source, "private void PlayerMediaEnded(")
failed = method_body(source, "private void PlayerMediaFailed(")
recovery = method_body(source, "private async Task RecoverFromPlayerFailureAsync(")
require("if (_isUnloading) return;" in ended and "if (_isUnloading) return;" in failed
        and "if (_isUnloading) return;" in recovery,
        "PREVIEW-UNLOAD late MediaEnded/MediaFailed callbacks must not revive a closed Editor")

load = method_body(source, "private async Task LoadSegmentAsync(")
require("if (!_isUnloading)" in load and "_page.RefreshEditorActions();" in load,
        "PREVIEW-UNLOAD rendering completion must not update a closed Editor UI")

print("PASS: PREVIEW-UNLOAD stop exits preview mode through owned cleanup and unlocks editing")

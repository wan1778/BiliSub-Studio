#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
ui = (ROOT / "internal/nativeui/ui_windows.go").read_text(encoding="utf-8")
telemetry = (ROOT / "internal/nativeui/ocr_telemetry.go").read_text(encoding="utf-8")
app = "\n".join(p.read_text(encoding="utf-8") for p in sorted((ROOT / "internal/application").glob("*.go")) if not p.name.endswith("_test.go"))
errors = []

# Production native UI must expose every major workflow that existed before the
# migration. This is a source-level parity gate; browser_e2e.py remains the
# behavioral oracle for the legacy adapter.
required_ui = {
    "navigation": ["Phụ đề", "Video", "OCR phụ đề", "Chỉnh video", "Cài đặt"],
    "subtitle": ["w.analyzeSubtitle()", "w.startSubtitle()", "w.pickOutput(w.subOut)", "w.openOutput(w.subOut)"],
    "video": ["w.analyzeVideo()", "w.startVideo()", "w.cancelActive()"],
    "ocr": [
        "w.pickOCRVideo()", "w.applyOCRSubtitlePreset()", "w.prepareOCR()", "w.testOCR()",
        "w.startOCR(false)", "w.pauseOCR()", "w.startOCR(true)", "w.exportOCR()",
        "w.refreshOCRCheckpoint()", "w.seekSelectedCue()", "w.syncCueToTime(at)", "w.toggleFullscreen()",
        "ocrCueSummary", "formatOCRTelemetry", "telemetryFromCheckpoint",
    ],
    "editor": [
        "w.pickEditorVideo()", "w.editorAddPreset(\"subtitle\")", "w.editorAddPreset(\"watermark\")",
        "w.editorDeleteSelected()", "w.editorUndoLast()", "w.exportEditor()", "w.editorRegionList",
    ],
    "settings/login/update": [
        "w.saveCookie()", "w.deleteCookie()", "w.startQR()", "w.changeTheme()", "w.pickDefaultOutput()",
        "w.doCleanup()", "w.doResetTools()", "w.doRemoveOCR()", "w.doCheckUpdate()", "w.doApplyUpdate()",
        "w.sendBugReport()", "w.requestClose()",
    ],
}
for area, markers in required_ui.items():
    for marker in markers:
        if marker not in ui and marker not in telemetry:
            errors.append(f"{area}: missing native marker {marker}")

required_app = [
    "func (a *App) Metadata", "func (a *App) StartSubtitle", "func (a *App) StartVideo",
    "func (a *App) PreviewInfo", "func (a *App) EnsureFFmpeg", "func (a *App) OCRFrame",
    "func (a *App) InspectOCRCheckpoint", "func (a *App) RemoveOCRCheckpoint", "func (a *App) StartOCRScan",
    "func (a *App) PauseJob", "func (a *App) ExportOCR", "func (a *App) StartEditor", "func (a *App) CancelJob",
    "func (a *App) SetCookie", "func (a *App) DeleteCookie", "func (a *App) QRStart", "func (a *App) QRPoll",
    "func (a *App) SetOutputDir", "func (a *App) SetTheme", "func (a *App) SetUpdateCheck",
    "func (a *App) CleanupStorage", "func (a *App) ResetTools", "func (a *App) RemoveOCR",
    "func (a *App) CheckUpdate", "func (a *App) PrepareUpdate", "func (a *App) SendBugReport",
    "func (a *App) PrepareShutdown",
]
for marker in required_app:
    if marker not in app:
        errors.append(f"application layer missing {marker}")

# Full OCR native telemetry must include the fields the web parity UI already
# exposed plus checkpoint topology/counters.
for marker in [
    "OCRImages", "InferenceCalls", "OCRCallsPerCue", "VisualSkips", "VisualConfirmations", "OCRRetries",
    "FramePipelineSeconds", "VisualSeconds", "EncodeSeconds", "OCRSeconds", "RealtimeSpeed",
    "ParallelismSelected", "ActiveLanes", "CompletedLanes", "BoundaryMerges", "ProgressPercent", "Schema",
]:
    if marker not in telemetry:
        errors.append(f"OCR telemetry missing field {marker}")

if errors:
    print("NATIVE FEATURE PARITY AUDIT: FAIL")
    for e in errors:
        print(" -", e)
    sys.exit(1)
print("NATIVE FEATURE PARITY AUDIT: PASS")

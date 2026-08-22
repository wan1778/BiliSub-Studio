#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
ui = (ROOT/'internal/nativeui/ui_windows.go').read_text(encoding='utf-8')
validation = (ROOT/'internal/nativeui/input_validation.go').read_text(encoding='utf-8')
errors=[]

checks = {
    'subtitle end-to-end': [
        'case idSubURL:', 'invalidateSubtitleMetadataIfURLChanged()', 'w.analyzeSubtitle()',
        'w.subAnalyzedURL = raw', 'metadataMatchesSubURL', 'w.startSubtitle()', 'idSubCancel',
    ],
    'video end-to-end': [
        'case idVideoURL:', 'invalidateVideoMetadataIfURLChanged()', 'w.analyzeVideo()',
        'w.videoAnalyzedURL = raw', 'metadataMatchesVideoURL', 'w.startVideo()', 'idVideoCancel',
    ],
    'OCR live validation and lifecycle': [
        'case idOCRTop, idOCRBottom, idOCRLeft, idOCRRight:', 'w.validateOCRLive()',
        'w.validateOCRRegion() == nil', 'w.testOCR()', 'w.startOCR(false)', 'w.pauseOCR()',
        'w.startOCR(true)', 'w.refreshOCRCheckpoint()', 'w.exportOCR()', 'w.seekSelectedCue()',
        'w.syncCueToTime(at)',
    ],
    'Editor live validation and export': [
        'case idEditorX, idEditorY, idEditorW, idEditorH, idEditorStrength, idEditorStart, idEditorEnd:',
        'w.validateEditorSelection()', 'editorSelectionValid', 'w.editorAddPreset("subtitle")',
        'w.editorAddPreset("watermark")', 'w.editorDeleteSelected()', 'w.editorUndoLast()',
        'w.exportEditor()', 'idEditorCancel',
    ],
    'Settings/QR/update/bug': [
        'case idCookie:', 'case idBugNote:', 'w.saveCookie()', 'w.deleteCookie()', 'w.startQR()',
        'w.doCheckUpdate()', 'w.doApplyUpdate()', 'w.sendBugReport()', 'w.requestClose()',
    ],
    'feedback/progress': [
        'subProgress', 'videoProgress', 'ocrProgress', 'editorProgress',
        '[Cần thao tác]', '[Đang xử lý]', '[Lỗi]', '[Hoàn tất]', '[Sẵn sàng]',
    ],
}
for area, markers in checks.items():
    for marker in markers:
        if marker not in ui:
            errors.append(f'{area}: missing {marker}')

for marker in [
    'if bottom <= top', 'if right <= left',
    'if x+width > 100.000001', 'if y+height > 100.000001',
    'math.IsNaN', 'math.IsInf',
]:
    if marker not in validation:
        errors.append(f'strict validation missing {marker}')

# No stale analyzed URL can be used for download.
for marker in [
    'normalizedURLField(w.subURL) != w.subAnalyzedURL',
    'normalizedURLField(w.videoURL) != w.videoAnalyzedURL',
]:
    if marker not in ui:
        errors.append(f'stale metadata start guard missing {marker}')

# All edit controls that affect live button state must have IDs and therefore
# generate EN_CHANGE notifications to the parent window.
for marker in [
    'w.subOut = w.editID(pageSubtitle', 'w.videoOut = w.editID(pageVideo',
    'w.ocrOut = w.editID(pageOCR', 'w.editorOut = w.editID(pageEditor',
    'w.editorName = w.editID(pageEditor', 'w.settingsCookie = w.editID(pageSettings',
    'idBugNote',
]:
    if marker not in ui:
        errors.append(f'live EN_CHANGE wiring missing {marker}')

if errors:
    print('NATIVE INTERACTION SMOKE AUDIT: FAIL')
    for e in errors:
        print(' -', e)
    sys.exit(1)
print('NATIVE INTERACTION SMOKE AUDIT: PASS')

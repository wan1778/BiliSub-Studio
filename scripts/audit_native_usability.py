#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
ui = (ROOT/'internal/nativeui/ui_windows.go').read_text(encoding='utf-8')
win32 = (ROOT/'internal/nativeui/win32_windows.go').read_text(encoding='utf-8')
ux = (ROOT/'internal/nativeui/ux_contract.go').read_text(encoding='utf-8')
errors=[]

required = {
    'beginner page headings': ['pageTitle', 'pageHelp', 'uxForPage'],
    'tooltips': ['initTooltips()', 'tooltipFor(', 'ttfIDIsHwnd', 'ttfSubclass'],
    'progress': ['subProgress', 'videoProgress', 'ocrProgress', 'editorProgress', 'progressSet'],
    'workflow states': ['subState', 'videoState', 'ocrStatus', 'editorStatus', 'settingsStatus'],
    'empty states': ['Bước tiếp theo:', 'chưa có telemetry', 'Danh sách phụ đề: 0 / 0 câu'],
    'validation': ['validateOCRRegion()', 'validateEditorSelection()', 'validateEditorExport()', 'validateOCRLive()', 'setEditorValidationError'],
    'url metadata invalidation': ['invalidateSubtitleMetadataIfURLChanged()', 'invalidateVideoMetadataIfURLChanged()', 'subAnalyzedURL', 'videoAnalyzedURL'],
    'central disabled state': ['func (w *window) syncControls()', 'enable(w.ocrExport, ocrIdle && !w.ocrHasCheckpoint'],
    'keyboard': ['isDialogMessageW.Call', 'vkF1', "wparam >= uintptr('1')", 'focusPageStart'],
    'dpi': ['wmDpiChanged', 'rebuildFonts()', 'w.logical(', 'w.mv('],
    'fullscreen': ['idOCRFullscreen', 'idEditorFullscreen', 'toggleFullscreen()', 'vkEscape'],
    'safe close': ['PrepareShutdown(ctx)', 'wmAppCloseReady'],
    'QR lifecycle': ['Hủy QR', 'qrBusy', 'pollQR()', 'qrState'],
    'update lifecycle': ['updateAvailable', 'updateBusy', 'doCheckUpdate()', 'doApplyUpdate()'],
    'cancellation': ['idSubCancel', 'idVideoCancel', 'idEditorCancel', 'cancelActive()'],
    'checkpoint UX': ['refreshOCRCheckpoint()', 'ocrHasCheckpoint', 'ProgressPercent'],
    'cue seek': ['seekSelectedCue()', 'syncCueToTime(at)'],
    'success/error feedback': ['[Lỗi]', '[Hoàn tất]', '[Đang xử lý]', '[Sẵn sàng]'],
}
corpus = ui + '\n' + ux
for area, markers in required.items():
    for marker in markers:
        if marker not in corpus:
            errors.append(f'{area}: missing {marker}')

for marker in ['Segoe UI', 'SetProcessDpiAwarenessContext', 'GetDpiForWindow', 'IsDialogMessageW', 'tooltips_class32']:
    if marker.lower() not in win32.lower():
        errors.append(f'Win32 usability support missing {marker}')

# syncControls is the only owner allowed to write enabled/disabled state.
sync_start = ui.index('func (w *window) syncControls()')
sync_end = ui.index('func (w *window) canStartJob()', sync_start)
for i, line in enumerate(ui.splitlines(), start=1):
    if 'enable(' not in line:
        continue
    offset = ui.find(line)
    # Use line-number based absolute offset for duplicate lines.
    offset = sum(len(x)+1 for x in ui.splitlines()[:i-1])
    if not (sync_start <= offset < sync_end):
        errors.append(f'enable-state write outside syncControls at ui_windows.go:{i}: {line.strip()}')

for marker in [
    'w.subURL = w.editID(pageSubtitle, "", false, idSubURL)',
    'w.videoURL = w.editID(pageVideo, "", false, idVideoURL)',
    'w.settingsCookie = w.editID(pageSettings, "", false, idCookie)',
    'idBugNote',
]:
    if marker not in ui:
        errors.append(f'live interaction wiring missing {marker}')

# Every critical actionable control must have a tooltip key and binding.
keys = [
 'sub_url',
 'sub_analyze',
 'sub_track',
 'sub_format',
 'sub_output',
 'sub_download',
 'sub_cancel',
 'video_url',
 'video_analyze',
 'video_quality',
 'video_mode',
 'video_speed',
 'video_container',
 'video_output',
 'video_download',
 'video_cancel',
 'ocr_pick',
 'ocr_preset',
 'ocr_play',
 'ocr_mute',
 'ocr_fullscreen',
 'ocr_timeline',
 'ocr_roi',
 'ocr_mode',
 'ocr_sensitivity',
 'ocr_device',
 'ocr_parallel',
 'ocr_prepare',
 'ocr_test',
 'ocr_start',
 'ocr_pause',
 'ocr_restart',
 'ocr_clear',
 'ocr_export',
 'ocr_output',
 'ocr_cues',
 'editor_pick',
 'editor_play',
 'editor_mute',
 'editor_fullscreen',
 'editor_presets',
 'editor_delete',
 'editor_undo',
 'editor_region',
 'editor_effect',
 'editor_strength',
 'editor_scope',
 'editor_timing',
 'editor_output',
 'editor_regions',
 'editor_export',
 'editor_cancel',
 'theme',
 'default_output',
 'default_output_pick',
 'default_output_open',
 'cookie',
 'cookie_save',
 'cookie_delete',
 'qr',
 'auto_update',
 'update',
 'cleanup',
 'reset_tools',
 'remove_ocr',
 'close_app',
 'bug',
]
for key in keys:
    if f'"{key}"' not in ux:
        errors.append(f'tooltip copy missing key {key}')
    tooltips = ui[ui.index('func (w *window) initTooltips()'):ui.index('func themeIndex')]
    if f'"{key}"' not in tooltips:
        errors.append(f'tooltip not bound for key {key}')

if errors:
    print('NATIVE USABILITY AUDIT: FAIL')
    for e in errors:
        print(' -', e)
    sys.exit(1)
print('NATIVE USABILITY AUDIT: PASS')

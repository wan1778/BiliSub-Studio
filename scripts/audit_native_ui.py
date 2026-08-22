#!/usr/bin/env python3
from pathlib import Path
import re, sys

root = Path(__file__).resolve().parents[1]
ui = (root/'internal/nativeui/ui_windows.go').read_text(encoding='utf-8')
win32 = (root/'internal/nativeui/win32_windows.go').read_text(encoding='utf-8')
ux = (root/'internal/nativeui/ux_contract.go').read_text(encoding='utf-8')
main = (root/'cmd/bilisub/main.go').read_text(encoding='utf-8')
errors=[]

# Every HWND stored as a named window field in build() must have explicit
# DPI-scaled geometry. Captions are covered by layoutCaptions/captionLayout.
build = ui[ui.index('func (w *window) build()'):ui.index('func deviceIndex')]
assigned = sorted(set(re.findall(r'w\.(\w+)\s*=\s*w\.(?:edit|editID|button|combo|label|logBox|add)\(', build)))
layout = ui[ui.index('func (w *window) layout()'):ui.index('func (w *window) syncControls()')]
for name in assigned:
    direct = f'w.mv(w.{name},' in layout
    looped = f'w.{name}' in layout and ('w.mv(pair.label,' in layout or 'w.mv(pair.edit,' in layout)
    if not (direct or looped):
        errors.append(f'native control has no explicit DPI-scaled layout: {name}')

if 'stackAnonymous' in ui:
    errors.append('legacy no-op stackAnonymous layout is still present')

required_ui = [
    'w.layoutCaptions(pageSubtitle', 'w.layoutCaptions(pageVideo',
    'w.layoutCaptions(pageOCR', 'w.layoutCaptions(pageEditor',
    'w.layoutCaptions(pageSettings', 'qrcode.Encode(q.URL)', 'w.paintQRCode(hdc)',
    'w.seekSelectedCue()', 'w.syncCueToTime(at)', 'wsPopup|wsVisible',
    'w.app.PrepareShutdown(ctx)', 'wmAppCloseReady',
    'w.initTooltips()', 'w.rebuildFonts()', 'wmDpiChanged',
    'isDialogMessageW.Call', 'vkF1', "wparam >= uintptr('1')",
    'w.editorFullscreen', 'w.subCancel', 'w.videoCancel', 'w.editorCancel',
    'w.subProgress', 'w.videoProgress', 'w.ocrProgress', 'w.editorProgress',
    'w.pageTitle[p]', 'w.pageHelp[p]',
]
for marker in required_ui:
    if marker not in ui:
        errors.append(f'missing native UI contract marker: {marker}')

for marker in [
    'SetProcessDpiAwarenessContext', 'GetDpiForWindow', 'CreateFontW',
    'Segoe UI', 'tooltips_class32', 'IsDialogMessageW',
]:
    if marker not in win32 and marker.lower() not in win32.lower():
        errors.append(f'missing Win32 usability marker: {marker}')

for page in ['pageSubtitle', 'pageVideo', 'pageOCR', 'pageEditor', 'pageSettings']:
    if page not in ux:
        errors.append(f'missing beginner UX copy for {page}')
for marker in ['Title:', 'Help:', 'tooltipTextByKey']:
    if marker not in ux:
        errors.append(f'missing UX contract marker: {marker}')

for forbidden in ['net.Listen', '127.0.0.1', 'http.Server', 'internal/api', '.Launch(']:
    if forbidden in main:
        errors.append(f'production startup still contains browser/HTTP marker: {forbidden}')
for required in ['application.New', 'nativeui.Run', 'proc.EnableContainment', 'proc.Breakaway']:
    if required not in main:
        errors.append(f'production startup missing native marker: {required}')

if errors:
    print('NATIVE UI AUDIT: FAIL')
    for e in errors:
        print(' -', e)
    sys.exit(1)
print(f'NATIVE UI AUDIT: PASS ({len(assigned)} named controls have explicit DPI-scaled layout)')

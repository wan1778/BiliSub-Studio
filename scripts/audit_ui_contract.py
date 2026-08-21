#!/usr/bin/env python3
from __future__ import annotations
from pathlib import Path
from bs4 import BeautifulSoup
import re, sys

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / 'web' / 'index.html'
EMBED = ROOT / 'internal' / 'api' / 'web' / 'index.html'
SERVER = ROOT / 'internal' / 'api' / 'server.go'

html = UI.read_text(encoding='utf-8')
server = SERVER.read_text(encoding='utf-8')
errors: list[str] = []

if not EMBED.exists() or UI.read_bytes() != EMBED.read_bytes():
    errors.append('web/index.html != internal/api/web/index.html')

soup = BeautifulSoup(html, 'html.parser')

# Product contract: BiliSub exposes exactly one OCR feature. Replacing the OCR
# core must never create a second page, engine selector, or parallel user flow.
ocr_pages = soup.select('section#ocr.page')
if len(ocr_pages) != 1:
    errors.append(f'exactly one OCR page required, got {len(ocr_pages)}')
for forbidden in ('OCR v2', 'OCRv2', 'RapidOCR', 'Umi-OCR'):
    if forbidden.lower() in soup.get_text(' ', strip=True).lower():
        errors.append(f'legacy/parallel OCR label leaked into UI: {forbidden}')

ids = [el['id'] for el in soup.find_all(attrs={'id': True})]
seen = set(); dups = sorted({x for x in ids if x in seen or seen.add(x)})
if dups:
    errors.append('duplicate DOM ids: ' + ', '.join(dups))

# Every API route referenced by the UI must be registered by server.Handler().
ui_routes = sorted(set(re.findall(r'(?<![A-Za-z0-9_])(/api/[A-Za-z0-9_./?-]+)', html)))
ui_routes_base = sorted({r.split('?')[0] for r in ui_routes})
server_routes = sorted(set(re.findall(r'mux\.HandleFunc\("(/api/[^"]+)"', server)))
missing_routes = sorted(set(ui_routes_base) - set(server_routes))
if missing_routes:
    errors.append('UI references unregistered API routes: ' + ', '.join(missing_routes))

# Interactive buttons must have one of: inline onclick, direct ID handler, or a
# documented delegated/group handler. This catches dead buttons after refactors.
direct_ids = set(re.findall(r's\("([A-Za-z0-9_-]+)"\)\.onclick\s*=', html))
inline_ids = {el.get('id') for el in soup.find_all('button') if el.get('onclick') and el.get('id')}
# Button groups intentionally wired via querySelectorAll/delegation.
group_selectors = re.findall(r'document\.querySelectorAll\("([^"]+)"\)', html)
covered_group_ids = set()
for sel in group_selectors:
    if sel.startswith('#') and ' ' in sel:
        root_id = sel[1:].split()[0]
        for el in soup.select(sel):
            if el.get('id'):
                covered_group_ids.add(el['id'])

# Nav buttons and effect buttons are delegated by data attributes.
for el in soup.find_all('button'):
    if el.get('data-page') or el.get('data-effect'):
        continue
    if el.get('onclick'):
        continue
    bid = el.get('id')
    if bid and (bid in direct_ids or bid in inline_ids or bid in covered_group_ids):
        continue
    # Modal/nav buttons can be anonymous with inline onclick only; anonymous
    # non-inline buttons are suspicious because they cannot be addressed safely.
    if not bid:
        errors.append('anonymous button without inline/delegated handler: ' + el.get_text(' ', strip=True)[:60])
    else:
        errors.append(f'button #{bid} has no detectable click handler')


# OCR control state has one authority. RC5 field testing exposed a regression
# where ocrEnsure enabled Start, then refreshAppStatus immediately disabled it
# using <video>.duration even though the shared FFmpeg fallback preview was ready.
# Critical OCR controls must therefore be written only by ocrSyncControls().
critical_ocr_controls = [
    'startOCR', 'testOCR', 'ocrPlay', 'ocrScrub', 'ocrMute',
    'ocrFullscreen', 'ocrSubtitlePreset', 'stopOCR', 'clearOCR', 'exportOCR',
]
control_fn = re.search(r'function\s+ocrSyncControls\s*\(\)\s*\{(.*?)\}function', html, re.S)
if not control_fn:
    errors.append('ocrSyncControls() missing or not parseable')
else:
    control_body = control_fn.group(1)
    for cid in critical_ocr_controls:
        needle = f's("{cid}").disabled='
        total = html.count(needle)
        inside = control_body.count(needle)
        if total != 1 or inside != 1:
            errors.append(f'OCR control #{cid} must have exactly one disabled-state writer in ocrSyncControls (total={total}, inside={inside})')

# First-time PaddleOCR setup may download a private Python runtime, PaddlePaddle,
# PaddleOCR and PP-OCRv6 models. The one-click UI must keep polling long enough
# for that managed install instead of presenting a false timeout after 3 minutes.
ensure_poll = re.search(r'for\(let i=0;i<(\d+);i\+\+\)\{await ht\(1000\);const st=await r\("/api/ocr/engine/status"\)', html)
if not ensure_poll:
    errors.append('OCR ensure polling contract missing')
elif int(ensure_poll.group(1)) < 1800:
    errors.append(f'OCR ensure polling window too short for one-click managed install: {ensure_poll.group(1)}s')

# App status may set the OCR engine fact, but must not derive preview readiness
# from the direct <video> element. Fallback preview readiness is feature-neutral.
status_fn = re.search(r'async function refreshAppStatus\(\)\{(.*?)\}async function pickFolder', html, re.S)
if status_fn and ('ocrVideo.duration' in status_fn.group(1) or 'ocrVideo.videoWidth' in status_fn.group(1)):
    errors.append('refreshAppStatus must not derive OCR readiness from direct-video dimensions/duration')

# Settings/bootstrap contract: sidebar version/drive must not depend on opening Settings.
if 'refreshAppStatus()' not in html:
    errors.append('refreshAppStatus() missing from UI')
# Require an unconditional startup status call after handler wiring. The current
# lifecycle intentionally has no heartbeat/idle lease, so startup status is a
# standalone call near the end of the script.
startup_tail = html[-16000:]
if 'refreshAppStatus();' not in startup_tail:
    errors.append('no unconditional startup refreshAppStatus() call near script end')

print(f'DOM ids: {len(ids)} total / {len(set(ids))} unique')
print(f'UI API routes: {len(ui_routes_base)}')
print(f'Server API routes: {len(server_routes)}')
print(f'Buttons: {len(soup.find_all("button"))}')
if errors:
    print('UI CONTRACT: FAIL')
    for e in errors:
        print(' -', e)
    sys.exit(1)
print('UI CONTRACT: PASS')

#!/usr/bin/env python3
import argparse, hashlib, struct
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('exe')
a = p.parse_args()
path = Path(a.exe)
b = path.read_bytes()

valid = []
i = 0
while True:
    i = b.find(b'MZ', i)
    if i < 0:
        break
    if i + 0x40 <= len(b):
        e_lfanew = struct.unpack_from('<I', b, i + 0x3c)[0]
        if i + e_lfanew + 4 <= len(b) and b[i+e_lfanew:i+e_lfanew+4] == b'PE\0\0':
            valid.append(i)
    i += 2

errors = []
if valid != [0]:
    errors.append(f'expected one PE at offset 0, got {valid}')
if b'BiliSubStudioCore.exe' in b:
    errors.append('legacy BiliSubStudioCore.exe marker found')

required = [
    b'BiliSubStudioNativeWindow',
    b'PP-OCRv6_small_det',
    b'PP-OCRv6_small_rec',
    b'yt-dlp.exe',
    b'ffmpeg.exe',
]
for marker in required:
    if marker not in b:
        errors.append(f'missing expected native/runtime marker {marker!r}')

# The production entrypoint no longer links the HTTP/browser adapter. These old
# route strings are high-signal evidence that internal/api was accidentally
# pulled back into the release binary.
for marker in [b'/api/video/download', b'/api/ocr/engine/ensure', b'/api/ocr/scan']:
    if marker in b:
        errors.append(f'legacy browser API marker linked into native release {marker!r}')

sha = hashlib.sha256(b).hexdigest()
print(f'file={path}')
print(f'size={len(b)}')
print(f'sha256={sha}')
print(f'valid_pe_offsets={valid}')
if errors:
    for e in errors:
        print('ERROR:', e)
    raise SystemExit(1)
print('release static validation: PASS (native-only production binary)')

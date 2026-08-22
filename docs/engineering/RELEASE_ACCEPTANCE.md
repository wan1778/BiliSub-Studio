# BiliSub Studio Native Windows Release Acceptance

Google Drive is the public beta channel. A candidate may be promoted only after the exact source/binary pair passes the automated matrix and the Windows-required field checks.

## Status vocabulary

- **PASS**: exercised in the current environment with an assertion on observable output.
- **PASS-MOCK**: a controlled integration/parity fixture passed; external provider or real Windows interaction was not exercised.
- **WINDOWS-REQUIRED**: cannot be truthfully proven by Linux cross-build alone.
- **BLOCKED**: release is forbidden.

## Production architecture contract

`BiliSubStudio.exe` must start `application.App -> nativeui.Run` directly. The release must not start or link the legacy localhost/browser UI. `internal/api` + embedded HTML remain only as a regression oracle until deliberately removed in a later cleanup.

Normal helper executables are app-owned and inherit the BiliSub Windows Job Object. The updater is the only breakaway helper.

## Functional acceptance matrix

| Area | User actions / contract | Required evidence |
|---|---|---|
| Native bootstrap | launch, title/version, five navigation pages, resize/min/max, close | native source audit + Windows x64 build; **WINDOWS-REQUIRED** visible field test |
| Layout/usability | every native label/control has explicit geometry; no overlap at supported sizes; page title/help/tooltips/progress/empty/loading/error/success/disabled states; Segoe UI, DPI scaling and keyboard navigation | `layout_contract_test.go` + `audit_native_ui.py` + `audit_native_usability.py` + `audit_native_layout_geometry.py`; **WINDOWS-REQUIRED** DPI/resize/font/Tab-order visual check |
| Native lifecycle | no Chrome/Edge/WebView/localhost UI; close cleans children | production entrypoint test + release binary route-marker rejection + Job Object code; **WINDOWS-REQUIRED** Task Manager/netstat check |
| Native pickers/folders | OCR/Editor source picker, output folders, Open folder | cross-build + **WINDOWS-REQUIRED** real dialogs/Unicode paths/Explorer |
| Cookie + QR login | cookie save/delete, native QR generate/render/poll/expiry/login | application tests + QR encoder tests + independent QR decode; provider sanity check before stable |
| Subtitle | URL edit invalidates stale metadata/track immediately; metadata, track, SRT/TXT/JSON, output, progress, cancel and feedback | `audit_native_interactions.py` + subtitle/video resolver regressions + legacy parity E2E + live Bilibili field test before stable |
| Video | URL edit invalidates stale metadata/quality immediately; quality/mode/speed/container, output, progress, start/cancel and feedback | `audit_native_interactions.py` + downloader regressions + legacy parity E2E + live field test before stable |
| Native media preview | H.264/HEVC/AV1/VP9 local source, Play/Pause, seek, mute/audio | nativeplayer tests + Windows x64 build; **WINDOWS-REQUIRED** codec/audio field matrix |
| OCR engine | Auto/CPU/GPU/Hybrid, ensure/status/remove, current-frame OCR | OCR manager tests + standalone GPU audit; **WINDOWS-REQUIRED** real NVIDIA/CPU initialization |
| OCR scan | strict live ROI validation (0–100, Bottom>Top, Right>Left, minimum size), mode/sensitivity, Auto/1/2/4/8/16 lanes, NVDEC/software fallback, full live/final telemetry | input validation tests + interaction smoke audit + deterministic scanner/parallel/pool/reconciler tests + race + native feature-parity audit; **WINDOWS-REQUIRED** real long-video GPU scan |
| OCR Pause/Resume | inspect schema-3/schema-4 checkpoint, Pause, close while scanning, reopen Resume, restart from zero | checkpoint inspection/telemetry tests + scanner fsynced-checkpoint tests + `application.PrepareShutdown` safe-close handshake tests; **WINDOWS-REQUIRED** close/reopen field test |
| Chinese-only SRT | reject foreign-script garbage and repeated punctuation artifacts | OCR tracker/reconciler/export regression tests + field SRT audit |
| Cue ↔ timeline | scrub selects nearest cue; cue click seeks preview | native source audit + pure cue/list logic + **WINDOWS-REQUIRED** interaction field test |
| Fullscreen | OCR preview enters borderless monitor fullscreen; Escape restores original style/rect | native source/cross-build audit + **WINDOWS-REQUIRED** multi-monitor/DPI field test |
| Editor | source preview, multi-region/presets/undo/delete, strict live X/Y/W/H validation including `X+W<=100`, `Y+H<=100`, effect/strength/time scope, progress/cancel/export | input validation tests + interaction smoke audit + videoedit regressions + native layout audit + **WINDOWS-REQUIRED** real interaction/export |
| Update | manifest, SHA/size validation, safe close, breakaway updater, atomic swap/restart | update tests + Job Object/breakaway audit; **WINDOWS-REQUIRED** self-swap field test |
| Tool ownership | no system PATH ffmpeg/ffprobe/yt-dlp; OCR private runtime only; no nvidia-smi/PowerShell | tools tests + standalone GPU audit + source audit |
| Process cleanup | ffmpeg/yt-dlp/Python do not survive app exit/crash | Job Object implementation/cross-build + **WINDOWS-REQUIRED** process-tree field test |

## Automated gate

Run from repo root:

```text
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py
python scripts/audit_native_ui.py
python scripts/audit_native_usability.py
python scripts/audit_native_layout_geometry.py
python scripts/audit_native_interactions.py
python scripts/audit_feature_parity.py
python scripts/audit_standalone_gpu.py
python scripts/audit_application_boundary.py
python scripts/audit_dependency_process.py
python scripts/generate_code_map.py --check
go run scripts/generate_ocr_call_map.go --check
python -u scripts/browser_e2e.py
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go test -exec=/bin/true -count=1 ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -H=windowsgui" -o <candidate.exe> ./cmd/bilisub
python scripts/validate_release.py <candidate.exe>
```

`audit_native_usability.py` enforces beginner-facing help/tooltips/state ownership and requires `syncControls()` to be the only native enable/disable writer. `audit_native_layout_geometry.py` checks all five pages at multiple logical client sizes. `audit_native_interactions.py` checks end-to-end command wiring, stale-metadata invalidation and strict live validation. `audit_feature_parity.py` checks that the native UI/application path exposes every major legacy workflow, `audit_application_boundary.py` prevents UI/service ownership drift, and `audit_dependency_process.py` enforces app-owned helper/process containment. `browser_e2e.py` remains a legacy parity oracle only; it does not mean the production app uses a browser.

## Promotion rule

1. Build one Windows x64 candidate outside Google Drive from a clean commit.
2. Run every automated gate above and record exact commit/SHA-256.
3. Run every applicable **WINDOWS-REQUIRED** item on that exact binary.
4. Re-run release validation/hash after field testing.
5. Only then upload EXE/source/notes and update the beta manifest.
6. Read back uploaded EXE/source/manifest and verify size + SHA-256 before deleting the previous beta.

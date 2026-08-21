# BiliSub Studio Engineering Guardrails

These rules exist to prevent context drift and blind edits as the codebase grows.

## Source of truth

1. Runtime behavior is defined by the current source tree and tests, not chat history, binaries, comments, or old release notes.
2. `ARCHITECTURE.md` defines ownership boundaries. If source and docs disagree, source wins and the docs must be corrected in the same change.
3. `docs/engineering/CODE_MAP.generated.md` is generated from the current source by `scripts/generate_code_map.py`; never hand-edit it. `--check` must pass before release.
4. `docs/engineering/OCR_CALL_MAP.generated.md` is generated from every production function in `internal/ocr` by `scripts/generate_ocr_call_map.go`; it records source line ownership and direct calls, and `--check` must pass after every OCR code change.
5. Never patch a release binary to fix v4. Fix source, test, build, then publish a new release.
6. `docs/migration/CSHARP_CODE_MAP.generated.md` is generated from the C# production tree by `csharp/scripts/generate_csharp_code_map.py`; never hand-edit it and keep `--check` passing.

## Mandatory workflow for non-trivial changes

1. Pin the current Git commit and record the task in a context ledger/plan.
2. Map the execution path from entrypoint/route to the target symbol.
3. Run upstream impact analysis for every production symbol to be edited. Direct callers must be accounted for. A missing caller set is UNKNOWN, not automatically safe.
4. Read the current source ranges for the target, its direct callers, and its existing regression tests.
5. Write the regression scenario before or with the fix.
6. Make the smallest scoped change that satisfies the plan.
7. Run package tests, race tests, vet, Windows build, and release validation.
8. Recompute the changed-symbol/call-path map and verify no ownership boundary was crossed unexpectedly.
9. Run the full release acceptance matrix in `docs/engineering/RELEASE_ACCEPTANCE.md`; a targeted fix never waives unrelated regression checks.
10. Do not update Google Drive until Windows-required items for the exact RC binary are confirmed.

## Ownership boundaries

- `cmd/bilisub`: native process startup/shutdown, Job Object setup, and self-update handoff only. It must not start HTTP/browser/WebView UI.
- `internal/application`: production application boundary. Native UI calls it directly; it orchestrates services but must not reimplement video/OCR algorithms.
- `internal/nativeui`: Win32 native controls/layout/dialogs/render/input and UI state only. It must not spawn FFmpeg/Python or implement downloader/OCR algorithms.
- `internal/nativeplayer`: app-owned FFmpeg preview decoding plus native Windows video/audio output only. It does not own OCR timing/accuracy.
- `internal/qrcode`: browser-free QR matrix generation only.
- `internal/api`: legacy HTTP/browser parity adapter used by tests; it is not imported by `cmd/bilisub` production startup. It must not implement video/OCR algorithms.
- `internal/video`: Bilibili stream resolution, download/retry/resume, fallback, and ffmpeg media assembly.
- `internal/videoedit`: local video cleanup regions, FFmpeg filter-graph generation, render progress, and non-destructive export only. It must not own Bilibili download logic.
- `internal/ocr`: the single OCR subsystem: managed PaddleOCR installation/lifecycle/protocol, PP-OCRv6 Small CPU/GPU worker scheduling, and deterministic local-video OCR scanning. Browser preview code must not own scan accuracy.
- `internal/tools`: installation/discovery of external tools only.
- `internal/jobs`: job state/cancellation only.
- `internal/appstate`: persistent config/cookie state only.
- `internal/subtitle`: subtitle fetch/parse/export only.
- `internal/proc`: Windows child-process visibility policy.
- Production local filesystem pickers are `internal/nativeui` Win32 platform integration shared by features; do not attribute them to OCR, Editor, or downloader ownership. Legacy `/api/pick-*` exists only in the parity adapter.

## Hard prohibitions

- No second BiliSub backend process or fixed secondary OCR HTTP port.
- No `BiliSubStudioCore.exe`.
- No direct normal child helper process on Windows unless it is wrapped by `proc.Hide` and inherits the app Job Object. Self-updater handoff must use `proc.Breakaway`.
- No production localhost UI, external browser launch, WebView, or WebView2. `cmd/bilisub` must enter `nativeui.Run` directly.
- No changing download concurrency, retry count, segment size, resume layout, CDN logic, and fallback behavior together without separate tests for each behavioral contract.
- No deleting completed resume data on a recoverable network error or cancellation.
- No accepting incomplete/oversized Range bodies as completed checkpoints.
- No reducing video resolution solely to obtain H.264.
- No treating a Python executable or model-cache directory alone as a healthy OCR install. `Tools/OCR/runtime/<device>/install.json`, the pinned private runtime, and the embedded worker checksum must agree; worker startup must additionally confirm PP-OCRv6 Small detection + recognition and the requested device.

## Release gates

Run from repo root:

```text
go test ./...
go vet ./...
go test -race ./...
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -H=windowsgui" -o <exe> ./cmd/bilisub
python scripts/validate_release.py <exe>
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
python -u scripts/browser_e2e.py  # legacy parity oracle; not production runtime
```

Any failure blocks release. Native Windows picker/self-update checks are `WINDOWS-REQUIRED`; cross-build success is not proof that their UI/runtime behavior works. See `docs/engineering/RELEASE_ACCEPTANCE.md`.

For the C# + WinUI 3 migration lane, every checkpoint must run `csharp/scripts/verify.ps1` on Windows. Source-complete status does not authorize candidate promotion before a clean Windows compile/publish, contract-test pass, native visual QA and field matrix for the exact published SHA-256.

## UI/UX contract

- `docs/design/UI_GUIDE.md` is the persistent design contract for the embedded web UI.
- UI redesigns are audit-first: scan current behavior, diagnose hierarchy/interaction problems, then make targeted changes. Do not restyle blindly.
- BiliSub is a utility/product UI, not a marketing page. Usability, learnability, state clarity, and direct manipulation outrank visual spectacle.
- New preview-based features (OCR region selection and video cleanup masks) must follow the shared direct-manipulation contract in `docs/design/UI_GUIDE.md`.
- Do not add a new frontend dependency solely for visual novelty without a scoped plan and verification.

## Frontend state ownership

- Critical OCR controls (`startOCR`, `testOCR`, preview controls, stop/clear/export) have exactly one `.disabled` state writer: `ocrSyncControls()`.
- Status refresh, engine readiness, direct/fallback preview changes, and scan transitions update facts only, then call the state owner. They must not independently enable/disable the same controls.
- `refreshAppStatus()` must never use direct `<video>` dimensions/duration to decide OCR readiness because the shared preview may be in FFmpeg frame-fallback mode.
- Native Win32 controls have exactly one `EnableWindow` owner: `internal/nativeui.(*window).syncControls`. Event handlers update facts/validation state and then call `syncControls`; they must not call `enable(...)` directly.
- Bilibili metadata is URL-bound. Editing a Subtitle/Video URL invalidates the old track/quality state immediately, and download must refuse stale metadata from another URL.
- Native OCR/Editor edit fields use strict live validation. Invalid ROI/region/timing keeps corrective fields editable but disables operations that would consume invalid state.

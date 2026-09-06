# Timeline Editor V2 - Current Status

Last updated: 2026-09-06

## Current checkpoint

```text
Program status: PLANNING APPROVED
Current gate: G5 - One-clip parity
Gate state: VERIFIED - V2 one-clip maps onto the classic render pipeline
Timeline implementation: STARTED (shell + domain + migration + one-clip parity)
Classic Editor modifications for Timeline: NONE (V2-only files plus test/verifier/docs)
```

## Approved direction

- Build Timeline Editor V2 separately.
- Keep the classic Editor working and available.
- Merge V2 into the normal Edit tab only after automated parity, Windows field verification and user approval.
- Use the immutable objectives and gates in `MASTER_PLAN.md`.
- Use accepted architecture decisions in `DECISIONS.md`.

## Baseline identity

- Verified source commit: `642bf8d`.
- Annotated tag: `timeline-v2-g0-baseline-20260906`.
- Baseline commits: `81fef4b` OCR, `2ead6ec` media, `9c3a1aa` Editor, `1addd83` verifier/docs, `642bf8d` logging gate follow-up.
- Timeline implementation remains absent from this baseline.
- Production project backup evidence is recorded in `BACKUP_PROCEDURE.md`.

Do not modify or move this tag. Future Timeline work must branch from or remain recoverable to this identity.


## Completed

- [x] Product goal discussed.
- [x] Separate-V2 safety strategy approved by the user.
- [x] MVP scope bounded to one sequential video track and hard cuts.
- [x] Correct visual/audio track order recorded.
- [x] Master plan persisted in the repository.
- [x] Architecture decisions persisted in the repository.
- [x] Resume and stop protocol persisted in the repository.
- [x] Working-tree inspection and four-group classification.
- [x] Diagnostic gates executed on the current dirty tree (see evidence below).
- [x] User approved the four-group baseline strategy and App-bin artifact cleanup.
- [x] Cancellation ownership, source-switch races, relink collision handling and verifier drift fixed.
- [x] Four approved baseline groups committed; one follow-up verifier-only commit records the final logging token refresh.
- [x] Full clean-tree Windows gate passed and baseline tag created.

## G0 baseline checklist

- [x] Inspect and classify every current working-tree change.
- [x] Run permitted diagnostic gates on the dirty tree.
- [x] Produce a known-good Windows x64 executable in an isolated publish.
- [x] Record binary SHA-256 and verification evidence.
- [x] Decide and commit the existing changes in reviewed pre-V2 groups.
- [x] Fix/refresh stale verifiers and the cancellation ownership regression.
- [x] Back up and hash-verify production `Data/Projects` before any migration experiment.
- [x] Obtain explicit user approval before commit/tag and baseline promotion.

## Final G0 verification evidence (2026-09-06)

- Clean-tree `powershell -NoProfile -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1` - PASS.
- Official Python migration, Editor Voice/TTS, logging, OCR and ASR gates - PASS.
- Release x64 solution build - 0 warnings, 0 errors.
- Core contract tests - `108/108 passed`.
- Range field regressions - PASS.
- Self-contained WinUI publish, real startup smoke, worker identity, PE32+ x64 and checksum readback - PASS.
- Version: `4.0.0-beta.70-csharp-p5`.
- `BiliSubStudio.exe` SHA-256: `7d4086987513e7c5512868a51c536dae8d5f61847e706398e194d043dfc02de5`.
- Source tree SHA-256: `fa2dc5888f0335f2d56c2fc1b0df6b833bc3a06efb4187ead5c6a3f3903eda66`.
- App-bin `Data`, `Tools`, `Cache` contamination and `.codex-build` diagnostics were removed before baseline commits.
- Production `Data/Projects` backup: 7 files / 6,366,311 bytes, manifest SHA-256 `a983c21e4adb6646f31370761e91e3abf3cb5db887bba9ba74677406dfa69138`.

## G0 diagnostic evidence (2026-09-06, dirty tree, HEAD 153f97cd)

Passing:

- `python -B csharp/scripts/verify_media_bundle_contract.py` - PASS.
- `python -B csharp/scripts/verify_translation_skill_contract.py` - PASS.
- `python -B csharp/scripts/verify_installer_runtime_layout_contract.py` - PASS.
- `python -B csharp/scripts/verify_ocr_worker_contract.py` - PASS (row compatibility + PaddleOCR 3 contract).
- `python -B csharp/scripts/verify_editor_async_void.py` - PASS (35 async-void all event-owned).
- Core contract tests: `108/108 passed` (includes project catalog/rename/delete, relink, media guards, OCR PTS timebase guards).
- CDN regression, Subtitle regression, CDN failover regression, Range regression - all PASS.
- `dotnet build csharp/BiliSubStudio.sln -c Release -p:Platform=x64` - 0 warnings, 0 errors.
- Isolated publish (`dotnet publish ... -o .codex-build/timeline-g0-baseline/publish`) followed by real WinUI startup smoke with layout walk: `ExitCode=0`, sentinel `PASS`.
- Published exe SHA-256: `F684B3F4CD6DC43C869835EE90DB899F518B8031B64543D886065544EBB0ACBE`.

Startup smoke environmental finding (not a source regression):

- First smoke run from the normal App `bin` output directory exited 0 without sentinel; log showed `TimeoutException` at `App.xaml.cs:47` (20 s init wait).
- Root cause: the App bin directory is contaminated with portable runtime roots - `Data` 14 files / ~87 MB, `Tools` 45,456 files / ~10.4 GB, `Cache` 13,026 files / ~556 MB. Initialization (preview-cache cleanup / storage scan) exceeds the 20 s smoke budget on first run over those roots.
- The same binary published into a clean directory passes the identical smoke. `verify.ps1` always publishes fresh, so the official gate is not exposed to this.
- Resolved before baseline: deleted the contaminated `Data|Tools|Cache` roots inside the App bin output directory (test byproducts, not user production data).

Historical dirty-tree failures, all resolved before the tagged baseline:

1. `verify_editor_voice_preview_contract.py` (VOICE-12) - STALE: expects `_voiceTrack);` terminator; shared request now ends `..., _voiceTrack, Trim: CurrentTrimRange()` (`EditorPage.xaml.cs:1647-1649`). Behavior verified intact (preview slices via `request with`, voice mix preserved).
2. `verify_editor_voice_preview_export_contract.py` (VOICE-14) - STALE: expects literal `medium`/`crf 18` inside `BuildRenderArguments`; export encoding moved to `EditorExportPolicy.BuildVideoEncoderArguments` (`VideoEditorService.cs:581`, `EditorExportSettings.cs:90-149`). Preview still `ultrafast`/23 independently.
3. `verify_editor_voice_reopen_contract.py` (VOICE-15) - STALE: same `_voiceTrack);` terminator; reopen restoration chain verified intact (`EditorPage.xaml.cs:499-526`, store validation `EditorProjectStore.cs:485-716`).
4. `verify_editor_tts_restart_contract.py` - STALE: expects 3-arg `native_record_valid(record, target_frames, natural_sample)`; worker now passes a 4th `minimum_generated_frames` corruption guard (`internal/tts/worker.py:209-225`) - strictly stronger.
5. `validate_csharp_migration.py` config schema drift - STALE: `EXPECTED_JSON_FIELDS` misses `ocr_output_dir` and `asr_execution_mode` which are intentional, contract-tested additions (`AppConfig.cs:13-32`, contract check at `Program.cs:222-230`).
6. `generate_csharp_code_map.py --check` - STALE generated doc (`docs/migration/CSHARP_CODE_MAP.generated.md`) plus a cp1252 console crash when printing the Vietnamese-character diff; needs regeneration and a UTF-8-safe stdout.
7. `verify_ocr_scanner_contract.py` - STALE: requires the old NVDEC `fps={fps}` filter shape; source deliberately removed the fps filter because it rewrites PTS, sampling is now PTS-preserving in C# via `settb` + `-fps_mode passthrough` (`OcrScanner.cs:1363-1380`), covered by passing Core contract tests (OCR timebase PTS exact match and guard).
8. `verify_editor_tts_cancel_contract.py` - GENUINE regression: `PollTtsJobAsync` detaches `_ttsJobId` after 30 s while the TTS job is still non-terminally cancelling (`EditorPage.xaml.cs:994-1000`), and source switching clears tracked IDs after only 8 s (`EditorPage.xaml.cs:1047-1060`). Violates the ownership rule "UI keeps the job ID until cleanup reaches a terminal state"; restart is still blocked by the app-level active-job gate, but the UI loses its owner and may not refresh. Must fix source before baseline promotion.

## G1 verification evidence (2026-09-06)

- Frozen inventory: `docs/editor-timeline/G1_CONTRACTS.md` (35 unique scripts).
- Result: `TOTAL=35 PASS=35 FAIL=0` with `python -B`.
- Six stale static tokens refreshed without weakening behavior:
  `BuildConfiguredRenderArguments`, sliced `renderRequest` ASS ownership,
  export-dialog immutable target, strengthened `subtitleReady` plus
  `cleanupPending`/`trimChanged`, settings `ValidateWritableDirectoryAsync`,
  image `graph.OutputLabel`.
- Classic Editor source: unchanged for Timeline; no schema change.
- G0 baseline tag `timeline-v2-g0-baseline-20260906` remains the recovery point.

## G1 re-verification evidence (2026-09-06, before G2)

- `git diff timeline-v2-g0-baseline-20260906..HEAD --name-only`: only 6 verifier scripts + timeline docs; no `csharp/src` changes.
- G1 frozen inventory re-run: `TOTAL=35 PASS=35 FAIL=0` with `python -B`.
- Release x64 build: 0 warnings, 0 errors; code map current; no `*Timeline*` V2 files present yet.

## G2 verification evidence (2026-09-06)

- Separate namespace `BiliSubStudio.Core.Timeline`: flag, V2 model/store/session,
  immutable `TimelineRenderPlan` + `TimelineCompiler`, preview/export shells.
- Flag `BILISUB_TIMELINE_V2` defaults off; page registers only when enabled.
- `TimelineEditorPage` owns only V2 session state; classic `EditorPage`,
  schema-7 store and `VideoEditorService` untouched.
- Store root is `Temp/TimelineV2` (or injected test root); production
  `Data/Projects` is rejected and every path is prefix-checked; writes are atomic.
- `MainWindow.xaml` unchanged; classic navigation items intact.
- Core contracts: `109/109 passed` (108 + `TimelineV2ShellContract`).
- `verify_timeline_v2_shell_contract.py`: PASS.
- G1 regression re-run after G2: `TOTAL=35 PASS=35 FAIL=0`.
- Release x64 build: 0 warnings, 0 errors; code map regenerated (133 files).
- `validate_csharp_migration.py`: PASS.
- Clean-tree `verify.ps1` on `31b8648`: PASS (contracts 109/109, Range
  regressions, self-contained publish, startup smoke, checksum readback).
- G2 gate binary: exe SHA-256
  `4b6ef2a0b1b8f83144371c66483c115eebb9a893d56e0005f1e8aa73c64e0f59`,
  source tree SHA-256
  `cd97f1f3d4378d0382b25c6042b864b125075f49ee6dab6c59b9b46d0384436c`.

## G3 verification evidence (2026-09-06)

- New domain: `TimelineTime` (int64 microseconds, frame-grid snap, explicit
  source/sequence mappings), `TimelineMediaAsset`, `TimelineClip`,
  `TimelineTrack` (DEC-007 canonical order), `TimelineSequence`
  (first-clip geometry default, explicit FPS, hard-cut validation, reflow).
- Project schema 1 -> 2 with assets/sequence; store rejects old schema files
  instead of guessing geometry/FPS. Test-root isolation and atomic writes kept.
- `TimelineCompiler` resolves ordered clip segments into the shared immutable plan.
- Core contracts: `110/110 passed` (108 + V2 shell + `TimelineDomainContract`
  boundary/property tests incl. 500-case roundtrip/snap fuzz).
- `verify_timeline_domain_contract.py` and `verify_timeline_v2_shell_contract.py`: PASS.
- G1 regression re-run: `TOTAL=35 PASS=35 FAIL=0`.
- `validate_csharp_migration.py`: PASS; code map regenerated.
- PDEC-001/002 follow current recommendations (first-clip geometry, explicit
  FPS); final product approval still required at promotion.
- Clean-tree `verify.ps1` on `c032308`: PASS (contracts 110/110, Range
  regressions, self-contained publish, startup smoke, checksum readback).
- G3 gate binary: exe SHA-256
  `bac8ff3bd1cc44a9c92bb4a590857edd1589f18ca3a785a6ec9e609be8a291c8`,
  source tree SHA-256
  `6d03392fa4bd3cc330666b55b01d6a9976e4e42d423e0aa387f987a1e2a42d97`.

## G4 verification evidence (2026-09-06)

- `TimelineMigrator.MigrateSchema7`: pure in-memory copy from already-loaded
  schema-7 state to a fresh-identity V2 project (schema 2). No IO on the
  source project file, schema-7 store, or media file; classic
  `EditorProjectStore` untouched.
- Copy carries one full-source clip (0..duration, 1x), first-clip geometry,
  explicit FPS, and explicit warnings for flattened regions and dropped
  subtitle/voice. Schema-7 trim/audio are intentionally not copied yet;
  those belong to G5 parity decisions.
- Core contracts: `111/111 passed` (new `TimelineMigrationCopyContract`:
  copy identity/shape/warnings, source bytes unchanged, schema-6 rejected,
  V2 store roundtrip).
- `verify_timeline_migration_contract.py`, G3/G2 verifiers, and
  `validate_csharp_migration.py`: PASS; code map regenerated.
- G1 regression re-run: `TOTAL=35 PASS=35 FAIL=0`.
- Clean-tree `verify.ps1` on `95b9678`: PASS (contracts 111/111, Range
  regressions, self-contained publish, startup smoke, checksum readback).
- G4 gate binary: exe SHA-256
  `bee94d5f556a58e898ea8ddf870ef1bdc03126ab06fa18c6a120aae987650239`,
  source tree SHA-256
  `6d3f9520c69cb321ffab876aa3a183d5417971abaa5420b4d5453b2b1bf3ba96`.

## G5 verification evidence (2026-09-06)

- `TimelineOneClipParity`: one-clip V2 plan maps to the exact classic
  `VideoEditRequest` (input path, dimensions, duration in seconds, source
  trim from microseconds, keep audio, no invented edits). Export range
  equals the classic trim window; a full-clip plan behaves like an
  untrimmed classic request; multi-clip plans are rejected.
- `TimelinePreviewExportService`: separate V2 types/preview root
  (`Temp/TimelineV2/Preview`) delegating to the proven
  `VideoEditorService` — the single FFmpeg graph owner. No duplicated
  filter/argument building, no direct process management.
- Core contracts: `112/112 passed` (new `TimelineParityContract`).
- `verify_timeline_parity_contract.py` plus G2/G3/G4 verifiers and
  `validate_csharp_migration.py`: PASS; code map regenerated.
- G1 regression re-run: `TOTAL=35 PASS=35 FAIL=0`.

## Remaining precondition

- None for starting G6 multi-clip core work. Any future restore or migration experiment must use a separate test root and re-verify `BACKUP_PROCEDURE.md` first.

## Next exact action

Begin G6 by implementing add/reorder/trim/split/delete with dependency and undo tests. Do not modify the classic Editor for Timeline work.

## Required resume command

Use this instruction in a new session:

> Read `docs/editor-timeline/MASTER_PLAN.md`, `docs/editor-timeline/STATUS.md`, and `docs/editor-timeline/DECISIONS.md`. Continue only the current checkpoint, inspect the working tree first, do not expand scope, and do not modify the classic Editor for Timeline work.

# Timeline Editor V2 - Current Status

Last updated: 2026-09-07

## Current checkpoint

```text
Program status: PLANNING APPROVED
Current gate: G12 - Mux
Gate state: IN_PROGRESS - source and dirty-tree gates pass; clean-tree verify pending
Timeline implementation: STARTED (through real FFmpeg mux for video plus source audio; no Edit-tab merge, flag default off)
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
- `TimelinePreviewExportService`: separate V2 request/result types delegating
  one-clip compatibility to the proven `VideoEditorService`, including its
  classic-owned preview cache. G9 later establishes the real V2 proxy root.
  No duplicated filter/argument building or direct process management.
- Core contracts: `112/112 passed` (new `TimelineParityContract`).
- `verify_timeline_parity_contract.py` plus G2/G3/G4 verifiers and
  `validate_csharp_migration.py`: PASS; code map regenerated.
- G1 regression re-run: `TOTAL=35 PASS=35 FAIL=0`.
- Clean-tree `verify.ps1` on `25d7c1e`: PASS (contracts 112/112, Range
  regressions, self-contained publish, startup smoke, checksum readback).
- G5 gate binary: exe SHA-256
  `538be787200bd083bbfd22d52e4d88ef8d475fe65050aca5faf86d310c685cb9`,
  source tree SHA-256
  `951f63968b836ec687bc57f01e77ec49af5546824c5d6fb24fcf0310a51f6686`.

## G6 verification evidence (2026-09-06)

- `TimelineSequenceOps`: pure `Add`/`Reorder`/`Trim`/`Split`/`Delete` over
  immutable clip lists. Every result reflows contiguous hard cuts
  (no overlap, no gap); reorder preserves play order because
  `ReflowContiguous` now follows caller list order instead of re-sorting by
  stale start times (real G6 bug found by contract, fixed in
  `TimelineSequence.cs`).
- Split maps the cut through the rate-aware time model
  (`SequenceToSourceUs`), keeps the left id and mints a new right id with
  advanced SourceIn; undo restores the whole pre-split set.
- `TimelineCommandHistory`: bounded (200) immutable-snapshot undo/redo;
  new commit clears the redo branch; empty history is a no-op.
- Core contracts: `113/113 passed` (new `TimelineSequenceOpsContract`:
  add/reorder/trim/split/delete invariants, 2x-rate split, delete
  determinism, history atomicity/bounds).
- `verify_timeline_sequence_ops_contract.py` plus G2–G5 verifiers and
  `validate_csharp_migration.py`: PASS; code map regenerated.
- G1 regression re-run: `TOTAL=35 PASS=35 FAIL=0`.
- Clean-tree `verify.ps1` on `621031c`: PASS (contracts 113/113, Range
  regressions, self-contained publish, startup smoke, checksum readback).
- G6 gate binary: exe SHA-256
  `d9225700f0fdc78e0e16f7b144fbf310010d9e8e807c41166f0dcd5779761429`,
  source tree SHA-256
  `060dd173079bf9870ecf26684a928e02f35bd5d231cf6632667e99c88802f0f7`.

## G7 verification evidence (2026-09-07)

- `TimelineItem` distinguishes clip-local effect/subtitle/logo ranges from
  sequence-global subtitle/music/logo ranges; all persisted times remain `long`
  microseconds. Local items must reference an existing clip and stay inside
  its duration.
- `TimelineDependencyOps` applies reorder/trim/split/delete to one immutable
  clips-plus-items state. Effects and clip logos crossing a split are cloned
  and clamped; items after the cut are rebased to right-clip local time;
  global items remain unchanged. Signed trim deltas preserve source-content
  ownership for inward/outward trims, including sub-microsecond rounding.
- Subtitle cues crossing a split are rejected by default and can only split
  through explicit `TimelineSubtitleSplitPolicy.SplitAndClamp`.
- `TimelineEditHistory` snapshots clips and items together, so split/delete
  undo and redo restore the complete dependency set atomically.
- Project schema 2 -> 3 persists items. Existing experimental schema-2 files
  load safely with an empty item set; schema 1 remains rejected.
- `TimelineCompiler` is still the sole resolver and now maps clip-local items
  into immutable sequence-time render-plan items. G5 classic parity rejects
  plans containing unsupported G7 items instead of silently dropping them.
- Core contracts: `114/114 passed` (new `TimelineItemsContract`: validation,
  reorder, trim clamp/drop, split ownership, subtitle policy, delete,
  dependency undo/redo, schema-3 persistence/schema-2 upgrade and compiler
  resolution, final-clip deletion and fractional-rate rounding boundaries).
- `verify_timeline_items_contract.py` plus G2-G6 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; no classic Editor source
  or schema-7 store modifications.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1` on `872907e`: PASS (contracts `114/114`, Range
  regressions, self-contained publish, real startup smoke, worker identity,
  PE32+ x64 and checksum readback).
- G7 gate binary: exe SHA-256
  `c5b5933dd917f7465b3494e8c6e6718447914310220bc06b79f883d2b870e8b1`,
  source tree SHA-256
  `a5953d25cf284d2ec1d83fe5ea3babe8f690a260da820cf87f11dc0681ca396e`.

## G8 verification evidence (2026-09-07)

- User accepted DEC-018: MVP has at most one sequence-global music track.
  User accepted DEC-019: persisted/compiled timing is exact microseconds;
  final media drift may not exceed `max(1 sample @ 48 kHz, 1/2 frame)`.
- `TimelineSubtitleCue` owns stable cue identity, spoken text and a
  duration-bound speech revision. `TimelineVoiceCue` links only through the
  cue ID/revision and keeps exact source-frame ranges in a fingerprinted
  voice-master asset; stale/orphan/duplicate links are rejected.
- `TimelineAudioDependencyOps` keeps voice linked through cue moves, reorder
  and non-crossing split. Trim/split through spoken content rejects by default;
  explicit `RemoveAffectedVoice` removes stale links atomically. Delete
  cascades local cues/voice/source audio while preserving global music.
- Every clip has exactly one `TimelineSourceAudioState` with gain `0..2`;
  split clones the gain to the right child. Music has independent gain/source
  frames and remains sequence-global. Legacy schema-3 music is surfaced as
  unverified audio instead of being silently dropped.
- Project schema 3 -> 4 persists subtitle cues, audio assets, source-audio
  states, voice links and music. Schema 2/3 upgrade with explicit unity source
  defaults; mixed old-schema G8 fields are rejected.
- `TimelineCompiler` remains the only resolver and emits immutable subtitle,
  source, voice and music operations. It carries playback rate, 48 kHz output,
  sequence FPS/DEC-019 tolerance, clamps global cue/audio output to video
  duration and derives voice sequence timing from the same resolved cue.
- Schema-7 migration now receives probed `HasAudio`; classic parity rejects
  contradictory source-audio ownership, ranges and clip duration mapping.
- G5 classic parity now rejects non-1x clips and leading sequence gaps rather
  than silently rendering timing it cannot reproduce.
- Core contracts: `116/116 passed` (new `TimelineSubtitleVoiceLinkContract`
  and `TimelineAudioSyncContract`: cue revision/link validity, edit ownership,
  atomic undo/redo, fractional boundaries, source/no-source audio, non-1x
  clips, one music track, tail/empty-sequence clamps, sample conversion fuzz,
  schema-4 roundtrip/schema-3 upgrade, legacy music and deterministic
  recompile).
- `verify_timeline_audio_contract.py` plus G2-G7 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; classic Editor and
  schema-7 store unchanged.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1` on `307b49e`: PASS (contracts `116/116`, Range
  regressions, self-contained publish, real startup smoke, worker identity,
  PE32+ x64 and checksum readback).
- G8 gate binary: exe SHA-256
  `35cc7c1fd6a488591e4fb60c19cc6d9636815739023da83d28b6ec6625952881`,
  source tree SHA-256
  `e79c3f7d9e6e590360446882bcc0b68469c52bc7066e0f8eba0cc90d2d5c1540`.

## G9 verification evidence (2026-09-07)

- `TimelinePreviewController` keeps a monotonic `Revision`, publishes only
  the latest artifact and waits for the previous renderer, process group and
  temporary cleanup barrier before starting a replacement. Cancellation
  toggles revision and disposes the captured CTS under a single lock window
  so cancellation tokens cannot be released mid-cancel.
- `TimelineProcessedPreviewService` is the sole owner of
  `Temp/TimelineV2/Preview` and per-request `OwnedProcessGroup`. It allocates
  a unique `*.rendering.mp4` path, atomic-promotes it on success and always
  cleans up both the rendering and final proxies when the renderer faults or
  the request is cancelled. Path validation rejects any artifact whose full
  path escapes the V2 preview root or whose filename does not match the
  managed prefix.
- Renderer seam `ITimelineProcessedPreviewRenderer` deliberately stays a
  shell: G10 supplies the multi-clip FFmpeg graph, while G9 proves
  publication, serialization, cleanup, path confinement and process reap.
  Renderer failure and full cleanup verified via real `--fixture-spawn-child`
  tree with `ActiveCount == 0` and a reaped nested child.
- `TimelinePreviewExportService`: one-clip G5 compatibility adapter now no
  longer claims the V2 preview root; the G5 verifier checks the overclaim is
  gone. Classic schema-7 store and Editor service remain untouched.
- Core contracts: `117/117 passed` (new `TimelineProcessedPreviewContract`:
  latest-wins serialization, cancellation-resistant stale result deletion,
  failed replacement keeps the current proxy, atomic V2 publication, owned
  process tree reap on cancel).
- `verify_timeline_processed_preview_contract.py` plus G2-G8 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; classic Editor and
  schema-7 store unchanged.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1` on `d546a97`: PASS (contracts `117/117`, Range
  regressions, self-contained publish, real startup smoke, worker identity,
  PE32+ x64 and checksum readback).
- G9 gate binary: exe SHA-256
  `7437e161e8a0bfbde64a17f43c7c036b28ddb427bb341b9fbafc53f9838a230d`,
  source tree SHA-256
  `4217c439c805eaa8542269355e29829115ec917cf5e0f8a6569e2fcaf80b71aa`.

## G10 verification evidence (2026-09-07)

- User accepted DEC-020: G10 verifies the multi-segment export lifecycle
  through the `ITimelineExportSegmentRenderer` seam; the real multi-clip
  FFmpeg mux graph is deferred to a follow-up gate.
- `TimelineRenderPlan` now carries `OutputWidth`/`OutputHeight`/
  `OutputFrameRate` from the sequence plus `ValidateOutputGeometry()`, so the
  export pipeline consumes first-clip geometry and explicit FPS without a
  second resolver (DEC-013).
- `TimelineExportConcatList` owns `Temp/TimelineV2/Export`, writes ffmpeg
  concat-demuxer `file '…'` lists in compiler clip order and rejects
  out-of-order/short segment sets; per-job cleanup removes only
  `timeline-segment-<jobId>-*` and matching list files.
- `TimelineExportService.ExportAsync` enforces `CleanupAwareCancel=true`,
  renders one normalized segment per clip (`*.rendering.mp4` → atomic move),
  links job + caller cancellation tokens, reaps its `OwnedProcessGroup` and
  deletes segment/concat/final artifacts on cancel or failure.
- `TimelinePreviewExportService`: G5 one-clip adapter keeps its own
  `TimelineOneClipExportResultV2` shape; the G10 `TimelineExportResultV2`
  carries output path, job id, segment count and duration.
- Core contracts: `118/118 passed` (new `TimelineExportContract`: mixed-format
  plan geometry, concat-list order, cleanup-aware cancel/success/failure).
- `verify_timeline_export_contract.py` plus G2-G9 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; classic Editor and
  schema-7 store unchanged.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1` on `65425f1`: PASS (contracts `118/118`, Range
  regressions, self-contained publish, real startup smoke, worker identity,
  PE32+ x64 and checksum readback).
- G10 gate binary: exe SHA-256
  `de0ba59c25271b8ecbfc9a09fad58af8652c9943cd1628604376062b233939c8`,
  source tree SHA-256
  `d17e853a4f3696d55958cf135a3f1de981158407f703d72ab1a713c9f73eb3cd`.

## G11 verification evidence (2026-09-07)

- User accepted readiness-only scope (no Edit-tab merge, no flag default
  change) and DEC-021: classic Editor stays available for at least two
  verified public builds after promotion; PDEC-006 resolved.
- `TimelinePromotionService` copies one real schema-7 project into the
  isolated V2 store behind a backup gate (`BackupTag` + 64-hex manifest SHA
  required up front). Source JSON/media are SHA-256 snapshotted before and
  after; any drift throws. The service writes nothing itself; the only writer
  is `TimelineProjectStoreV2`. Missing media resolves to relink guidance.
- Shared additive probes only: `MediaPreviewService.ProbeHasAudioAsync`
  (audio-stream presence, existing probe semantics unchanged) and
  `BiliSubApplication.GetEditorProjectPath` (read-only schema-7 path facade).
- Copy entry lives only in the flag-gated V2 dev page (classic dropdown +
  backup-tag/manifest inputs + warnings display). `EditorPage`,
  `MainWindow.xaml` and classic navigation are untouched; flag defaults off.
- Core contracts: `119/119 passed` (new `TimelinePromotionContract`:
  real-shaped copy with source preservation, backup-gate rejection,
  non-schema-7 rejection).
- `verify_timeline_promotion_contract.py` plus G2-G10 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; classic Editor and
  schema-7 store unchanged.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1` on `0e570f3`: PASS (contracts `119/119`, Range
  regressions, self-contained publish, real startup smoke, worker identity,
  PE32+ x64 and checksum readback).
- G11 gate binary: exe SHA-256
  `a21c3ca827301780b0676b0c7a7b4920248727a5abbd560c133b1aa28cfdf857`,
  source tree SHA-256
  `30af182670443b7190422ee67ee656ab6752ac2817e6400156c83b1860e9d174`.

## G12 pre-commit verification evidence (2026-09-07)

- User accepted DEC-022: real multi-clip mux for video plus clip source audio
  only; items, subtitle/voice cues and music reject explicitly until ported.
- `TimelineFfmpegSegmentRenderer` renders normalized H.264/AAC segments
  (scale/setsar/fps/yuv420p, setpts/atempo rate, 48 kHz, per-clip gain;
  generated silence for silent clips so concat inputs stay uniform).
  Windowing lives inside the filtergraph (`trim`/`atrim`): output `-ss/-t`
  cannot bound an infinite lavfi silence source and previously left a
  full-window audio tail after atempo (real field bug found by contract).
- `TimelineFfmpegConcatenator` joins segments with the concat demuxer
  (`-c copy`, `+faststart`), probes the mux and validates geometry, codec,
  FPS, 48 kHz audio, total duration and A/V drift against a DEC-019-derived
  field bound. FFmpeg paths stay explicit constructor inputs (no ToolManager
  coupling, no bundle download in tests).
- `TimelineExportService` now drives the concatenator seam, deletes owned
  segment files after a validated move, and keeps all G10 lifecycle tokens.
- Core contracts: `120/120 passed` (new `TimelineMuxContract`: exact argv,
  capability matrix, validation math, real mixed-format field mux with color
  order proof on PATH-resolved FFmpeg).
- `verify_timeline_mux_contract.py` plus G2-G11 verifiers: PASS.
- G1 frozen classic Editor contracts: `35/35 PASS`; classic Editor and
  schema-7 store unchanged.
- Release x64 build: 0 warnings, 0 errors; Range field regressions PASS;
  `validate_csharp_migration.py` PASS; generated code map current.
- Clean-tree `verify.ps1`, self-contained publish/startup smoke and final G12
  binary/source hashes: PENDING until the G12 source is committed.

## Remaining precondition

- Commit the reviewed G12 source and run clean-tree `csharp/scripts/verify.ps1`
  before changing the gate state to `VERIFIED`. The field mux contract needs
  FFmpeg/FFprobe on PATH.
- Any future restore or migration experiment must use a separate test root and
  re-verify `BACKUP_PROCEDURE.md` first.

## Next exact action

Review and commit the G12 source, run the clean-tree Windows verification gate,
then record binary/source hashes. Do not modify the classic Editor for
Timeline work.

## Required resume command

Use this instruction in a new session:

> Read `docs/editor-timeline/MASTER_PLAN.md`, `docs/editor-timeline/STATUS.md`, and `docs/editor-timeline/DECISIONS.md`. Continue only the current checkpoint, inspect the working tree first, do not expand scope, and do not modify the classic Editor for Timeline work.

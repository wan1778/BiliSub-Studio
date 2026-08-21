# RC13 implementation ledger

## Pinned source baseline

- Release baseline: BiliSub Studio v4.0.0-beta.12 RC12 Windows Test
- Upstream RC12 commit recorded by release: `c7da66add544748eaadc618f2f9e8d5c8aaf15ca`
- Source archive: `BiliSubStudio_source_v4.0.0-beta.12-RC12.zip`
- Architectural objective: replace RC12 request-level OCR micro-batching as the primary performance strategy with deterministic N-lane parallel segment scanning backed by a dynamic shared OCR worker pool.

## Non-negotiable invariants

1. Subtitle correctness outranks throughput.
2. `subtitleTracker` remains lane-local and is not made concurrent.
3. Core segment ownership is deterministic; pre/post-roll are context only.
4. OCR candidates are never dropped to increase speed.
5. Pause completes only after every active lane reaches a tracker-safe state and schema-4 checkpoint data is fsynced.
6. Resume must be deterministic after app restart.
7. Auto calibrates concurrency, then locks the selected topology for that scan/checkpoint.
8. PP-OCRv6 Small detection + recognition and PaddleOCR 3.7.0 remain unchanged.
9. No OCR v2, ONNX, TensorRT, HPI, or detector removal.
10. CODE_MAP and OCR_CALL_MAP are regenerated at every phase that changes production paths.

## Phase plan

- RC13-A: parallelism contract + segment topology + tests
- RC13-B: dynamic shared OCR worker pool + tests
- RC13-C: range/lane runner + FFmpeg bounded segment decode + tests
- RC13-D: coordinator + core ownership + boundary reconciliation + deterministic validation
- RC13-E: checkpoint schema 4 + all-lane pause barrier + resume
- RC13-F: Auto end-to-end calibration + resource/duration guards
- RC13-G: frontend selector/progress/telemetry + compatibility cleanup
- RC13-H: full regression, race/vet/UI/maps/E2E/Windows build/release validation

## RC13 field hotfix — Auto calibration hang

Observed on Windows field test (2026-08-20): Auto reached `Đang đo 16 luồng quét OCR trên video thật...`, system CPU/GPU/RAM activity then fell back near idle, while the job remained stuck in calibration.

Pinned pre-fix source: `1bdd8bcfd5b6606fde3536ca4ab6ebc30e46dd3d`.

Root call path:
`Scanner.Run -> selectAutoParallelism -> ConfigureScanWorkers(level) -> benchmarkParallelLevel -> N lane goroutines -> blocking receive from outcome channel`.

Root cause / risks to fix together:
1. `benchmarkParallelLevel` waits for exactly N outcomes with an unconditional channel receive, so one lane that never returns can hang Auto forever.
2. After a timed-out/failed level, shrinking the OCR pool through `ConfigureScanWorkers` can itself wait indefinitely for a busy worker when called with the unbounded parent scan context.
3. A failed high level must never discard the last known-good lower level or leave high-level worker/FFmpeg activity orphaned.

Hotfix invariants:
- hard timeout per Auto benchmark level;
- cancellation is propagated to every calibration lane;
- outcome collection selects on context cancellation/deadline rather than blocking receive;
- bounded worker-pool restore after every calibration sequence;
- timeout at a higher level falls back to the best previously completed level;
- if pool restore cannot complete within its bounded grace period, fail the scan clearly rather than hang;
- no changes to tracker, OCR thresholds, segment ownership, checkpoint semantics, Paddle models, detector, or normal manual 1/2/4/8/16 scan behavior.

Release-gate note: full `-race` exposed an existing timing-sensitive Hybrid concurrency assertion. The production path was unchanged; the regression fake now uses a bounded two-call barrier so the test deterministically proves concurrent entry instead of relying on scheduler timing. Twenty consecutive targeted race runs passed before resuming the full gate.

## RC13-HF2 — preflight resource gate before Auto expansion

Pinned HF1 source package commit: `8b357ff5c5485727bf8ab4a071b987571e583611`.

Field-policy correction after HF1: the watchdog prevented an infinite hang but Auto could still create the next worker level before checking whether the previous benchmark showed enough machine headroom. HF2 changes the control order to `measure current -> resource preflight -> configure next -> bounded benchmark -> commit`.

Production symbols added/changed:
- `Scanner.selectAutoParallelism`: resource gate executes before `ConfigureScanWorkers(next)`;
- `Scanner.benchmarkParallelLevel`: returns benchmark resource telemetry in addition to throughput;
- `startAutoResourceSampler`: samples calibration resource use;
- `evaluateAutoResourceGate`: pure safety/prediction policy;
- `probePlatformResources`: Windows kernel32 RAM/CPU telemetry;
- `probeNVIDIAResources`: NVIDIA VRAM/GPU telemetry through existing `nvidia-smi`.

HF2 invariants:
1. no next-level worker is created before the resource gate permits it;
2. memory prediction preserves explicit RAM/VRAM safety margins;
3. CPU/GPU decisions use benchmark averages rather than one transient peak;
4. unknown telemetry retains HF1's bounded watchdog path;
5. manual 1/2/4/8/16 behavior is unchanged;
6. tracker/checkpoint/detector/OCR threshold paths are untouched;
7. full release gate and exact Windows field test remain mandatory before Drive promotion.

## RC13-HF3 — repeated short-Latin OCR garbage correctness hotfix

Windows field test after HF2 confirmed that standalone OCR fragments such as `A` and `N` could reappear as recorded subtitle cues. The tracker implementation was unchanged from RC12: short ASCII text required extra confirmations, but repeated noise could still accumulate enough hits to be promoted. Parallel segment scanning increases the number of independent tracker warm-up regions, making this old weakness easier to surface.

Root call path:
`Paddle OCR result -> lane-local subtitleTracker.Observe -> candidate confirmation -> promoteCandidate -> lane cue -> core ownership -> BoundaryReconciler -> live/final cues`.

HF3 policy for the Chinese OCR export path:
1. standalone 1-3 Latin letters (`A`, `N`, `W`, `OV`, ...) are treated as inconclusive OCR noise, not as empty text;
2. they cannot create a candidate, replace/cancel a real candidate, or close an active Chinese cue;
3. `commitActive` rejects the same shape defensively for restored legacy state;
4. tracker restore removes such committed/active cues from older schema-3/schema-4 checkpoints;
5. `BoundaryReconciler` applies the same final-output guard so stale HF2 checkpoint cues cannot leak into live/final SRT;
6. digits, mixed CJK+Latin text, and Latin text longer than three letters remain eligible.

No OCR threshold, detector/model, lane topology, Auto resource gate, NVDEC path, checkpoint schema, pause barrier, or worker-pool scheduling semantics are changed by HF3.


## Native Windows migration finalization — checkpoint inspection and telemetry gate

Final native migration acceptance adds explicit checkpoint/telemetry parity before release:

1. Native checkpoint inspection exposes schema 3/4 and preserves the distinction between aggregate work progress and the contiguous safe media frontier.
2. Paused OCR UI refreshes from the fsynced checkpoint, so recent cues are frontier-aligned even when parallel lanes have processed later segments.
3. Cue list count is rendered in a separate native label; rendering cues can no longer overwrite the telemetry panel.
4. Live/final/checkpoint telemetry share one formatter and cover OCR images/inferences, frames, images-per-cue, batch average, lane topology, boundary merges, visual skip/confirm/retry, decoder, cumulative timing, elapsed/realtime speed, Auto benchmark fields, and latest confidence/text when available.
5. Release acceptance now includes native feature-parity, application-boundary and dependency/process audits in addition to UI/GPU/call-map/browser/Windows gates.

### Final native migration matrix-1 checkpoint

The synchronized working tree passed the complete automated release matrix after checkpoint/telemetry parity was added. The first attempted combined matrix correctly stopped on stale generated maps; CODE_MAP and OCR_CALL_MAP were regenerated, documentation/release audits were synchronized, and the full matrix was rerun from the start. The successful run includes test/vet/race, native/UI/feature/GPU/application/dependency-process audits, both generated-map checks, browser parity E2E, Windows amd64 cross-test/build, and native-only release static validation. An exact clean-commit rerun remains mandatory before any EXE is packaged for field testing.

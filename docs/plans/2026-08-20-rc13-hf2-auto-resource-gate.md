# RC13-HF2 Auto resource preflight

## Trigger

Windows field test showed HF1 needs an earlier safety layer: Auto must decide whether the machine can safely try the next parallel level before creating that level's workers/lanes.

## Call path

`Scanner.Run -> selectAutoParallelism -> benchmarkParallelLevel(current) -> resource sampler -> evaluateAutoResourceGate(current,next) -> ConfigureScanWorkers(next) only when allowed -> bounded benchmark -> commit/fallback`.

## Scoped changes

- Native Windows physical RAM and CPU telemetry.
- NVIDIA VRAM and GPU utilization via the existing `nvidia-smi` dependency.
- Resource sampling during each real-video Auto benchmark level.
- Predictive RAM/VRAM safety gate and CPU/GPU saturation gate before next-level worker expansion.
- Preserve HF1 benchmark/pool timeouts and hard reset as second-line recovery.

## Explicitly unchanged

- subtitleTracker and visual-confirmation rules.
- OCR thresholds, PP-OCRv6 Small detector/recognizer, PaddleOCR 3.7.0.
- Segment topology/ownership/reconciler.
- Checkpoint schema 4 and schema-3 legacy resume.
- Manual 1/2/4/8/16 semantics.
- NVDEC path.

## Regression gates

Unit tests for each resource stop reason and healthy/unknown telemetry, full OCR tests, full `go test`, `go vet`, `-race`, UI contract, generated CODE_MAP/OCR_CALL_MAP, browser E2E, Windows cross-test/build and release validation. Windows field test remains required for native telemetry correctness and Auto selection quality.

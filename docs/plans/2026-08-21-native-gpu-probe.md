# Phase 13 — Native NVIDIA probe, no external GPU CLI

## Goal

Remove the production dependency on the NVIDIA command-line utility while preserving GPU OCR device selection and Auto parallel resource gating.

## Production call path

- `ocr.Manager.RefreshCapabilities` -> `detectNVIDIAGPU` -> Windows CUDA Driver API (`nvcuda.dll`).
- `Scanner` Auto resource sampler -> `probeNVIDIAResources` -> Windows NVML driver library (`nvml.dll`).

No helper executable is spawned for GPU discovery, VRAM telemetry, or GPU-utilization telemetry.

## Compatibility policy

- CUDA driver API `< 11.8`: GPU Paddle runtime is rejected.
- CUDA driver API `11.8 .. < 12.6`: use pinned cu118 Paddle wheel.
- CUDA driver API `>= 12.6`: use pinned cu126 Paddle wheel.
- NVML is optional telemetry. If NVML is unavailable, CPU/RAM telemetry and the existing bounded benchmark watchdog remain authoritative safety fallbacks; this does not disable a CUDA GPU proven usable by `nvcuda.dll`.

## Gates

- OCR unit tests + Windows cross-compile.
- Full Go tests, vet and race.
- static standalone GPU audit.
- UI contract, CODE_MAP and OCR_CALL_MAP.
- legacy browser E2E oracle.
- Windows x64 cross-test/build and release static validator.

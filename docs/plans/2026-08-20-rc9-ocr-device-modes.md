# RC9 OCR device modes

Baseline: BiliSub Studio v4.0.0-beta.12 RC8 source archive supplied by the user.

## Goal
Add a user-selectable OCR compute mode without creating a second OCR feature:
- Auto: prefer NVIDIA GPU, fallback to CPU.
- GPU: require a usable NVIDIA GPU runtime.
- CPU: preserve RC8 CPU behavior.
- CPU + GPU: install/start separate CPU and GPU workers and permit concurrent OCR execution while preserving ordered subtitle tracking/checkpoints.

## Execution path
web OCR device selector
-> POST /api/ocr/engine/ensure {device}
-> api.Server.ocrEnsureHandler
-> ocr.Manager.ConfigureDevice
-> ocr.Manager.Ensure
-> install/runtime selection
-> worker.py --device cpu|gpu:0
-> Scanner.Run / Manager.Run
-> subtitleTracker -> checkpoint -> cues/SRT

## Constraints
- One OCR page/subsystem only.
- No system Python, PATH, manual CUDA or pip steps.
- Separate CPU/GPU virtual environments; never mix paddlepaddle and paddlepaddle-gpu in one venv.
- GPU package remains PaddlePaddle 3.2.0, PaddleOCR remains 3.7.0, PP-OCRv6 Small remains pinned.
- Windows GPU runtime uses official PaddlePaddle GPU wheels; GPU availability is validated by worker startup, not by nvidia-smi alone.
- Auto must fallback to CPU if GPU install/start fails.
- Explicit GPU/Hybrid must report failure rather than silently lie about device.
- Hybrid results must be committed in timestamp order before subtitleTracker/checkpoint mutation.

## Release gate
Run all AGENTS.md gates. Do not publish to Drive until the exact Windows RC binary passes field acceptance.

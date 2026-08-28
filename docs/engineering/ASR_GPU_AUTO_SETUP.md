# ASR private GPU setup

## Behavior

On the first ASR job that can attempt NVIDIA GPU inference, the application downloads its own GPU runtime. Installing/opening the app alone does not download these large packages. No OCR installation or user-installed CUDA Toolkit is needed. CUDA libraries are not NVIDIA drivers: the user must already have a working NVIDIA driver reporting CUDA 12.8 or newer. This conservative gate matches the pinned CTranslate2 Windows build; older/unknown drivers or unavailable NVIDIA hardware use CPU with an explanation. When reported free VRAM is below 1,500 MiB, the app also skips GPU preparation.

Downloads total 1,335,067,632 bytes (about 1.3 GB), plus disk space for extracted DLLs. They live under `Tools/ASR/gpu`, not the OCR/Piper venv, system directories or global PATH. An interrupted download keeps its `.partial`; SHA mismatch is rejected. Verified archives are retained for repair without another download. Each successful extraction has its own generation directory and atomic manifest; old generations are retained to avoid replacing DLLs used by running workers. Cached files are hash-checked before reuse.

`worker.py` registers only the prepared paths for CUDA requests, keeps directory/DLL handles alive, and preloads the pinned CUDA runtime, cuBLAS and cuDNN before importing faster-whisper. The same paths are passed to the actual transcription after the existing real-audio GPU probe succeeds. CPU fallback runs in a separate worker without GPU paths. Failure to download, load or execute on GPU logs the reason and falls back to CPU/int8; cancellation is propagated instead of starting CPU work.

## Pinned artifact provenance

All URLs, byte counts and SHA-256 values were read from the official versioned PyPI JSON metadata. DLL and license entry names were inspected from the Windows wheel ZIP directory without installing or executing them.

| NVIDIA package | Version | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| nvidia-cuda-runtime-cu12 | 12.8.90 | 944318 | c0c6027f01505bfed6c3b21ec546f69c687689aad5f1a377554bc6ca4aa993a8 |
| nvidia-cublas-cu12 | 12.8.4.1 | 567544208 | 47e9b82132fa8d2b4944e708049229601448aaad7e6f296f630f2d1a32de35af |
| nvidia-cuda-nvrtc-cu12 | 12.8.93 | 73586838 | 7a4b6b2904850fe78e0bd179c4b655c404d4bb799ef03ddc60804247099ae909 |
| nvidia-cudnn-cu12 | 9.10.2.21 | 692992268 | c6288de7d63e6cf62988f0923f96dc339cea362decb1bf5b3141883392a7d65e |

Sources: [CTranslate2 4.8.1 Windows build dependencies](https://github.com/OpenNMT/CTranslate2/blob/v4.8.1/python/tools/prepare_build_environment_windows.sh), [cuDNN 9.10.2 support matrix](https://docs.nvidia.com/deeplearning/cudnn/backend/v9.10.2/reference/support-matrix.html), [CUDA runtime metadata](https://pypi.org/pypi/nvidia-cuda-runtime-cu12/12.8.90/json), [cuBLAS metadata](https://pypi.org/pypi/nvidia-cublas-cu12/12.8.4.1/json), [NVRTC metadata](https://pypi.org/pypi/nvidia-cuda-nvrtc-cu12/12.8.93/json), [cuDNN metadata](https://pypi.org/pypi/nvidia-cudnn-cu12/9.10.2.21/json).

## Validation status for this change

Source implementation and regression definitions only. **Build, automated tests, GPU inference and UI/field tests were NOT RUN**, as explicitly requested by the user. No GPU runtime PASS is claimed. No release, version bump or modification of existing installed app payloads was performed.

For later user-authorized validation: clean-machine download; repeat/offline reuse; missing/corrupt DLL repair; cancel/resume download; absent/old NVIDIA driver; low VRAM; actual `CUDA/float16` or `CUDA/int8_float16` speech probe and full ASR; CPU fallback without CUDA; paths containing spaces; and existing ASR checkpoints. `EditorAsrGpuContract` covers packaging/cache and numeric driver policy offline, but is not a substitute for those field cases.

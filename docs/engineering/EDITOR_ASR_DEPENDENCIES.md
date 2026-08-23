# Editor local ASR dependencies

This file records the reviewed pins used by the optional video-to-Chinese-SRT path. None of the runtime/model files below are embedded in the installer; they are downloaded only when the user starts ASR.

| Component | Pin | Bytes | SHA-256 | Source/license |
|---|---:|---:|---|---|
| faster-whisper wheel | `1.2.1` / universal wheel | 1,118,909 | `79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7` | PyPI release from `SYSTRAN/faster-whisper`; MIT |
| CTranslate2 Windows x64 wheel | `4.8.1` / CPython 3.12 | 19,220,789 | `49f96e861b57301f0b76a082109bde2cac8204a6b4fedc870883008271e82251` | PyPI release from OpenNMT/CTranslate2; MIT |
| faster-whisper small config | model revision `536b0662742c02347bc0e980a01041f333bce120` | 2,370 | `b55496ac7940a7ae47d2c01eab40edfd8701feec1229d9cce3b40014383fb828` | `Systran/faster-whisper-small` converted multilingual Whisper model |
| faster-whisper small weights | same immutable revision | 483,546,902 | `3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671` | Hugging Face LFS object at the pinned revision |
| tokenizer | same immutable revision | 2,203,239 | `fb7b63191e9bb045082c79fd742a3106a12c99513ab30df4a0d47fa6cb6fd0ab` | same model repository |
| vocabulary | same immutable revision | 459,861 | `34ce3fe1c5041027b3f8d42912270993f986dbc4bb34cf27f951e34a1e453913` | same model repository |

Runtime contract:

- the two direct Python wheels include immutable `#sha256=` URL fragments and their imported versions are verified after installation;
- the private ASR venv reuses only the exact-patch Python bootstrap proven against Windows error 448; it does not use the OCR venv;
- every model file must match its exact byte length and SHA-256 before a verified stamp is written;
- the worker sets Hugging Face/Transformers offline mode and loads an absolute local model directory with `local_files_only=True`;
- `.gitattributes` forces LF for both Python workers, so the packaged worker hash is byte-identical to the reviewed Git blob even on Windows checkout;
- the app extracts a real bounded audio sample and completes a CUDA or CPU benchmark before starting full transcription;
- all FFmpeg/Python children are in an owned process group; cancellation waits for cleanup while the atomic cue checkpoint remains resumable.

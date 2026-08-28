# Optional CPU + GPU ASR

## Controls and selection

The Voice inspector offers `GPU ưu tiên (mặc định)`, `CPU`, and `Hybrid · CPU + GPU` for Whisper speech timing. The mode is saved as `asr_execution_mode` when ASR starts, and restored after configuration loads. Missing/unknown settings retain the GPU-preferred default. OCR, Vietnamese SRT import and NGHI/Piper whole-cue synthesis are unchanged.

`Phân tích nhịp theo chế độ đã chọn` starts ASR even if timing already exists, without automatically starting TTS. Valid checkpoints may be reused: this is not a forced cache wipe or performance-comparison button. Selecting a mode alone does not invalidate timing/voice. Successful reanalysis invalidates dependent voice through the existing project flow. Errors/cancellation preserve previous project references; each new analysis has a unique path so interrupted handoff cannot overwrite a previously referenced analysis.

- CPU skips GPU preparation and probes CPU/int8.
- GPU-preferred retains private CUDA preparation, real-audio probe and CPU fallback.
- Hybrid shares the NVIDIA driver/VRAM gates and private CUDA libraries. It additionally requires at least six logical processors and 3 GiB available RAM. These are admission checks, not a memory guarantee.
- The Hybrid probe splits a bounded audio sample between both devices. Completion must report work on both CPU and GPU and a real-time factor at most 1.5. Rejection/failure tries GPU alone, then CPU under the existing policy, with explicit logs. Cancellation never starts fallback work.
- This short probe is a readiness/slow-path gate, **not proof Hybrid beats GPU alone**. Mid-transcription failure stops and preserves committed chunks; it does not silently switch devices or omit failed chunks.

## Worker and seams

One owned Python process creates two independent Whisper models, CUDA `float16`/`int8_float16` and CPU `int8`. Each loads once per worker and is reused by one task at a time. The GPU model gets two CPU threads; the CPU model gets at most eight and leaves four logical processors outside that configured limit. Preprocessing/libraries can use additional threads; this is not process-wide CPU affinity.

Two executor threads dynamically take the next chunk, not a fixed 50/50 split. Four-chunk lookahead bounds out-of-order results when a device is slower. Short audio may have only one production chunk and no parallel speed gain; model loading and overlap also add overhead.

Production chunks target 60 seconds, preferring at least 300 ms of low energy within three seconds of the nominal cut. Two seconds of neighboring audio provide context. Word reconciliation keeps a shared contemporaneous anchor once; absent an anchor, half-open midpoint ownership defines the seam. Text is rebuilt from retained words with raw spacing. Conflicting/missing timings fail the uncommitted chunk instead of silently dropping words. This heuristic cannot guarantee error-free recognition or deduplication: repeated short words, continuous/noisy speech and resume seams require field review.

Threaded calls use [CTranslate2's GIL-releasing inference](https://opennmt.net/CTranslate2/parallel.html); NumPy input and word timestamps follow pinned [faster-whisper 1.2.1](https://github.com/SYSTRAN/faster-whisper/blob/v1.2.1/faster_whisper/transcribe.py). Models remain local/offline; no new model artifact or CUDA package is introduced.

## Checkpoints and cancellation

Hybrid saves `Data/Projects/ASR/<project-id>.hybrid-v1.json`, keyed by source identity plus `:hybrid-word-seam-v1`, separate from single-device checkpoints. Segment events are staged until a sequential `chunk_complete` validates the start/frontier. Only then are words and frontier saved atomically, including silent chunks. The terminal event must match committed chunk/segment/word counts. Resume reads two seconds of left context and excludes committed words via `--core-start`.

Cancellation retains durable checkpoints, discards incomplete staged results and waits for owned Python/FFmpeg processes before temporary audio cleanup. Both inference threads belong to the same Python process. Single-device fallback uses its own checkpoint, never mixing formats.

Speech/project metadata accepts `device: hybrid` and compute `<GPU compute>+int8`. Vietnamese SRT mapping, preview, master rendering and one Piper synthesis per whole cue remain unchanged. ASR does not rewrite imported SRT timecodes.

## Validation status

Implementation and regression definitions only. **Build, automated tests, actual inference and UI tests were NOT RUN**, per the user's instruction to self-test. No runtime, quality or speed PASS is claimed. No installed app payload was updated; testing requires a subsequent authorized build.

Definitions for later authorized execution: `verify_asr_hybrid_contract.py` covers seam ownership, repeated words, silence, text rebuilding, conflicting timings, scheduling bounds/model reuse and checkpoint/UI wiring. `EditorAsrGpuContract` adds Hybrid arguments, config normalization and verified analysis reopen. These do not substitute for real CPU/GPU concurrency or timing evidence.

Later field cases: both devices working; elapsed time versus GPU-only on identical uncached input; short/long audio; noisy/continuous speech and repeated words at seams; CPU slow tail; memory pressure; missing CUDA/driver; explicit CPU without GPU download; cancel/resume; checkpoint reopen; mode changes with existing Vietnamese SRT/voice; preview and regenerated master. Voice quality remains **WAITING FOR USER FIELD TEST**.

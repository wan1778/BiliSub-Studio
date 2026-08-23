# Editor All-in-One for Chinese film localization

Status: M1-M3 foundations are implemented and the M4 production path is now being completed on the draft Editor branch: Whisper word/pause timing, lightweight male/female voice routing, local NghiTTS/Piper voice generation, timing fit, karaoke ASS, cache persistence, and shared preview/export TTS mixing. The owner explicitly deferred incremental field testing until the requested Editor branch is complete. Windows candidate gates remain mandatory for every checkpoint; this does not authorize a merge or public release.

> **Owner revision (binding, 2026-08-23):** Whisper is a timing/rhythm analyzer even when an SRT already exists; its fallback Chinese SRT is secondary. Persistent speaker diarization/pyannote is out of scope. Voice routing only needs `male_like` / `female_like` / `uncertain` with confidence and manual override. Required TTS is local/free NghiTTS-compatible Piper ONNX. Edge/paid APIs are not production dependencies. Demucs/stem separation is removed. Source audio remains Keep/Duck/Mute and generated TTS must use the exact same processed-preview/final-render graph.

Date: 2026-08-23

## 1. Product decision

The Editor is a guided Chinese-film localization workstation, not a generic nonlinear video editor.

It must support two source paths:

1. Video only: Whisper timing/word analysis -> fallback Chinese cues when needed -> Vietnamese translation -> male/female voice routing -> local Vietnamese TTS -> karaoke/subtitle/audio mix -> final render.
2. Video plus source SRT: import and validate SRT -> Whisper timing/word analysis without replacing the SRT -> Vietnamese translation -> male/female voice routing -> local Vietnamese TTS -> karaoke/subtitle/audio mix -> final render.

The existing Blur/Mosaic/Cover region editor remains part of the workflow for masking burned-in source subtitles and watermarks.

## 2. Binding constraints

- Production remains C# + .NET 10 + WinUI 3 on Windows x64.
- No browser UI, WebView, localhost BiliSub backend, Go production path, or second BiliSub executable.
- The user must not install system Python, pip, PowerShell modules, FFmpeg, or model dependencies.
- Optional AI runtimes are app-managed, versioned, checksum-verified, and isolated under `Tools`.
- Local/offline processing is the default because paid API budget cannot be assumed.
- The current Editor localization path has no paid/required API dependency; translation, timing analysis and TTS are local/app-managed.
- Video commonly exceeds four hours. Every long stage is resumable and bounded in RAM/VRAM/disk use.
- Do not dump a full video's frames to `%TEMP%` or the system drive.
- Project data, artifacts, models, cache, and temporary output stay in the app-owned same-drive roots.
- Cancel stops and reaps every owned child process tree. Completed stages remain recoverable; only the current partial artifact is removed.
- Source media is never overwritten. Final output is rendered to a temporary file, validated, then atomically promoted.
- Correctness and recoverability outrank peak throughput.
- Every production change updates call/data maps, adds regression coverage, passes Windows CI/package gates, and then passes the exact real-machine field scenario before public release.

## 3. Historical source audit and resolved root cause

The migration baseline that started this branch was only a thin region/export surface:

```text
EditorPage
  -> MediaPreviewService: ffprobe + one raw frame
  -> in-memory List<EditRegion>
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.BuildFilter
  -> FFmpeg Blur/Mosaic/Cover render
```

That baseline did not own a persisted project, subtitle document, translation pipeline, timing analysis, TTS cache, or resumable localization checkpoint. It also omitted selected-region editing, Undo, presets, and processed-frame effect preview.

The root cause was architectural, not one missing button: the migration preserved the final FFmpeg exporter but had not ported an Editor domain model or a resumable localization pipeline. M1-M4 on this draft branch now add those ownership boundaries; the branch remains build/field gated until the complete Editor scenario passes.

## 4. Corrections to the proposed technology assumptions

### 4.1 Whisper timing and fallback ASR

Use the app-managed `faster-whisper` worker as the local timing analyzer. It runs on the source audio whether the project imported an SRT or not. The worker preserves word-level timestamps, VAD-derived speech regions, pauses, confidence and source offsets. Imported SRT text/timecodes remain authoritative; Whisper-generated Chinese SRT is only the fallback path when no source SRT exists.

The C# boundary must persist the worker's word records instead of collapsing them to segment timing. `EditorSpeechAnalysisDocument` maps source-audio words and pauses onto stable SRT cue IDs so karaoke and TTS fitting reuse the same timing evidence after restart.

### 4.2 Lightweight voice routing, not speaker identity

Persistent speaker identity is not required. There is no pyannote/diarization dependency. Each usable dialogue region receives only an advisory acoustic class:

- `male_like`;
- `female_like`;
- `uncertain`.

The suggestion carries confidence and pitch evidence. A per-cue manual male/female override is authoritative and survives project reopen. The UI must not present the acoustic class as a factual biological-sex claim.

### 4.3 Source audio policy; no stem separation

The product does not attempt to remove only Chinese dialogue while preserving music/effects. Demucs/stem separation is out of scope.

The exact audio modes are:

1. `Giữ nguyên`: keep the complete source mix and add TTS.
2. `Giảm âm lượng`: duck the complete source mix and add TTS.
3. `Tắt tiếng gốc`: remove the source track and keep TTS.

The same policy is built by one FFmpeg audio graph for the 12-second processed preview and final export.

### 4.4 Local Vietnamese TTS

Required TTS is local/free and app-managed. BiliSub Studio uses NghiTTS-compatible Vietnamese Piper ONNX voices without embedding the NghiTTS Vue/Web runtime, without WebView, localhost, or paid API.

Initial generic routing candidates are `deepman3909` (male) and `calmwoman3688` (female). Runtime/model revision, size and SHA-256 are pinned before use. Celebrity-named voice packages are not defaults. The current mirror marks the generic weight license as unknown, so public redistribution remains blocked until model-weight provenance/redistribution rights are resolved even if Windows CI passes.

### 4.5 Timing fit

Changing SSML rate alone cannot guarantee that every translated sentence fits its cue naturally. Timing fit is a measured pipeline:

1. Translate with a cue-duration/reading-length budget.
2. Synthesize once and measure actual audio duration.
3. Retry within the provider's safe rate range when needed.
4. Apply bounded post time-stretch only for the remaining small mismatch.
5. Use adjacent safe silence/gap when available.
6. Mark unresolved collisions for review instead of silently producing unintelligible speech.

## 5. Planned production ownership

```text
BiliSubStudio.App / EditorPage
  -> guided workflow, preview, timeline, cue/region inspector, user decisions
  -> no direct FFmpeg/Python/provider process ownership

BiliSubStudio.Core / Editor
  -> EditorProjectStore
  -> EditorPipelineService
  -> EditorDependencyInvalidator
  -> EditorPreviewService
  -> EditorRenderService

BiliSubStudio.Core / Speech
  -> AsrManager + private AsrWorkerClient
  -> DiarizationManager + private AudioAnalysisWorkerClient
  -> StemSeparationManager + private StemWorkerClient
  -> worker manifests, model manifests, resource policies, process groups

BiliSubStudio.Core / Editor translation
  -> EditorSubtitleDocument (strict SRT; no cue merge/normalization)
  -> TranslationSkillBundle (exact bundled skill + contextual reference retrieval)
  -> LocalSubtitleTranslationService
       -> app-managed llama.cpp CLI process; no HTTP/localhost
       -> pinned Qwen3-8B Q4_K_M GGUF
       -> bounded analysis/batch planner + validator + checkpoint
  -> optional provider abstractions later, only after the local path passes field test

BiliSubStudio.Core / Tts
  -> ITtsProvider
       -> EdgeOnlineTtsProvider
       -> PiperLocalTtsProvider
  -> SpeakerVoiceMap
  -> TimingFitService
  -> TtsClipCache

BiliSubStudio.Core / Media
  -> app-owned ffprobe/FFmpeg
  -> audio extraction, preview frame, mix, mux, ASS/SRT render, output validation
```

Private workers use framed stdin/stdout JSON and expose no HTTP server or fixed port. Each worker belongs to an owned process group and is identified by an exact asset/runtime manifest.

## 6. Project and artifact model

Each Editor project is stored under:

```text
Data/Projects/<project-id>/project.json
Data/Projects/<project-id>/checkpoints/
Cache/Editor/<project-id>/
Temp/Editor/<project-id>/
```

`project.json` is versioned and written atomically. It contains no provider secret.

Minimum schema:

```text
EditorProject
  Schema
  Id / Name / CreatedUtc / UpdatedUtc
  SourceMediaFingerprint
      normalized path, size, last-write, duration, streams, optional content sample hash
  SourceSubtitle
      origin, path/fingerprint, language, import diagnostics
  Glossary
      path/fingerprint, parsed characters/terms/rules
  Cues[]
      stable id
      source start/end/text/words/confidence
      translated text/status/review flags
      Whisper word/pause timing and acoustic voice-class suggestion
      manual male/female override
      synthesized cache key/duration/timing-fit/review status
  Regions[]
      stable id, normalized geometry, effect, strength, time scope
  SpeechAnalysis
      Whisper segments/words/pauses, confidence, male_like/female_like/uncertain suggestion
  AudioMix
      source Keep/Duck/Mute, TTS track/gain and exact shared preview/export policy
  SubtitleRender
      off/soft/hard, style and safe-area settings
  StageManifests
      stage, input fingerprint, settings fingerprint, output artifacts, completion state
```

Required Editor AI/TTS stages do not require paid API keys or a gated diarization token. Any future optional provider credential must remain outside project JSON in the existing protected-data ownership boundary.

## 7. Dependency invalidation rules

Changing an upstream input invalidates only dependent work:

| Change | Preserve | Invalidate |
|---|---|---|
| Preview position/UI layout | all artifacts | nothing |
| Region geometry/effect | ASR, translation, TTS | processed preview and final render |
| Source SRT text/timing | source media and audio analysis | translation, TTS, final render |
| Glossary `.md` | ASR and source cues | translation, TTS, final render |
| Translation text | Whisper timing/audio analysis | affected TTS cache and final render |
| Manual male/female voice override | Whisper timing, translation | affected TTS cache and final render |
| Audio-mix mode/levels | cues and TTS clips | mix and final render |
| Source media fingerprint | project metadata only | every derived artifact |

No stage may reuse an artifact whose input/settings fingerprint no longer matches.

## 8. Guided UI

The Editor remains one top-level tab with a guided task flow:

1. `Nguồn`: video, optional source SRT, glossary `.md`.
2. `Phụ đề`: import validation plus Whisper word/pause timing; fallback Chinese SRT only when needed.
3. `Dịch`: local model/skill, glossary audit, per-cue review.
4. `Giọng đọc`: male/female suggestion/override, local voice generation, timing-fit review and karaoke ASS.
5. `Hoàn thiện`: mask regions, Keep/Duck/Mute source audio, hard subtitle, output, render.

The workspace is not a Premiere-style generic editor:

- left/center: native preview with direct multi-region manipulation and processed-frame preview;
- bottom: one timeline with cue, TTS/rhythm, region, and warning lanes;
- right: current-stage inspector and one dominant next action;
- persistent stage/progress/recovery state;
- advanced model/provider/codec controls are collapsed by default.

Required editing behavior:

- create, select, move, and resize regions directly on the contained video frame;
- saved regions can be edited, not only deleted;
- Undo/Redo and subtitle/watermark presets;
- timeline visually shows whole/timed region spans and cue/TTS collisions;
- cue click seeks preview; scrub/play updates active cue;
- project autosaves after debounced edits and before every long stage;
- Cancel preserves completed stages and regions/cues;
- reopening the app offers `Tiếp tục dự án`.

## 9. Processing modes

Modes select a pipeline policy, not a cosmetic label:

### Nhanh

- Prefer imported SRT.
- Run the same local Whisper timing pass with the safest measured device.
- One default male and one default female voice with optional per-cue override.
- Whole-source Keep/Duck/Mute mixing plus cached TTS.

### Cân bằng

- Whisper word/pause timing for every source path; fallback Chinese SRT only when absent.
- Lightweight male/female/uncertain routing with confidence.
- Per-cue/rhythm-group TTS mapping and measured timing fit.
- Cached translation/TTS artifacts and recoverable checkpoints.

### Chất lượng cao

- Highest safe local timing/ASR configuration selected by preflight.
- Stricter uncertain-voice and impossible-fit review instead of silent guesses.
- Per-group timing retries, karaoke timing validation, stricter translation validation, and final stream/duration/decode verification.

All modes expose the actual selected model/device and estimated disk requirement before starting.

## 10. Translation contract for Chinese fantasy/costume films

The translation layer preserves stable cue IDs and timing. It never asks an LLM to return raw SRT as the primary protocol.

`TranslationSkillBundle` compiles the supplied `Dịch Trung Tu Tiên` skill into:

- character names, aliases, titles, relationships, and gender when supplied;
- sects, realms, cultivation stages, techniques, artifacts, places, and fixed translations;
- address/pronoun rules and do-not-translate entries;
- tone/style constraints.

`LocalSubtitleTranslationService` first reads the complete source in bounded analysis pages and accumulates a per-film character/terminology/address bible. It then sends overlapping scene context while requesting a strict JSON schema keyed by cue ID from the private app-managed `llama-cli` process. Validation rejects:

- missing, duplicate, or unknown cue IDs;
- changed timing/ordering;
- glossary violations;
- untranslated Chinese above a configurable threshold;
- empty or implausibly long Vietnamese output;
- provider commentary or formatting outside the schema.

Failed batches retry independently and never discard completed translations. The user can edit any cue, lock an approved translation, and retranslate only selected unlocked cues.

## 11. Long-video and resource policy

- Extract a single analysis audio stream, not video frames.
- Process ASR/diarization/separation in bounded chunks with overlap and deterministic reconciliation.
- Store compact JSON/checkpoint state; do not keep entire decoded audio/video in RAM.
- Model workers are benchmarked/preflighted against live RAM/VRAM before allocation.
- A GPU stage may fall back to a lower local model or CPU only before work begins; no silent topology change mid-stage.
- Display estimated model download, project cache, stem, TTS, and final-output disk requirements.
- Enforce a same-drive free-space reserve and stop safely before the drive is exhausted.
- Cache keys include source/model/settings hashes so completed work can be reused safely.

## 12. Render and validation contract

Final rendering must support:

- original audio + TTS;
- ducked original mix + TTS;
- separated non-vocal stem + TTS;
- subtitle off, softsub, or hardsub;
- Blur/Mosaic/Cover regions;
- MP4 and MKV policies with compatible audio/video codecs.

Before atomic promotion, Core verifies through ffprobe and bounded decode checks:

- expected video stream exists and is decodable;
- expected audio stream exists and is non-empty;
- duration is within tolerance of the source;
- requested subtitle stream/hardsub plan was applied;
- output is not the source path and is non-empty;
- temporary files and child process trees are clean after success, error, and cancel.

## 13. Milestones and release gates

### M0 - Architecture checkpoint

- This plan, planned call/data map, dependency/licence inventory, and acceptance matrix.
- No production release.

### M1 - Editor foundation and lost-parity recovery

- Versioned project store/autosave/reopen.
- Direct multi-region create/select/move/resize/edit.
- Processed-frame preview, timeline region spans, Undo/Redo/presets.
- Current Blur/Mosaic/Cover export preserved and hardened.

### M2 - Common path: Video + SRT + translation skill -> Vietnamese SRT/hardsub

- Strict SRT import preserves source block count/order/timecode and stable cue IDs.
- Direct normalized subtitle placement box on preview; resize-safe and project-persisted.
- Exact supplied skill ZIP is bundled, SHA/path/size validated and compiled into core rules + contextual glossary/reference layers.
- Pinned app-managed llama.cpp Vulkan/CPU runtime and Qwen3-8B Q4_K_M model; no manual Ollama/Python install and no localhost server.
- Whole-source terminology/character analysis, overlapping cue batches, strict JSON validation and atomic checkpoint/resume.
- Separate Vietnamese SRT output plus real ASS/FFmpeg hardsub using the selected placement.

The fixed preview uses a compact Subtitle/Blur/Audio/Export icon rail. SRT selection and AI preparation no longer depend on choosing a video first; a preselected validated SRT is attached when the video project opens. Subtitle and Blur modes own separate pointer interaction so their rectangles cannot steal each other's gestures. Schema-5 projects persist source-audio Keep/Duck/Mute, Whisper speech-analysis provenance, TTS cache provenance and per-cue male/female overrides while retaining schema 1-4 migration.

Preview is not allowed to switch to an unprocessed source playback path. The editable still frame remains the low-latency direct-manipulation surface; `Xem bản chỉnh` renders a bounded 12-second proxy from the playhead through the same effect/ASS/source+TTS audio builders as final output. Cue time, Whisper word timing and pause timing are all sliced and shifted into proxy-relative time. Its proxy clock is remapped to source time, all edit controls lock while playing, and returning/ending releases and deletes the app-owned temporary MP4 before the editable frame is refreshed.

The previous incremental M2 field-test proposal is retained as an automated/regression acceptance target, but the owner requested one consolidated field test after the full Editor branch instead of testing each intermediate candidate.

### M3 - Local Whisper timing / fallback Chinese ASR

- Implemented app-managed faster-whisper 1.2.1/CTranslate2 4.8.1 in a separate private venv that reuses the Windows error-448-safe exact-patch Python bootstrap.
- Implemented immutable multilingual small-model revision with exact per-file size/SHA-256, resumable download and offline-only loading.
- Implemented real-video 16 kHz audio extraction, Chinese transcription, word timestamps and VAD. The same pass now persists word/pause timing even when the user imported an SRT; generated Chinese SRT is fallback only.
- Implemented pre-run full GPU benchmark with live VRAM gate and measured CPU/int8 fallback; device/compute/threads are locked before the real scan.
- Implemented atomic per-segment checkpoint/resume with overlap-tail reconciliation and owned Python/FFmpeg cleanup on cancel.

M3 remains build-verified rather than field-PASS until the owner performs the single consolidated Editor test after later milestones.

### M4 - Male/female routing, local TTS and karaoke

- Preserve Whisper words/pauses in schema-5 project-linked speech analysis and map them to imported/generated SRT cues.
- Lightweight `male_like` / `female_like` / `uncertain` acoustic routing with confidence; manual per-cue male/female override is authoritative.
- App-managed Piper 1.4.2 and pinned generic NghiTTS-compatible ONNX male/female models; no API/WebView/localhost dependency.
- Per-rhythm-group cache, synthesize/measure/length-scale/bounded time-stretch, review state, 300-second cache blocks and one seekable master voice track.
- Karaoke ASS generated from the same Whisper timing evidence and the exact ASS builder used by render.

### M5 - Source/TTS mix, subtitle render, final export

- Keep/Duck/Mute complete source mix; no stem-separation path.
- Shared source+TTS FFmpeg graph for processed 12-second preview and final export.
- Hardsub/karaoke ASS, region filters, final validation and atomic promotion.

### M6 - Hardening and public beta

- full dependency/process/disk/corruption/restart matrix.
- 4+ hour pause/close/reopen/resume field scenario.
- real Chinese fantasy/costume-film translation and dubbing review.
- exact Windows candidate CI/package/installer PASS, then user field-test PASS.
- only then publish a new immutable public version and update `update/beta.json`.

## 14. Minimum regression matrix

- Project atomic save, corrupt-file quarantine, schema upgrade, source fingerprint drift.
- Stage dependency invalidation and selective cache reuse.
- SRT encoding/timing/order validation and Unicode paths.
- Glossary parsing, cue-ID structured translation, retry and locked-cue preservation.
- Worker stdout-noise tolerance, request timeout, process-tree cleanup, model/runtime manifest mismatch.
- ASR chunk overlap/reconciliation, VAD silence, long-duration timestamp continuity.
- Whisper words/pauses survive checkpoint/project reopen and remain aligned after preview slicing.
- Male/female/uncertain suggestion is advisory; manual voice override survives restart and selectively invalidates only TTS.
- Local NghiTTS/Piper cache, duration measurement, bounded fit and impossible-fit warning.
- Audio modes map exactly to Keep/Duck/Mute plus the same TTS master track in preview/export; no stem behavior exists.
- Multi-region filter correctness, processed preview parity, rotation/SAR/letterbox mapping.
- Render cancel cleans partial output without deleting completed project artifacts.
- Final output stream/duration/decode validation, collision-safe path and source preservation.
- WinUI layout/keyboard/DPI smoke at supported viewport sizes.

## 15. Field-test sequence

Automated gates remain milestone-specific. Real-machine field testing is consolidated after the requested Editor branch is complete; the user is not asked to test intermediate downstream stages.

M1:

1. Open a real video.
2. Add two regions, move/resize one, Undo/Redo, resize the window.
3. Seek to a timed region and confirm processed preview.
4. Export, Cancel once, export again, verify no FFmpeg remains.

M2:

1. Open video + Chinese SRT + glossary `.md`.
2. Translate one short scene locally.
3. Confirm names/realms/sects follow the glossary.
4. Edit and lock one cue, retry the batch, confirm the locked cue survives.
5. Close/reopen and continue the project.

The consolidated final scenario adds Whisper word/karaoke timing, male/female override, local TTS, Keep/Duck/Mute mix and four-hour resume checks only after their automated Windows gates pass.

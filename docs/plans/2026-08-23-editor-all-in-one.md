# Editor All-in-One for Chinese film localization

Status: M1 foundation, the requested M2 common path, the icon-mode workspace and the first persisted source-audio policy are implemented on the draft Editor branch. The owner explicitly deferred incremental field testing until the requested Editor branch is complete. Windows candidate gates remain mandatory for every checkpoint; this does not authorize a merge or public release.

Date: 2026-08-23

## 1. Product decision

The Editor is a guided Chinese-film localization workstation, not a generic nonlinear video editor.

It must support two source paths:

1. Video only: extract audio -> Chinese ASR -> timed Chinese cues -> Vietnamese translation -> speaker/audio analysis -> Vietnamese TTS -> subtitle/audio mix -> final render.
2. Video plus source SRT: import and validate SRT -> Vietnamese translation -> speaker/audio analysis -> Vietnamese TTS -> subtitle/audio mix -> final render.

The existing Blur/Mosaic/Cover region editor remains part of the workflow for masking burned-in source subtitles and watermarks.

## 2. Binding constraints

- Production remains C# + .NET 10 + WinUI 3 on Windows x64.
- No browser UI, WebView, localhost BiliSub backend, Go production path, or second BiliSub executable.
- The user must not install system Python, pip, PowerShell modules, FFmpeg, or model dependencies.
- Optional AI runtimes are app-managed, versioned, checksum-verified, and isolated under `Tools`.
- Local/offline processing is the default because paid API budget cannot be assumed.
- Provider APIs are optional accelerators and must never be required to open or recover a project.
- Video commonly exceeds four hours. Every long stage is resumable and bounded in RAM/VRAM/disk use.
- Do not dump a full video's frames to `%TEMP%` or the system drive.
- Project data, artifacts, models, cache, and temporary output stay in the app-owned same-drive roots.
- Cancel stops and reaps every owned child process tree. Completed stages remain recoverable; only the current partial artifact is removed.
- Source media is never overwritten. Final output is rendered to a temporary file, validated, then atomically promoted.
- Correctness and recoverability outrank peak throughput.
- Every production change updates call/data maps, adds regression coverage, passes Windows CI/package gates, and then passes the exact real-machine field scenario before public release.

## 3. Current-source audit and root cause

The current C# Editor is only a thin region/export surface:

```text
EditorPage
  -> MediaPreviewService: ffprobe + one raw frame
  -> in-memory List<EditRegion>
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.BuildFilter
  -> FFmpeg Blur/Mosaic/Cover render
```

It does not yet own a persisted project, subtitle document, translation provider, ASR worker, speaker map, TTS clips, audio stems, render manifest, or pipeline checkpoint.

The C# migration also omitted behavior that existed in the historical native editor model: selected-region editing, Undo, presets, and processed-frame effect preview. The current timeline does not show region spans, and saved regions cannot be moved/resized/edited directly.

The root cause is therefore architectural, not one missing button: the migration preserved the final FFmpeg exporter but did not port an Editor domain model or a resumable localization pipeline.

## 4. Corrections to the proposed technology assumptions

### 4.1 ASR

Use an app-managed `faster-whisper` worker as the initial local ASR implementation. It supports word-level timestamps and Silero VAD, and CTranslate2 offers practical CPU/GPU execution. Model and compute type are selected by a resource preflight; the user is not asked to understand CUDA or model formats.

ASR output is an internal cue document with stable IDs, word timing, confidence, language, and source offsets. SRT is an import/export format, not the only source of truth.

### 4.2 Diarization is not gender detection

`pyannote.audio` answers "who spoke when" and returns anonymous speaker labels. It does not guarantee male/female classification.

The product therefore separates three concepts:

- diarization label: `SPEAKER_00`, `SPEAKER_01`, ...;
- optional voice-class suggestion: a confidence-scored acoustic suggestion;
- user voice assignment: the authoritative TTS voice for that speaker.

Automatic suggestions are allowed, but the UI must expose a simple override and never present uncertain gender as fact.

### 4.3 Preserving music requires source separation

Whisper, VAD, and diarization cannot remove only original dialogue while preserving music/effects. FFmpeg volume reduction alone lowers the whole source mix.

The audio modes are therefore explicit:

1. `Lồng trên tiếng gốc`: keep source mix and add TTS.
2. `Giảm tiếng gốc`: duck the complete source mix under TTS; fast but also lowers music/effects.
3. `Giữ nhạc nền`: run an optional local stem-separation stage, then mix TTS with the non-vocal stem. This is slower and may produce artifacts on film audio.

The high-quality mode initially uses an app-managed two-stem Demucs-class worker. The UI must not promise perfect dialogue removal.

### 4.4 TTS provider stability

`edge-tts` uses Microsoft Edge's online speech service without a documented application API key. It is useful and free, but it is not a stable production contract controlled by BiliSub Studio.

TTS is therefore provider-based:

- `EdgeOnlineTtsProvider`: free online provider, runtime health check, retry/backoff, cache, and clear unavailable state;
- `PiperLocalTtsProvider`: offline fallback, app-managed voice packages;
- future official/API providers can be added without changing the project model.

The voice catalog is discovered from the selected provider rather than hard-coded as "14+ voices". For Microsoft's current Vietnamese catalog, `HoaiMy` is female and `NamMinh` is male. `NgocChinh` must not be advertised as an Edge/Microsoft voice unless the active provider actually returns it.

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
      speaker id/voice assignment
      synthesized clip key/duration/timing-fit status
  Regions[]
      stable id, normalized geometry, effect, strength, time scope
  AudioAnalysis
      speech/silence turns, anonymous speaker turns, confidence, user overrides
  AudioMix
      mode, source/stem levels, TTS level, ducking settings
  SubtitleRender
      off/soft/hard, style and safe-area settings
  StageManifests
      stage, input fingerprint, settings fingerprint, output artifacts, completion state
```

API keys/tokens are stored through Windows DPAPI in the existing protected-data ownership boundary. Hugging Face acceptance/token requirements for gated pyannote models must be surfaced before download; a partially downloaded model is not a healthy installation.

## 7. Dependency invalidation rules

Changing an upstream input invalidates only dependent work:

| Change | Preserve | Invalidate |
|---|---|---|
| Preview position/UI layout | all artifacts | nothing |
| Region geometry/effect | ASR, translation, TTS | processed preview and final render |
| Source SRT text/timing | source media and audio analysis | translation, TTS, final render |
| Glossary `.md` | ASR and source cues | translation, TTS, final render |
| Translation text | ASR, diarization, stems | affected TTS clips and final render |
| Speaker voice | ASR, translation, stems | affected TTS clips and final render |
| Audio-mix mode/levels | cues and TTS clips | mix and final render |
| Source media fingerprint | project metadata only | every derived artifact |

No stage may reuse an artifact whose input/settings fingerprint no longer matches.

## 8. Guided UI

The Editor remains one top-level tab with a guided task flow:

1. `Nguồn`: video, optional source SRT, glossary `.md`.
2. `Phụ đề`: import validation or local Chinese ASR.
3. `Dịch`: provider/model, glossary audit, per-cue review.
4. `Giọng đọc`: speaker groups, voice assignment, timing/collision review.
5. `Hoàn thiện`: mask regions, audio mode, hard/soft subtitle, output, render.

The workspace is not a Premiere-style generic editor:

- left/center: native preview with direct multi-region manipulation and processed-frame preview;
- bottom: one timeline with cue, speaker, TTS, region, and warning lanes;
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
- Skip diarization and stem separation unless explicitly enabled.
- One default male and one default female/manual voice mapping.
- Whole-mix ducking or original-audio mix.

### Cân bằng

- Local ASR when SRT is absent.
- Speaker diarization and silence analysis.
- Per-speaker TTS mapping and measured timing fit.
- Cached translation/TTS artifacts and recoverable checkpoints.

### Chất lượng cao

- Highest safe local ASR model selected by preflight.
- Diarization plus overlap/collision review.
- Optional two-stem separation for dialogue replacement.
- Per-cue timing retries, stricter translation validation, and final stream/duration/decode verification.

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

The fixed preview now uses a compact Subtitle/Blur/Audio/Export icon rail. SRT selection and AI preparation no longer depend on choosing a video first; a preselected validated SRT is attached when the video project opens. Subtitle and Blur modes own separate pointer interaction so their rectangles cannot steal each other's gestures. Schema-3 projects persist source-audio keep/duck/mute and render maps it to an exact FFmpeg policy.

The previous incremental M2 field-test proposal is retained as an automated/regression acceptance target, but the owner requested one consolidated field test after the full Editor branch instead of testing each intermediate candidate.

### M3 - Video-only Chinese ASR

- app-managed faster-whisper runtime/model manager.
- audio extraction, word timestamps, VAD, cue segmentation, checkpoint/resume.
- CPU/GPU preflight and long-video reconciliation.

### M4 - Speaker analysis and TTS

- app-managed pyannote-compatible diarization runtime with gated-model setup.
- anonymous speaker grouping, confidence and user voice override.
- Edge online provider plus Piper local fallback.
- per-cue cache, measured timing fit, collision review.

### M5 - Audio separation, mix, subtitle render, final export

- fast whole-mix ducking.
- optional high-quality two-stem separation.
- TTS mix, soft/hardsub, region filters, final validation and atomic promotion.

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
- Diarization speaker IDs remain anonymous; user voice override survives restart.
- TTS provider outage/fallback/cache, duration measurement and impossible-fit warning.
- Audio modes map exactly to full-mix ducking versus separated-stem behavior.
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

Later milestones add ASR, speaker/TTS, stem/mix, and four-hour resume checks only after their automated Windows gates pass.

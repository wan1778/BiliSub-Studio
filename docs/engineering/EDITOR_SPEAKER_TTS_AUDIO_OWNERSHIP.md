# Editor M4 speaker, TTS and audio ownership

Status: architecture/audit checkpoint for the draft `editor-all-in-one` branch. This document freezes ownership before production speaker/TTS/stem implementation. It does not authorize merge, public beta, or field PASS.

## 1. Audited current boundary and root cause

The current Editor has a correct processed-preview/export parity boundary for video effects, subtitles and source-audio keep/duck/mute, but it has no production ownership for speaker identity, speaker-to-cue mapping, TTS providers, synthesized clips, timing fit, stems or mixed-audio manifests.

Today the audio path is intentionally narrow:

```text
EditorProject schema 4
  -> EditorAudioSettings(SourceMode, SourceGain)
  -> EditorPage.CurrentEditRequest
  -> VideoEditorService
       preview: BuildPreviewSlice -> BuildFilterCore + BuildAss + BuildAudioArgumentsCore
       export:  BuildFilter      -> BuildAss + BuildAudioArguments
  -> source audio only
```

`BuildAudioArgumentsCore` maps `0:a?` and can reset timestamps / apply one source-volume filter / encode AAC. That is sufficient for source keep/duck/mute, but it cannot correctly own multiple timed TTS inputs or a separated non-vocal stem. Adding TTS through `MediaPlayer.Volume`, a second preview path, or a post-render mux would create two audio truths and violate the existing preview/render parity contract.

The root cause is therefore an absent audio-domain plan, not a missing TTS button.

## 2. Non-negotiable semantic separation

Speaker processing stores three separate concepts:

1. `speaker_id`: anonymous diarization identity such as `SPEAKER_00`; authoritative only for grouping turns.
2. `voice_class_suggestion`: optional acoustic suggestion with method + confidence. It is advisory and is never displayed as a guaranteed biological sex/gender fact.
3. `voice_assignment`: provider + voice id selected by the user or an explicit automatic policy. This is the authoritative TTS choice.

`pyannote`-compatible diarization may own anonymous speaker turns. It must not own guaranteed gender classification. Any future acoustic voice-class classifier is a separate dependency and must expose calibrated confidence plus an unknown/insufficient-evidence state.

## 3. Production ownership

```text
BiliSubStudio.App / EditorPage
  -> display anonymous speaker groups
  -> show suggestion + confidence without factual-gender wording
  -> allow explicit per-speaker voice override
  -> show cue timing-fit/review warnings
  -> trigger prepare/analyze/synthesize actions
  -> never spawn Python/provider/FFmpeg directly

BiliSubStudio.Core / Editor
  -> persisted project schema and dependency invalidation
  -> cue <-> speaker assignment state
  -> stage manifests/checkpoints
  -> orchestration only; no provider-specific UI assumptions

BiliSubStudio.Core / Speech (M4 implementation boundary)
  -> SpeakerAnalysisService
       -> diarization worker/runtime/model ownership
       -> bounded audio chunks, overlap reconciliation, anonymous speaker turns
       -> confidence/provenance; no guaranteed gender
  -> optional future VoiceClassSuggestionService
       -> independent acoustic classifier
       -> suggestion class + calibrated confidence + unknown

BiliSubStudio.Core / Tts
  -> ITtsProvider
       -> EdgeOnlineTtsProvider first
       -> offline provider later without project-schema rewrite
  -> TtsClipCache
  -> TimingFitService
  -> SpeakerVoiceMap normalization

BiliSubStudio.Core / Editor media render
  -> AudioRenderPlan / shared FFmpeg graph builder
       -> source mix OR separated non-vocal stem
       -> timed TTS clips
       -> source/stem/TTS gains and ducking
       -> one graph used by processed preview and final render
  -> VideoEditorService remains the FFmpeg process owner until/unless split into dedicated preview/render services

M5 stem separation
  -> StemSeparationService owns model/runtime/process/checkpoint
  -> outputs a verified non-vocal stem artifact
  -> never masquerades as Whisper/VAD/diarization output
  -> shared AudioRenderPlan consumes the stem; it does not own separation
```

## 4. Project model frozen for the next schema

The next schema must add typed state equivalent to:

```text
SpeakerAnalysis
  status
  model/runtime provenance
  source-audio fingerprint
  turns[]
      start/end
      speaker_id
      diarization confidence when available
  speakers[]
      speaker_id
      optional voice_class_suggestion
          class_id
          confidence 0..1
          method/provenance
      authoritative voice_assignment
          provider_id
          voice_id
          assignment_source = auto | user

TtsProject
  provider_id
  provider/runtime provenance
  clips[] keyed by cue id
      cue_id
      speaker_id
      translated_text_hash
      voice_id
      synthesis settings fingerprint
      cache_key
      generated path/artifact fingerprint
      raw_duration
      fitted_duration
      provider_rate
      post_stretch_ratio
      fit_status = fit | review | failed
      review_reason

AudioMix
  base_mode = source_keep | source_duck | source_mute | separated_non_vocal
  source_gain
  stem_gain
  tts_gain
  ducking settings
```

Missing cache files must invalidate/regenerate the affected artifact; they must not corrupt or quarantine the entire project. Provider secrets/tokens must never be written into `project.json`.

## 5. Dependency invalidation

| Change | Preserve | Invalidate |
|---|---|---|
| Preview position/window size | everything | nothing |
| Diarization model/settings | translation, regions | speaker turns, cue-speaker map, dependent automatic voice assignments, affected TTS, mix/render |
| Manual speaker merge/split | translation, regions, stems | affected cue-speaker map and affected TTS |
| Voice-class suggestion | all authoritative assignments | nothing unless automatic policy explicitly re-applied |
| User speaker voice override | ASR, translation, diarization, stems | TTS clips for that speaker + mix/render |
| Vietnamese cue text | speaker turns, stems | that cue TTS clip + mix/render |
| Cue timing | translation text if unchanged | that cue timing fit + mix/render |
| Provider/voice synthesis settings | ASR, translation, diarization, stems | affected TTS cache keys + mix/render |
| Source keep/duck/mute gain | cues/TTS clips | mix/render only |
| Enable separated non-vocal mode | cues/TTS clips | stem stage when missing/stale + mix/render |
| Source-media fingerprint | project metadata only | every derived speaker/TTS/stem/render artifact |

No completed artifact may be reused if its input/settings fingerprint differs.

## 6. Speaker-analysis execution contract

- Analyze an extracted bounded audio representation, never video frames.
- Long videos are chunked with overlap and deterministic turn reconciliation.
- Worker runtime/model files are app-owned, exact-version pinned and checksum/fingerprint validated before use.
- Gated model/token/license requirements are surfaced before download. A failed or partial gated download is not healthy state.
- Benchmark/resource preflight occurs before the long run; no silent CPU/GPU topology switch midway through a stage.
- Checkpoint only deterministic accepted turn boundaries.
- Cancel kills/reaps every owned worker/FFmpeg process and preserves completed checkpoint state.
- Diarization output remains anonymous even when a later voice suggestion exists.

Exact diarization package/model revisions are deliberately not frozen in this checkpoint; they must be selected only after dependency/license/gated-access verification and then recorded in a dedicated dependency manifest.

## 7. TTS provider contract

Initial online provider: `EdgeOnlineTtsProvider`.

Required provider behavior:

- explicit provider id and discovered voice catalog;
- health/unavailable state visible to Core/UI;
- bounded retry with backoff for transient failures;
- cancellation propagated through provider/network/process ownership;
- deterministic cache key from provider id, voice id, normalized text and synthesis settings;
- no hard-coded promise that a named voice exists unless the active catalog returns it;
- provider outage never destroys completed clips;
- provider-specific rate limits/accepted rate syntax are isolated behind `ITtsProvider`.

Offline fallback is a later implementation checkpoint. The project persists provider/voice identifiers generically so adding it does not rewrite speaker or cue ownership.

## 8. Measured timing-fit contract

Timing fit is based on generated audio duration, never text-length guess alone:

```text
translated cue
  -> synthesize baseline clip
  -> measure real decoded duration
  -> compare with cue budget + safe adjacent silence policy
  -> provider-rate retry within provider-characterized safe limits
  -> measure again
  -> bounded post time-stretch only for remaining small mismatch
  -> measure final clip
  -> fit OR review-required
```

Safe provider-rate and post-stretch bounds must be empirical/provider-specific constants with regression coverage; this checkpoint does not invent unverified universal numbers.

A cue is `review` rather than silently over-compressed when it cannot fit within the validated limits. Timing fit never changes source SRT order/timecodes automatically.

## 9. TTS cache contract

Cache root is app-owned under `Cache/Editor/<project-id>/Tts` (or the final equivalent project cache root).

Cache identity must include at least:

- translated text hash;
- cue id/timing fingerprint where timing affects synthesis policy;
- speaker id;
- provider id + provider/runtime version when available;
- voice id;
- provider rate/style/settings;
- timing-fit algorithm version.

Cache writes are temporary + validated + atomic promotion. A clip is reusable only when metadata and file fingerprint match. Missing/corrupt clip removes only that clip from reusable state.

## 10. Unified preview/render audio graph

M4 must replace the current single-source-audio argument-only assumption with one explicit render plan while preserving exact legacy behavior when no TTS/stem exists.

```text
CurrentEditRequest / final render request
  -> AudioRenderPlanBuilder
       inputs:
         source video audio
         optional separated non-vocal stem
         zero..N intersecting TTS clips
       timeline:
         source timestamps
         preview source-window offset when slicing
       policy:
         source_keep / source_duck / source_mute / separated_non_vocal
         source/stem/TTS gain
       output:
         FFmpeg input list + one audio filter graph/output label

processed preview
  -> slice regions/cues/TTS clips to 12-second source window
  -> shift timed TTS offsets into proxy time
  -> exact same AudioRenderPlanBuilder
  -> H.264/AAC preview proxy

final export
  -> exact same AudioRenderPlanBuilder
  -> final audio codec/container policy
```

Rules:

- TTS must be audible in `Xem bản chỉnh` before export.
- Source/stem/TTS mix is never approximated with `MediaPlayer.Volume`.
- Preview may use faster video encoding but not a different audio semantic graph.
- Preview slicing must include only TTS clips that intersect the source window and must preserve their source-time placement after offset translation.
- With no TTS/stem, keep/duck/mute must remain byte-policy equivalent to the current tested FFmpeg argument behavior where applicable.

## 11. UI contract for M4

The compact icon rail remains. Speaker/TTS controls belong inside the Audio inspector rather than adding a long text tab.

Minimum simple workflow:

1. `Phân tích người nói` after source cues/video are available.
2. Show `SPEAKER_00`, `SPEAKER_01`, ... with turn/cue counts.
3. Optional small suggestion text includes confidence; never label uncertain gender as fact.
4. Voice dropdown per speaker is authoritative and persists immediately.
5. `Tạo giọng Việt` synthesizes/reuses cached clips and reports fit/review counts.
6. Clicking a review cue seeks the existing preview/timeline.
7. `Xem bản chỉnh` renders the actual source/stem/TTS mix.

Advanced dependency/model/provider details stay collapsed by default.

## 12. Checkpoint sequence after this audit

### M4-B: typed persistence + speaker analysis foundation

- schema migration with typed speaker state and selective invalidation;
- dependency manifest for chosen diarization runtime/model after verification;
- app-owned prepare/benchmark/analyze/checkpoint/cancel path;
- anonymous speaker UI + manual voice assignment persistence;
- Windows contracts for anonymous labels, confidence semantics and restart persistence.

### M4-C: Edge online TTS + cache + timing fit

- `ITtsProvider` and Edge implementation;
- discovered Vietnamese voice catalog;
- retry/health/cancel/cache;
- measured duration and bounded timing fit;
- review-required state and UI;
- no offline fallback yet unless explicitly promoted into this milestone.

### M4-D: unified audio graph

- TTS clips added to one `AudioRenderPlan`;
- processed preview and export share the exact mix builder;
- keep/duck/mute regressions remain green;
- source + TTS preview audible before export.

M5 then adds separated non-vocal stem as another base-audio input to the same graph.

## 13. Release gate

This architecture checkpoint changes no public release status. Every later implementation checkpoint must still pass contract/regression tests, real WinUI/XAML/XBF/PRI build, startup smoke, installer custom-location startup/uninstall smoke and candidate packaging. Field QA remains consolidated until the full Editor main flow is complete.

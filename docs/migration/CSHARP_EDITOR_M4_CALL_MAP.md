# Editor M4 speaker/TTS/audio call map

Checkpoint: `editor-all-in-one` M4-A ownership freeze. This call map is additive to `CSHARP_WINUI3_CALL_MAP.md`; no production speaker/TTS/stem process is enabled by this checkpoint.

## Current verified boundary

```text
EditorPage.Audio inspector
  -> EditorAudioSettings(source keep / duck / mute)
  -> EditorPage.CurrentEditRequest
  -> BiliSubApplication.CreateEditorPreviewSegmentAsync OR StartEditor
  -> VideoEditorService
       preview -> BuildPreviewSlice -> BuildFilterCore + BuildAss + BuildAudioArgumentsCore
       export  -> BuildFilter      -> BuildAss + BuildAudioArguments
  -> source audio only
```

This boundary is retained until M4-D replaces the single-source audio argument assumption with a unified audio render plan.

## M4-B speaker-analysis path

```text
EditorPage.Audio / Phân tích người nói
  -> BiliSubApplication.StartEditorSpeakerAnalysis
  -> JobManager.Create(kind=editor-speaker-analysis, cleanupAwareCancel=true)
  -> SpeakerAnalysisService.AnalyzeAsync
       -> dependency preflight / gated-model readiness
       -> app-owned FFmpeg bounded audio extraction
       -> resource benchmark before long run
       -> private diarization worker
            -> anonymous SPEAKER_XX turns only
       -> overlap reconciliation
       -> checkpoint accepted turns
       -> project speaker state
            speaker_id
            turn confidence/provenance
            optional voice-class suggestion + confidence
            authoritative provider/voice assignment
  -> EditorProjectStore.SaveAsync

Cancel
  -> AppJob.Cancel stays cancelling
  -> kill/reap worker + FFmpeg process trees
  -> preserve completed speaker checkpoint
  -> terminal CancelComplete only after cleanup
```

`pyannote`-compatible diarization never writes guaranteed gender. A future acoustic voice-class suggestion service is separate and advisory.

## M4-C TTS path

```text
EditorPage.Audio / Tạo giọng Việt
  -> BiliSubApplication.StartEditorTts
  -> JobManager.Create(kind=editor-tts, cleanupAwareCancel=true)
  -> TtsPipelineService
       -> SpeakerVoiceMap resolves authoritative assignment
       -> ITtsProvider selected by provider_id
            initial -> EdgeOnlineTtsProvider
       -> TtsClipCache lookup by text/provider/voice/settings fingerprint
       -> synthesize missing clip
       -> Media/FFprobe duration measurement
       -> TimingFitService
            baseline duration
            provider-rate retry within characterized safe bounds
            bounded post time-stretch for remaining small mismatch
            final duration measurement
            fit OR review-required
       -> atomic clip + metadata cache promotion
       -> project TTS clip manifest/checkpoint
  -> UI exposes completed / cached / review / failed counts
```

Provider outage or cancellation does not delete completed clips. Voice availability is discovered from the active provider; UI does not promise unreturned voice ids.

## M4-D shared audio render plan

```text
EditorPage.CurrentEditRequest / final render request
  -> AudioRenderPlanBuilder
       base input
         source mix              for source_keep/source_duck/source_mute
         separated non-vocal     for M5 high-quality replacement mode
       timed inputs
         intersecting TTS clips
       settings
         source/stem/TTS gain
         ducking policy
         source-time offsets
  -> one FFmpeg audio filter graph + output label

Xem bản chỉnh
  -> BuildPreviewSlice
       -> shift/clamp video regions
       -> shift/clamp subtitle cues
       -> select intersecting TTS clips
       -> shift TTS placement by preview sourceStart
  -> exact AudioRenderPlanBuilder
  -> preview H.264/AAC proxy
  -> WinUI MediaPlayer only plays the rendered proxy

Final export
  -> exact same AudioRenderPlanBuilder
  -> final codec/container policy
  -> output validation / atomic promotion
```

`MediaPlayer.Volume` remains monitor-only and can never substitute for source/stem/TTS mix semantics.

## M5 stem integration

```text
EditorPage.Audio / Giữ nhạc nền
  -> BiliSubApplication.StartEditorStemSeparation
  -> StemSeparationService
       -> exact app-owned runtime/model manifest
       -> bounded chunk processing/checkpoint
       -> verified non-vocal stem artifact
  -> project stage manifest
  -> AudioRenderPlanBuilder(base = separated non-vocal stem)
       + existing timed TTS clips
  -> same processed preview / final render path
```

Whisper, VAD and diarization never claim to remove dialogue. Stem separation owns that artifact explicitly.

## Persistence and invalidation call path

```text
user changes speaker voice
  -> Editor project voice_assignment update
  -> invalidate TTS clips for that speaker only
  -> invalidate mix/render
  -> preserve ASR/translation/diarization/stem

user edits Vietnamese cue
  -> invalidate TTS clip for that cue only
  -> invalidate mix/render

user changes source audio gain/mode
  -> preserve cues + TTS cache
  -> invalidate mix/render only

source media fingerprint changes
  -> invalidate speaker analysis + TTS + stems + render
```

Missing/corrupt cache artifacts cause selective regeneration; they do not quarantine the whole project.

## Gate

M4-A itself introduces documentation/version ownership only. The existing generated C# code map remains unchanged because no production C# type/method is added in this checkpoint. M4-B and later production changes must regenerate `CSHARP_CODE_MAP.generated.md` and extend the main WinUI call map before their CI candidate can be considered PASS.

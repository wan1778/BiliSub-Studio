# C# Editor M4 revised call map

Status: owner-revised voice-class + local TTS plan. This supersedes the earlier pyannote diarization / stem-separation M4 assumptions.

```text
EditorPage
  -> translated subtitle cues + source timeline
  -> Voice inspector
       -> male default local voice
       -> female default local voice
       -> optional per-cue override
       -> uncertain/review warnings

BiliSubApplication.StartEditorVoiceAnalysis
  -> EditorVoiceClassService
       -> use cue start/end as bounded analysis windows
       -> app-owned FFmpeg extracts mono analysis audio
       -> local acoustic classifier / feature pipeline
       -> EditorVoiceClassResult
            cue id
            class = male | female | uncertain
            confidence 0..1
            analysis fingerprint
       -> persist result in Editor project

BiliSubApplication.StartEditorTts
  -> LocalVietnameseTtsService
       -> NghiTTS/Piper-compatible Vietnamese text normalization
       -> resolve local ONNX model + JSON config by reviewed manifest
       -> default route
            male-like -> selected male voice
            female-like -> selected female voice
            uncertain -> configured fallback or user review
       -> explicit per-cue override wins
       -> synthesize cue WAV
       -> measure real duration
       -> TimingFitService
            safe synthesis-rate retry
            bounded post time-stretch when necessary
            unresolved mismatch -> review warning
       -> TtsClipCache
            content/model/settings keyed
            atomic completed clip promotion

EditorPage.XemBanChinh
  -> CurrentEditRequest
       -> source keep/duck/mute policy
       -> timed TTS clip plan
       -> subtitle / regions
  -> BiliSubApplication.CreateEditorPreviewSegmentAsync
  -> VideoEditorService.CreatePreviewSegmentAsync
       -> BuildPreviewSlice
            clip/shift regions
            clip/shift subtitle cues
            clip/shift TTS clips
       -> shared video graph
       -> shared audio graph
            source mix keep/duck/mute
            TTS clip inputs delayed to cue position
            gain / mix / limiter policy
       -> H.264/AAC preview proxy

EditorPage.Render_Click
  -> same CurrentEditRequest / audio render plan
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
       -> same source/TTS audio graph as preview
       -> final output validation
```

## Explicitly removed paths

```text
NO pyannote
NO SPEAKER_00 / SPEAKER_01 clustering
NO gated diarization model/token requirement
NO Demucs
NO vocal/non-vocal stem cache
NO "remove dialogue but keep music" promise
NO MediaPlayer-only TTS preview approximation
```

## Persistence target

A future schema revision will add per-cue voice metadata and TTS clip metadata. Exact production record names are deferred until implementation, but ownership is fixed:

```text
Cue voice state
  auto class
  confidence
  manual class/voice override
  selected local voice fingerprint
  TTS clip key/path/duration
  timing-fit state
```

Changing a cue translation or its selected voice invalidates that cue TTS only. Changing Blur/Subtitle placement does not invalidate voice analysis or TTS.

## Native-app constraint

NghiTTS is a reference implementation and model source candidate. BiliSub Studio remains native C# + .NET 10 + WinUI 3. The Vue/Vite frontend, browser Web Worker architecture and Cloudflare API endpoints are not production dependencies.

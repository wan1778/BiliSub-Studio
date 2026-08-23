# C# Editor M4 implemented call map

Status: production path implemented on `editor-all-in-one`; Windows CI/field verification gates remain.

```text
Video + original audio
  -> EditorPage.CreateAsr_Click
  -> BiliSubApplication.StartEditorAsr
  -> LocalAsrService.TranscribeAsync
       -> LocalAsrInstaller (pinned faster-whisper model/runtime)
       -> ToolManager.EnsureFfmpegAsync
       -> extract mono 16 kHz source audio
       -> GPU/CPU real benchmark
       -> internal/asr/worker.py
            faster_whisper(... local_files_only=True)
            word_timestamps=True
            VAD
            per-segment F0 feature routing
            -> words[] + male_like/female_like/uncertain + confidence
       -> resumable checkpoint schema 2
       -> EditorSpeechAnalysisDocument.SaveAsync
            app-owned speech JSON + SHA-256
       -> optional fallback .zh.srt only when project had no external SRT

External/generated Chinese SRT
  -> EditorSubtitleDocument
  -> remains authoritative for cue text/order/timecode
  -> EditorSpeechAnalysisDocument.MapToCues
       map Whisper words by timeline overlap
       derive speech envelope
       derive leading/trailing silence
       derive internal pauses
       derive advisory voice routing per cue

Chinese SRT
  -> LocalSubtitleTranslationService
       pinned local Qwen3-8B Q4_K_M + Dịch Trung Tu Tiên skill
       -> VietnameseText without changing cue IDs/order/timecodes
       -> translation change clears stale TTS state only

EditorPage.GenerateTts_Click
  -> BiliSubApplication.StartEditorTts
  -> LocalTtsService.GenerateAsync
       -> verify speech-analysis SHA/source fingerprint
       -> VietnameseTtsTextNormalizer
       -> voice routing
            manual cue override wins
            male_like confidence >= threshold -> deepman3909
            female_like confidence >= threshold -> calmwoman3688
            uncertain -> closest fallback + review flag
       -> BuildRhythmGroups from Whisper pauses
       -> LocalTtsInstaller
            app-managed Python 3.12
            pinned piper-tts 1.4.2 Windows wheel + SHA-256
            pinned NghiTTS-compatible ONNX/config files + SHA-256
       -> internal/tts/worker.py
            synth baseline
            measure duration
            bounded Piper length_scale
            measure
            bounded FFmpeg atempo
            measure
            fit/review
            clip cache
            300-second block cache
            seekable voice-master.flac
       -> EditorTtsProject + EditorVoiceTrack

Karaoke
  -> EditorSpeechAnalysisDocument.MapToCues
  -> EditorSubtitleBurn(SpeechTiming, Karaoke=true)
  -> VideoEditorService.BuildAss
       speech envelope -> Dialogue start/end
       original word/gap rhythm -> resampled Vietnamese token durations
       ASS {\\kf...} tags
  -> same builder used by:
       final hardsub
       Xem bản chỉnh proxy
       BiliSubApplication.SaveEditorKaraokeAssAsync -> *.karaoke.ass

Xem bản chỉnh
  -> EditorPage.CurrentEditRequest
       regions
       subtitle + karaoke timing
       source Keep/Duck/Mute
       EditorVoiceTrack
  -> BiliSubApplication.CreateEditorPreviewSegmentAsync
  -> VideoEditorService.CreatePreviewSegmentAsync
       BuildPreviewSlice
            clip/shift regions
            clip/shift cues
            clip/shift word timings + pauses
       seek source video to sourceStart
       seek voice-master.flac to same source time
       shared BuildVoiceAudioFilter
            keep -> source + voice
            duck -> volume(source) + voice
            mute -> voice only
       H.264/AAC yuv420p faststart proxy

Final export
  -> same CurrentEditRequest
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
       same BuildAss
       same BuildVoiceAudioFilter
       H.264 + AAC final output
```

## Persistence (schema 5)

```text
EditorProject
  Speech: EditorSpeechProject
    model/revision/device/compute
    analysis path + SHA-256
    segment/word counts
    benchmark RTF

  Tts: EditorTtsProject
    engine/version
    male/female voice ids
    result manifest + SHA-256
    EditorVoiceTrack
    cue/review counts

  VoiceOverrides
    cue id -> male | female

  Subtitle.Karaoke
    persisted switch
```

Missing speech/TTS cache files selectively invalidate those derived fields. A missing derived cache must not quarantine an otherwise valid Editor project.

## Explicitly absent paths

```text
NO pyannote
NO speaker identity clustering
NO Hugging Face gated diarization token
NO Demucs/stem separation
NO paid TTS API
NO localhost/WebView NghiTTS app
NO MediaPlayer-only fake voice preview
```

## Third-party gate

- Piper runtime: GPL-3.0-or-later, separate local process.
- NghiTTS reference source: Apache-2.0.
- selected `sannht/vi_voice` weight index currently says `license: unknown` for the pinned generic weights.
- therefore public release stays blocked until voice-weight redistribution/provenance is resolved.

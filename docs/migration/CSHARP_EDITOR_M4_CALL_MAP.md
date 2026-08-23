# C# Editor M4 implemented call map

Status: production path implemented on `editor-all-in-one`; consolidated Windows field verification remains.

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
            conservative F0 acoustic routing
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
       combine advisory acoustic routing per cue

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
            male_like confidence >= threshold -> male acoustic profile
            female_like confidence >= threshold -> female/base profile
            uncertain -> closest fallback + review flag
       -> BuildRhythmGroups from Whisper pauses
       -> LocalTtsInstaller
            app-managed Python
            pinned piper-tts 1.4.2 Windows wheel + SHA-256
            one pinned official Piper VAIS-1000 ONNX/config pair
            rhasspy/piper-voices@3d796cc2f2c884b3517c527507e084f7bb245aea
            exact size + SHA-256 verification
       -> internal/tts/worker.py
            load VAIS model once
            female route -> base synthesis
            male route -> synthetic pitch factor 0.84 + tempo compensation
            measure actual profiled WAV duration
            bounded Piper length_scale
            measure
            bounded FFmpeg atempo
            measure
            fit/review
            selective clip cache
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
       preflight output-drive free space
       same BuildAss
       same BuildVoiceAudioFilter
       runtime low-disk guard
       output decode/duration/audio validation
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
    male/female profile ids
    result manifest + SHA-256
    EditorVoiceTrack
    cue/review counts

  VoiceOverrides
    cue id -> male | female

  Subtitle.Karaoke
    persisted switch
```

Missing speech/TTS cache files selectively invalidate those derived fields. A missing derived cache must not quarantine an otherwise valid Editor project.

## Exact final voice pin

```text
Piper runtime: 1.4.2
Voice repository: rhasspy/piper-voices
Model revision: 3d796cc2f2c884b3517c527507e084f7bb245aea
Base voice: vi_VN-vais1000-medium
Model: 63,201,294 bytes / ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab
Config: 4,860 bytes / fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0
Female route: vais1000-female-profile-v1
Male route: vais1000-male-profile-v1 / pitch factor 0.84
Cache voice revision: <model revision>-profile-v1
```

VAIS-1000 attribution is shipped in `THIRD_PARTY_NOTICES.md`.

## Explicitly absent paths

```text
NO pyannote
NO speaker identity clustering
NO Hugging Face gated diarization token
NO Demucs/stem separation
NO paid TTS API
NO localhost/WebView NghiTTS app
NO sannht/vi_voice production weights
NO deepman3909/calmwoman3688 production download
NO MediaPlayer-only fake voice preview
```

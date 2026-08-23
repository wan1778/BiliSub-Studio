# Editor Whisper timing, voice routing and local TTS ownership

Status: M4 production implementation on `editor-all-in-one`; Windows verification pending for this checkpoint.
Date: 2026-08-23

## Product decision

Whisper is a timeline-analysis dependency, not merely a fallback SRT generator. For every source video that will use karaoke/TTS, the Editor analyzes the original audio and persists:

- word-level start/end timing;
- speech envelope per segment;
- pauses between words/speech groups;
- an advisory acoustic voice class: `male_like`, `female_like`, or `uncertain`;
- confidence and median pitch used only for TTS routing.

If an external Chinese SRT is supplied, that SRT remains authoritative for cue text/order/timecodes. Whisper timing is mapped onto the imported cue windows. If no SRT exists, Whisper may additionally produce a fallback Chinese SRT, but that is secondary to timing analysis.

The voice class is a timbre/pitch routing signal. It is not a claim about a person's biological sex or gender identity. A manual per-cue `male`/`female` override is authoritative.

## Explicitly removed scope

The Editor does not implement:

- pyannote diarization;
- SPEAKER_00/SPEAKER_01 identity clustering;
- gated diarization tokens/models;
- Demucs/stem separation;
- a “remove Chinese dialogue but preserve original music” promise.

Source audio remains `keep`, `duck`, or `mute`. `mute` means the complete original mix is muted.

## Whisper timing implementation

`internal/asr/worker.py` runs the pinned local faster-whisper model with `word_timestamps=True` and VAD. It returns complete word records instead of letting C# discard them.

`LocalAsrService` persists checkpoint schema 2, then writes an app-owned speech-analysis document under `Data/Projects/Speech`. `EditorSpeechAnalysisDocument` verifies SHA-256 and maps the analysis to whichever SRT is active.

Per cue, the mapped result owns:

- speech start/end inside the cue;
- leading/trailing silence;
- word timings;
- internal pauses >= 180 ms;
- voice-class suggestion/confidence/pitch.

Changing the SRT does not invalidate the video-level Whisper analysis; the timing is remapped. Changing the source video/model revision invalidates the analysis.

## Karaoke ASS

`VideoEditorService.BuildAss` is the single ASS owner for both render and user-exported karaoke ASS.

When word timing exists and Karaoke is enabled:

- dialogue start/end uses the detected speech envelope;
- Vietnamese display tokens receive ASS `\\kf` durations derived from the original word/pause rhythm;
- the duration allocator preserves total speech time even when Chinese and Vietnamese token counts differ.

This is rhythm alignment, not a false one-to-one semantic claim between Chinese and Vietnamese words.

Preview slicing must shift cue, word and pause timestamps together. Final export and the saved `*.karaoke.ass` use the same builder.

## Local NghiTTS/Piper implementation

Reference repository: `nghimestudio/nghitts`.

BiliSub Studio does not embed its Vue/Vite application, Web Workers, Cloudflare endpoints, WebView or localhost server. The native app uses reviewed local Piper-compatible ONNX voice assets in an app-managed Python runtime.

Pinned runtime for this checkpoint:

- Piper: `piper-tts 1.4.2` Windows x64 wheel;
- wheel SHA-256: `9c4a3a11f5889ea9d0df4414dce2bd9bee5ce7d9cf604c8fd5e307441d4c031f`;
- Python: app-managed 3.12 bootstrap already owned by BiliSub Studio.

Pinned generic voice candidates:

- male: `deepman3909.onnx` — 63,516,050 bytes — SHA-256 `1fb3a404e9927c87367d4175e8cad24ffc6d9959af29888c38682e5ec621056c`;
- female: `calmwoman3688.onnx` — 63,516,050 bytes — SHA-256 `8db60d8afc50dc0921fd3a1b0b942813f44cc3744dbe2534617f2b8726096e7e`;
- both configs: 4,855 bytes — SHA-256 `971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8`;
- voice source revision: `sannht/vi_voice@62e57b18157ed213b3863a7a8a35b14d3404554b`.

The mirror's index identifies the voice-weight license as `unknown`. Therefore these weights are allowed only for integration/field verification on this draft branch until redistribution rights are resolved. The public release gate must remain closed. Celebrity-named voice models are not production defaults.

The Piper runtime is GPL-3.0-or-later and is executed as a separate app-managed local process. Third-party notices/license obligations must be completed before public distribution.

## Vietnamese text and rhythm fit

`VietnameseTtsTextNormalizer` normalizes common Vietnamese TTS forms locally (numbers, dates, time, decimals, percentages). No online API is required.

`LocalTtsService` maps each translated cue to Whisper rhythm groups. The user override wins; otherwise a confident `male_like`/`female_like` suggestion selects the matching generic voice. Uncertain routing is marked for review.

For each rhythm group, `internal/tts/worker.py`:

1. synthesizes baseline audio;
2. measures actual WAV duration;
3. retries Piper `length_scale` inside 0.86–1.16;
4. measures again;
5. applies bounded pitch-preserving FFmpeg `atempo` inside 0.92–1.08 when needed;
6. measures again;
7. marks `fit` or `review` instead of forcing extreme speed.

No SRT timecode/order is silently rewritten.

## Cache and long-video behavior

TTS clip keys include timing algorithm version, Piper version, voice revision, cue/group identity, selected voice, group timing and normalized Vietnamese text.

Clips are cached per rhythm group. The worker assembles app-owned 300-second block WAV caches and one seekable `voice-master.flac`. This avoids thousands of FFmpeg input arguments on long films while retaining selective clip/block regeneration.

Changing translation text or a per-cue voice override invalidates the TTS state/track, not the source video, SRT translation, Blur regions, or Whisper analysis. Missing speech/TTS cache files invalidate only the corresponding derived state rather than quarantining the whole Editor project.

## Preview/render parity

Voice is not a MediaPlayer-volume approximation.

`VideoEditRequest` carries the app-owned `EditorVoiceTrack`. `VideoEditorService` uses one source/TTS audio graph for both final render and `Xem bản chỉnh`:

- `keep`: source + TTS;
- `duck`: reduced complete source mix + TTS;
- `mute`: TTS only.

The 12-second proxy seeks the same source time in both the video and the seekable voice master. Karaoke cue/word/pause timing is shifted into proxy time by `BuildPreviewSlice`.

## User-facing Audio inspector

The compact Audio inspector owns:

- `Phân tích nhịp + Nam/Nữ`;
- `Tạo voice Việt local`;
- Karaoke on/off;
- current-cue automatic/male/female override;
- progress/cancel/review state;
- existing source Keep/Duck/Mute controls.

No speaker identity list and no new long navigation tab are introduced.

## Release gate

This implementation is not field-PASS until all are true:

- C# contract tests include word timing, pause mapping, karaoke, voice override persistence, NghiTTS manifest, timing grouping and Keep/Duck/Mute voice graph parity;
- WinUI/XAML build and startup/layout smoke pass;
- installer/custom-path/uninstall smoke and candidate packaging pass;
- processed preview audibly matches final render on Windows;
- male/female heuristic is measured on representative Mandarin film dialogue and uncertain cases remain reviewable;
- selected voice-weight redistribution/provenance is resolved;
- one consolidated Editor real-machine field test passes.

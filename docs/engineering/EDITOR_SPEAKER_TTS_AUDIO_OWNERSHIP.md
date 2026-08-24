# Editor Whisper timing, voice routing and local TTS ownership

Status: production path implemented on `editor-all-in-one`; consolidated Windows field verification remains.
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

## Whisper timing and acoustic routing

`internal/asr/worker.py` runs the pinned local faster-whisper model with `word_timestamps=True` and VAD. It returns complete word records and a lightweight local F0 routing estimate.

The advisory F0 policy is intentionally conservative:

- median pitch <= 155 Hz -> `male_like`;
- median pitch >= 185 Hz -> `female_like`;
- middle band -> `uncertain`;
- low-energy/unvoiced/too-short evidence -> `uncertain`;
- uncertain confidence is kept below the automatic-routing threshold.

`LocalAsrService` persists app-owned speech analysis and `EditorSpeechAnalysisDocument` verifies SHA-256, maps words to active SRT cues, combines overlapping segment evidence and preserves uncertain cases for user review.

Changing the SRT does not invalidate the video-level Whisper analysis; timing is remapped. Changing the source video/model revision invalidates the analysis.

## Karaoke ASS

`VideoEditorService.BuildAss` is the single ASS owner for final render, processed preview and user-exported karaoke ASS.

When word timing exists and Karaoke is enabled:

- dialogue start/end uses the detected speech envelope;
- Vietnamese display tokens receive ASS `\\kf` durations derived from original word/pause rhythm;
- the allocator preserves total speech time when Chinese and Vietnamese token counts differ.

This is rhythm alignment, not a false one-to-one semantic mapping.

## Final local Piper/VAIS implementation

NghiTTS was reviewed as an architectural reference, but its previously evaluated generic `sannht/vi_voice` weights are not production assets because the reviewed weight index did not provide a sufficiently clear release license.

Production pins one official Piper Vietnamese model:

- voice collection: `rhasspy/piper-voices`;
- model revision: `3d796cc2f2c884b3517c527507e084f7bb245aea`;
- base voice: `vi_VN-vais1000-medium`;
- model collection: MIT;
- source dataset: VAIS-1000, CC BY 4.0;
- ONNX: 63,201,294 bytes, SHA-256 `ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab`;
- config: 4,860 bytes, SHA-256 `fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0`;
- Piper runtime: `piper-tts 1.4.2` Windows x64 wheel, SHA-256 `9c4a3a11f5889ea9d0df4414dce2bd9bee5ce7d9cf604c8fd5e307441d4c031f`.

The base voice is loaded once. Two acoustic routing profiles are exposed:

- `vais1000-female-profile-v1`: original base synthesis;
- `vais1000-male-profile-v1`: deterministic synthetic lower-pitch profile using pitch factor `0.84` with tempo compensation before timing fit.

The male route is a synthetic audio profile, not a real-person identity/likeness claim.

`THIRD_PARTY_NOTICES.md` is packaged with the Windows app and records the VAIS/Piper attribution.

## Vietnamese text and rhythm fit

`VietnameseTtsTextNormalizer` normalizes common Vietnamese TTS forms locally. No online API is required.

`LocalTtsService` maps each translated cue to Whisper rhythm groups. Manual override wins. A confident acoustic class selects the matching route; uncertain routing uses a fallback profile but remains review-marked.

For each rhythm group, `internal/tts/worker.py`:

1. synthesizes baseline Piper audio;
2. applies the selected acoustic profile;
3. measures actual WAV duration;
4. retries Piper `length_scale` inside 0.86–1.16;
5. measures again;
6. applies bounded FFmpeg `atempo` inside 0.92–1.08 when needed;
7. measures again;
8. marks `fit` or `review` instead of forcing extreme speed.

No SRT order/timecode is silently rewritten.

## Cache and long-video behavior

TTS clip keys include timing algorithm version, Piper version, voice/profile revision, cue/group identity, selected route, group timing and normalized Vietnamese text.

The voice revision combines the immutable model revision with `profile-v1`, preventing beta.36 NghiTTS clips from being reused after the VAIS migration. Future profile changes must increment the profile revision.

Clips are cached per rhythm group. The worker assembles app-owned 300-second block WAV caches and one seekable `voice-master.flac`. Changing translated text or a per-cue override invalidates TTS state, not valid Blur regions, SRT translation or Whisper analysis. Missing derived caches selectively invalidate their own stage.

## Preview/render parity

Voice is not a MediaPlayer-volume approximation.

`VideoEditRequest` carries the app-owned `EditorVoiceTrack`. `VideoEditorService` uses the same source/TTS audio semantics for processed preview and final render:

- `keep`: complete source mix + Vietnamese TTS;
- `duck`: reduced complete source mix + Vietnamese TTS;
- `mute`: Vietnamese TTS only.

Every internal preview segment seeks the same source time in video and `voice-master.flac`. Karaoke cue/word/pause timing is shifted into segment time by `BuildPreviewSlice`; consecutive segments remain an implementation detail behind continuous full-video playback.

## User-facing Audio inspector

The compact Audio inspector owns:

- `Phân tích nhịp + Nam/Nữ`;
- `Tạo voice Việt local`;
- Karaoke on/off;
- current-cue automatic/male/female override;
- progress/cancel/review state;
- source Keep/Duck/Mute controls.

No speaker identity list and no new long navigation tab are introduced.

## Release gate

The branch is not release-PASS until all are true:

- C# contracts cover word timing, pause mapping, karaoke, voice override persistence, exact VAIS/Piper manifest, timing grouping and Keep/Duck/Mute voice graph parity;
- static verification rejects the retired `sannht/vi_voice` production path and pins the male profile factor;
- WinUI/XAML build and startup/layout smoke pass;
- installer/custom-path/uninstall smoke and candidate packaging pass;
- processed preview audibly matches final render on Windows;
- uncertain acoustic cases remain reviewable/overrideable;
- third-party attribution ships with the Windows runtime;
- one consolidated Editor real-machine field test passes.

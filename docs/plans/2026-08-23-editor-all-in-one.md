# Editor All-in-One for Chinese film localization

Status: M1-M6 production path implemented on draft branch `editor-all-in-one`. Automated Windows verification and one consolidated owner field test remain the release gates.
Date: 2026-08-23

## 1. Product boundary

BiliSub Studio Editor is a guided Chinese-film localization workstation, not a generic nonlinear editor.

Supported source flows:

1. **Video + Chinese SRT**
   - import strict SRT;
   - analyze source audio with local Whisper for word/pause timing and acoustic Nam/Nữ routing;
   - translate locally with Qwen + bundled `Dịch Trung Tu Tiên` skill;
   - generate Vietnamese voice locally;
   - preview the processed result;
   - export the final video.
2. **Video without SRT**
   - local faster-whisper analyzes/transcribes Chinese;
   - generated Chinese SRT becomes the fallback source subtitle;
   - continue through the same translation/voice/preview/export path.

Blur/Mosaic/Cover remains available for masking burned-in source subtitles and watermarks.

## 2. Binding constraints

- C# + .NET 10 + WinUI 3, Windows x64.
- No Go production path.
- No browser/WebView UI, localhost backend, Ollama, or second BiliSub executable.
- Required AI/TTS path is local/free; no paid API dependency.
- User does not install system Python, FFmpeg, model runtimes, or pip dependencies manually.
- App-managed tools/models are pinned by version/revision, expected size and SHA-256.
- Source media is never overwritten.
- Long jobs own and reap their complete child-process trees.
- Project/checkpoint writes are atomic and resumable where the stage supports resume.
- Missing derived caches invalidate only the affected derived stage.
- Public beta/merge is forbidden until automated gates and the consolidated Windows field test pass.

## 3. Explicitly removed scope

These are **not production requirements** and must not be reintroduced from older planning notes:

- pyannote or persistent speaker diarization;
- `SPEAKER_00`/speaker identity clustering;
- gated Hugging Face diarization tokens;
- Demucs or vocal/non-vocal stem separation;
- “remove Chinese dialogue while keeping original music”;
- Edge/online/paid TTS as a required provider;
- celebrity/reference-person default voices.

The app only needs an advisory per-cue acoustic route:

- `male_like`;
- `female_like`;
- `uncertain`.

Manual `male`/`female` override is authoritative. The acoustic class is used only for TTS routing and is not presented as a factual gender-identity determination.

## 4. Current production ownership

```text
EditorPage
  -> EditorProjectStore (schema 5, autosave/reopen/source fingerprint)
  -> EditorSubtitleDocument (strict SRT)
  -> LocalSubtitleTranslationService
       -> app-managed llama.cpp
       -> Qwen3-8B Q4_K_M
       -> bundled Dịch Trung Tu Tiên skill
  -> LocalAsrService
       -> app-managed faster-whisper
       -> word timestamps + VAD + fallback Chinese SRT
       -> local F0 acoustic routing
  -> LocalTtsService
       -> app-managed Piper 1.4.2
       -> licensed VAIS-1000 Piper model
       -> female base profile / synthetic lower-pitch male profile
       -> measured timing fit
       -> clip/block cache + voice-master.flac
  -> VideoEditorService
       -> Blur/Mosaic/Cover
       -> ASS/karaoke
       -> source Keep/Duck/Mute + TTS
       -> processed full-video preview through chained internal segments
       -> validated atomic final render
```

No production stage exposes an HTTP server or fixed local port.

## 5. Project persistence and invalidation

Schema 5 persists:

- source fingerprint;
- stable regions;
- strict source/translated subtitle cues and placement;
- source audio mode;
- Whisper analysis provenance and SHA-256;
- TTS engine/profile/manifest/track provenance;
- per-cue voice overrides;
- karaoke setting.

Invalidation rules:

| Change | Preserve | Invalidate |
|---|---|---|
| Preview position/window size | all artifacts | nothing |
| Blur region geometry/effect | subtitle/Whisper/TTS | processed preview/final render |
| New/changed SRT | source media + valid Whisper analysis | translation/TTS/render |
| Translation text | Whisper analysis | affected TTS/render |
| Manual Nam/Nữ override | translation + Whisper analysis | TTS/render |
| Keep/Duck/Mute | cues + TTS | preview mix/render |
| Missing TTS cache | source/subtitle/Whisper | TTS only |
| TTS engine/profile revision changed | source/subtitle/Whisper/overrides | TTS only |
| Source media fingerprint changed | harmless project name/output name | every derived artifact |

A project created by the retired beta.36 NghiTTS path must not restore its old voice track after upgrading to the VAIS profile path.

## 6. Translation path

`EditorSubtitleDocument` preserves SRT block order, numbering and timing.

`TranslationSkillBundle` verifies the exact bundled `Dịch Trung Tu Tiên` ZIP by hash, entry/path/size constraints and required content. `LocalSubtitleTranslationService` uses the pinned local Qwen model through the app-owned llama.cpp process and returns translations keyed to stable cue IDs.

Translation checkpoints preserve completed batches. Translation changes clear stale TTS state but do not force a new Whisper timing pass when the source video is unchanged.

## 7. Whisper timing and acoustic routing

Whisper runs even when an external SRT exists because TTS/karaoke need source-audio timing evidence.

The pinned local faster-whisper path provides:

- word start/end timestamps;
- VAD speech envelope;
- internal pauses;
- source offsets;
- fallback Chinese SRT when no source SRT exists.

`internal/asr/worker.py` also computes a conservative local F0 routing estimate:

- <=155 Hz -> `male_like`;
- >=185 Hz -> `female_like`;
- 155-185 Hz -> `uncertain`;
- insufficient voiced evidence -> `uncertain`.

Uncertain cases remain reviewable and manual overrideable.

## 8. Licensed local Vietnamese TTS

NghiTTS was used only as an architectural reference. The previously evaluated `sannht/vi_voice` weights (`deepman3909`, `calmwoman3688`) are rejected from production because their reviewed weight license was not sufficiently clear.

Production uses:

- `rhasspy/piper-voices`;
- exact model revision `3d796cc2f2c884b3517c527507e084f7bb245aea`;
- `vi_VN-vais1000-medium`;
- ONNX 63,201,294 bytes, SHA-256 `ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab`;
- config 4,860 bytes, SHA-256 `fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0`;
- Piper `1.4.2` Windows runtime, pinned wheel SHA-256.

Profiles:

- `vais1000-female-profile-v1`: base voice;
- `vais1000-male-profile-v1`: deterministic synthetic lower-pitch profile, pitch factor `0.84` with tempo compensation.

Both routes use one loaded model. The male route is a synthetic acoustic transform, not a real-person voice identity.

`THIRD_PARTY_NOTICES.md` ships with the app and records the required attribution.

## 9. TTS timing fit and cache

For each rhythm group:

1. normalize Vietnamese text;
2. synthesize baseline WAV;
3. apply selected acoustic profile;
4. measure actual duration;
5. retry Piper `length_scale` within 0.86-1.16;
6. measure again;
7. use bounded FFmpeg `atempo` within 0.92-1.08 if needed;
8. measure final audio;
9. mark `fit` or `review` instead of forcing extreme speed.

TTS never rewrites SRT order/timecodes to hide an impossible fit.

Cache identity includes timing algorithm, Piper version, immutable model/profile revision, cue/group identity, selected route, timing and normalized text. The worker also owns a profile-revision marker so incompatible old clip/block/master caches are removed before reuse.

Long projects use per-group clips, 300-second block WAV caches and one seekable `voice-master.flac` for preview/export.

## 10. Source audio policy

Only three source-audio modes exist:

- **Keep**: complete original source mix + Vietnamese TTS.
- **Duck**: reduced complete original source mix + Vietnamese TTS.
- **Mute**: Vietnamese TTS only; original source audio is removed.

No mode claims to isolate Chinese speech from music/effects.

MediaPlayer mute/volume is monitor-only. It is not the render policy.

## 11. Processed preview contract

The editable surface uses a processed still frame for direct manipulation.

`Xem bản chỉnh` plays from the current playhead through the full source using app-owned temporary H.264/AAC proxy segments and the same semantic builders as final render:

- current regions;
- subtitle/karaoke ASS;
- source Keep/Duck/Mute;
- current TTS master track.

Intersecting region/cue/word/pause timing is clipped and shifted into each internal preview window. The preview clock maps back to source time and playback automatically continues with the next segment. Editing is locked while the proxy plays; stopping, reaching the full source end or closing the page removes temporary proxies and restores the editable frame.

## 12. Final export and hardening

Final render:

- writes to a sibling `.rendering` artifact;
- never targets the source file;
- applies the same subtitle/effect/audio semantics as preview;
- verifies output streams/duration before promotion;
- atomically promotes only a verified output;
- removes partial render on failure/cancel.

Disk safety:

- preflight requires approximately `2 x source size + 512 MB` when reliable drive information exists;
- live free-space check runs approximately every 3 seconds;
- render stops before free space drops below the 512 MB reserve;
- no fake capacity estimate is invented for unsupported/network volume reporting.

Same-path source replacement is detected by source fingerprint and archives/resets stale derived project state.

## 13. UI contract

The Editor keeps one preview and compact right-side inspectors:

- Subtitle;
- Blur;
- Audio;
- Export.

Important behavior:

- SRT selection and local-AI preparation are available before video selection;
- subtitle and blur rectangles own separate direct-manipulation modes;
- regions can be create/select/move/resize/edit and Undo/Redo;
- subtitle placement is normalized and resize-safe;
- Audio inspector provides Whisper timing, TTS generation, karaoke, current-cue Auto/Nam/Nữ override, progress/cancel and Keep/Duck/Mute;
- preview/export use the same project state.

The Editor remains guided and compact rather than becoming a Premiere-style general editor.

## 14. Automated release gate

Every production checkpoint must pass:

- static source/call-map contracts;
- exact generated C# code map check;
- Core contracts (50-test suite count retained);
- CDN/subtitle regressions;
- WinUI/XAML build;
- real published-EXE startup/layout smoke;
- installer root/custom-path/install/uninstall smoke;
- candidate packaging and evidence artifact;
- public beta/update publishing step skipped while PR remains draft.

Core coverage includes:

- project save/reopen/corrupt quarantine;
- source-fingerprint replacement isolation;
- selective missing-cache invalidation;
- retired TTS profile invalidation after upgrade;
- Whisper word/pause mapping;
- acoustic routing and manual override persistence;
- licensed VAIS/Piper manifest/profile/cache contracts;
- timing grouping and normalization;
- Keep/Duck/Mute + TTS graph parity;
- processed-preview slicing;
- final output validation;
- process-tree cleanup;
- disk-space safety.

## 15. Consolidated owner field test

The owner is not asked to test intermediate checkpoints. After the final Windows candidate passes, one real-machine test covers:

1. Open a real Chinese video and Chinese SRT.
2. Run local translation and verify several names/terms.
3. Run `Phân tích nhịp + Nam/Nữ`.
4. Override at least one cue Nam/Nữ and generate local Vietnamese voice.
5. Play `Xem bản chỉnh` through multiple internal segment boundaries and confirm continuous full-video playback plus visible/audible subtitle/effects/voice before export.
6. Spot-check Keep, Duck and Mute behavior.
7. Create/move/resize Blur/Mosaic/Cover and verify preview.
8. Close/reopen once and confirm project/override/valid voice state restores.
9. Cancel one long operation and confirm the app recovers without orphan processes/partial output.
10. Export a final video and verify it plays with the expected TTS/subtitle/effects/audio policy.
11. Verify the video-only path can generate fallback Chinese SRT with local Whisper.

Only after this consolidated field test passes may PR #14 be considered for merge/public beta.

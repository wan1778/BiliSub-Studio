# NghiTTS audit and final local voice-pin decision

Audit date: 2026-08-23
Status: production model decision closed for the Editor field candidate.

## What NghiTTS contributed

Reference: `nghimestudio/nghitts`.

NghiTTS demonstrated a useful fully local Vietnamese TTS design based on Piper-compatible ONNX models, Vietnamese text preprocessing, adjustable synthesis speed and WAV output. BiliSub Studio used that work as an architecture/reference input only.

BiliSub Studio does **not** embed the NghiTTS Vue/Vite app, browser Web Worker, WebView, Node runtime, localhost API or cloud inference service. The native application owns its local runtime, worker lifecycle, checksums, cache, timing fit and preview/export audio graph.

## Rejected production weights

The initially evaluated generic pair was:

- `deepman3909`
- `calmwoman3688`
- source: `sannht/vi_voice`

Although the exact files could be pinned by revision, size and SHA-256, the reviewed weight index identifies their model license as `unknown` and the repository/model card did not establish a sufficiently clear redistribution/downloader license for release.

Decision: these weights are **not** downloaded or distributed by the production path. Celebrity/reference-person voice models are also excluded from default production use.

## Final production voice source

BiliSub Studio uses the official Piper voice collection:

- repository: `rhasspy/piper-voices`
- exact model revision: `3d796cc2f2c884b3517c527507e084f7bb245aea`
- voice: `vi_VN-vais1000-medium`
- model collection license: MIT as declared by the upstream repository metadata
- training dataset: VAIS-1000
- dataset license: CC BY 4.0
- speaker profile documented for the dataset/model: one Vietnamese female/Northern voice
- sample rate: 22,050 Hz

Pinned files:

- `vi_VN-vais1000-medium.onnx`
  - size: 63,201,294 bytes
  - SHA-256: `ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab`
- `vi_VN-vais1000-medium.onnx.json`
  - size: 4,860 bytes
  - SHA-256: `fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0`

The Windows app downloads these exact immutable files once, verifies byte size and SHA-256, and then runs synthesis locally/offline.

## Male/female routing without a second ambiguous model

The product still needs two acoustic routes because the owner requested only practical Nam/Nữ voice selection, not speaker identity.

BiliSub Studio therefore exposes two deterministic profiles from the one licensed VAIS-1000 base model:

- `vais1000-female-profile-v1`: original base synthesis.
- `vais1000-male-profile-v1`: synthetic lower-pitch profile generated locally.

For the male profile, `internal/tts/worker.py` applies a fixed pitch factor `0.84` (approximately -3 semitones) using FFmpeg `asetrate` + `aresample`, then compensates tempo by `1 / 0.84` so the transform itself does not intentionally lengthen the cue. The normal timing-fit stage measures the transformed WAV afterwards.

This route is described as a synthetic acoustic profile. It is not presented as a recording, identity or likeness of a real male speaker.

The worker detects that both routes point to the same ONNX/config pair and loads the Piper model only once to avoid duplicate model RAM.

## Timing fit and cache identity

The production sequence remains:

1. normalize Vietnamese text locally;
2. map cue to Whisper speech/pause timing;
3. choose automatic acoustic route or authoritative manual override;
4. synthesize baseline Piper WAV;
5. apply the selected VAIS acoustic profile;
6. measure actual decoded WAV duration;
7. retry Piper `length_scale` only within 0.86–1.16;
8. measure again;
9. apply bounded `atempo` only within 0.92–1.08 when needed;
10. measure final duration;
11. mark `fit` or `review` instead of forcing extreme speed.

No SRT order or timecode is silently rewritten.

The TTS cache fingerprint includes a voice revision string containing both the immutable model revision and `profile-v1`. Therefore the beta.36 NghiTTS clip cache cannot be reused after the production voice-source change, and a future acoustic-profile change must increment the profile revision.

## Attribution

`THIRD_PARTY_NOTICES.md` records the Piper/VAIS source, exact model revision, hashes and CC BY 4.0 dataset attribution. The App project packages this notice into the Windows publish output.

## Final production constraints

- local/offline inference after first verified download;
- no paid TTS API;
- no localhost service;
- no WebView/browser TTS runtime;
- no celebrity/reference-person default voice;
- no pyannote/diarization;
- no Demucs/stem separation;
- preview and export consume the same generated `voice-master.flac` semantics.

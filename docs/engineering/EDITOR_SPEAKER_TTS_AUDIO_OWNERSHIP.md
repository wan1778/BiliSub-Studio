# Editor voice classification and TTS ownership

Status: owner-revised M4 direction, superseding the earlier speaker-diarization/stem plan.
Date: 2026-08-23

## Product decision

BiliSub Studio does not need to identify persistent speakers such as SPEAKER_00/SPEAKER_01. The Editor only needs a practical acoustic voice class for choosing a Vietnamese TTS voice:

- male-like voice
- female-like voice
- uncertain

This is a voice/timbre classification used for TTS routing. It must not be presented as a claim about a person's gender identity.

## Removed scope

The following are removed from the Editor plan:

- pyannote diarization
- speaker clustering / persistent anonymous speaker IDs
- Hugging Face gated diarization setup
- Demucs or any other source/stem separation stage
- preserve-music/remove-original-dialogue mode

Source audio remains only the existing keep / duck / mute policy. If source audio is muted, the complete original mix is muted. The product must not imply that dialogue alone was removed.

## Voice-class analysis

The source SRT/ASR cue timing already supplies the speech windows. For each usable cue, Core may extract a bounded mono analysis segment and estimate an acoustic voice class.

Required output per cue:

- class: male | female | uncertain
- confidence: 0..1
- analysis version/fingerprint
- optional manual override

The authoritative value is the user's override when present. Low-confidence analysis must never be silently forced to male/female.

The first implementation should prefer a small language-independent acoustic classifier/feature pipeline over a diarization stack. It must run locally, require no paid API and use app-owned FFmpeg/runtime components. The exact classifier is not pinned by this architecture checkpoint; it must be benchmarked on real Mandarin film dialogue before production pinning.

## NghiTTS audit and intended reuse

Reference repository: https://github.com/nghimestudio/nghitts

The repository implements client-side Piper-compatible Vietnamese TTS using ONNX models, Vietnamese text normalization, phonemization, chunking, speed control and WAV generation. Its code repository is Apache-2.0.

BiliSub Studio remains native C# + WinUI 3. It must not embed the Vue/Vite web application or create a localhost/web backend. Reuse is limited to reviewed algorithms, model/config formats and compatible assets.

Initial generic Vietnamese voice candidates from the repository documentation:

- calmwoman3688: female voice, about 60.6 MB ONNX + JSON
- deepman3909: male voice, about 60.6 MB ONNX + JSON

Celebrity-named voices are not default candidates. The source-code license does not by itself prove that separately hosted model weights, datasets or a person's voice likeness are cleared for BiliSub Studio distribution. Each selected model must have explicit provenance/license review, exact size and SHA-256 before bundling or app-managed download.

## TTS provider policy

Primary direction: local NghiTTS/Piper-compatible ONNX inference.

Fallback policy:

- the Editor must remain usable without paid APIs;
- online TTS is optional only if free and not required for project recovery;
- no API key, subscription or per-character billing may become a mandatory production dependency.

The production TTS owner will expose one provider-neutral contract so another local model can replace a voice without changing Editor project data.

## Timing fit

For each translated cue:

1. synthesize the Vietnamese text;
2. measure actual WAV duration;
3. adjust the TTS speed within a safe voice-specific range;
4. if needed, apply only bounded post time-stretch;
5. if it still cannot fit the cue naturally, mark the cue for review.

The system must not force extreme playback rates merely to fit a subtitle window.

## Cache and invalidation

TTS cache keys include at least:

- translated cue text
- selected voice/model fingerprint
- speed/rate settings
- text-normalization version
- timing-fit version

Changing one cue text invalidates only that cue's TTS. Changing the male/female default voice invalidates only cues routed to that class unless a cue has an explicit manual voice assignment.

## Preview/render contract

TTS is not a MediaPlayer-only effect.

Both `Xem bản chỉnh` and final export must consume the same audio render plan:

- source mix keep/duck/mute
- timed Vietnamese TTS WAV clips
- the same gain/timing settings

Preview slices must clip and shift TTS cue timing exactly as subtitle/region timing is already clipped and shifted.

## User-facing behavior

The Audio/Voice inspector should remain simple:

- Giọng nam: selected local voice
- Giọng nữ: selected local voice
- Phân loại tự động: on/off
- per-cue override when needed
- warning count for uncertain voice class or timing-fit failure

No speaker identity list is required.

## Release gate

No M4 implementation is field-PASS until:

- local voice-class analysis is measured on real Mandarin film dialogue;
- selected generic NghiTTS/Piper model files have explicit provenance/license and exact SHA-256;
- TTS cache/retry/timing-fit contracts pass;
- processed preview audibly matches final render;
- Windows build, contract tests, XAML/startup/layout smoke, installer smoke and candidate packaging all pass;
- the consolidated Editor field test passes.

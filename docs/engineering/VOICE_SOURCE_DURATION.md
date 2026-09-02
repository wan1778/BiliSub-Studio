# Whole-cue NGHI/Piper timing policy

## Production policy

The current cache/timing revision is `whole-cue-piper-natural-first-v17`. Every Vietnamese cue containing at least one Unicode letter or number is normalized and sent to the pinned NGHI/Piper model as one complete utterance. Punctuation-, symbol-, invisible-mark- and emoji-only cues remain in SRT but are omitted from voice. Speech is never split or tail-cut to make it fit.

The duration controller is Piper-first with a bounded pitch-preserving fallback:

- Every ordinary cue and sentence group is first synthesized at the verified model base rate. If that complete natural-rate speech fits the SRT window, it is accepted immediately at measured `1.0x`; no second fast pass is allowed.
- Only an actually overlong cue is synthesized again. Piper targets the smallest measured speed needed to fit that cue's SRT window, with 0.5% frame headroom for rounding/model-duration variation. There is no fixed `1.30x` floor.
- Piper `length_scale` may fall as far as `0.30`, but this value is never interpreted as a speed multiplier. The native-reference PCM divided by selected retained PCM is the authoritative measured rate.
- If complete Piper output still exceeds the source window at `length_scale=0.30`, FFmpeg `atempo` compresses the complete retained waveform. The factor is calculated from measured frame counts and retried at most six times. `atempo` preserves pitch; `atrim`, tail truncation, `asetrate` and Rubber Band are forbidden.
- Every materially rate-adjusted result is marked for review so listening remains an explicit field gate.

Groups contain at most 512 cues and 300 seconds. Any positive source gap splits the group; a group cannot borrow time from later silence or unrelated dialogue. Allocation preserves the complete transformed waveform, source order, the exact group boundary, and exact 22,050 Hz PCM frame counts. A high corruption guard remains at `100x`; practical speed is derived from the cue's measured native length and timecode instead of a fixed `1.40x` ceiling.

## OCR and ASR ownership

SRT is the sole timing and cue-presence authority. Every project voice manifest uses the cue's complete SRT start/end interval. ASR never shortens, shifts, replaces, or removes a speakable SRT cue. Voice planning performs two SRT-only cleanup steps; neither rewrites or deletes cues from the exported SRT:

1. It collapses only proven touching/overlapping OCR flicker duplicates while preserving normal repeated dialogue and distinct sentence fragments.
2. It removes cues with no speakable Unicode letters or numbers from voice only. Every remaining cue is retained regardless of ASR overlap.

ASR mapping is advisory metadata for voice analysis/classification only. `BuildWholeCue` always emits the exact SRT cue window. Internal Whisper pauses never create additional Piper calls, suppress SRT cues, or affect TTS placement.

## Frame integrity and cache

Every attempt calls `voice.synthesize(text, SynthesisConfig(length_scale=...))` with the complete normalized text. Only verified trailing silence may be removed; unused target frames are padded with silence. Worker metadata records source, generated, trimmed, padding and target frame counts. Core independently validates cue order, placement, mono PCM16 at 22,050 Hz, RIFF frame count, clip/master SHA-256, non-clipping state, group boundaries and exact master duration.

Cache identity includes the exact SRT envelope, timing source, normalized text, worker hash and v17 policy revision. The revision change deliberately invalidates v16 clips because all of them were generated under the removed fixed-fast-floor policy.

## Verification boundary

Offline worker contracts cover Piper-first fitting, bounded dynamic-tempo metadata, full-waveform allocation, exact padding, trailing-silence removal, cache identity, malformed metadata and the absence of cutting/pitch-shifting filters. Core property tests cover randomized ASR/SRT mappings, OCR A/B/A cleanup, preservation of normal cues, and prove that ASR never changes a speakable cue's SRT-owned window.

The earlier v16 field run proved that cues 23-24 can require approximately `1.54x` and that a bounded `atempo` escape hatch is needed after Piper saturates. It also exposed a policy defect: v16 forced every cue toward at least `1.30x`, including cues whose natural-rate speech already fit. V17 preserves the same complete-waveform and exact-frame guarantees while removing that floor. A full project content preflight found 27 punctuation/symbol-only cues among 16,903; all are removed before ASR/TTS while the source SRT remains unchanged. `1 / length_scale` is never used as the audible-speed estimate; measured PCM frame ratios remain authoritative.

These checks prove timing rules, provenance and signal integrity. Audible clarity remains a user listening gate and must not be reported as a voice-quality PASS until the generated preview has been heard.

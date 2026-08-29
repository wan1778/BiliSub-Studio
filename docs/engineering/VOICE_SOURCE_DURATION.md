# Whole-cue voice duration matching with non-aborting fallback

## Requested behavior

Match the full Vietnamese utterance to the corresponding source-speech duration. A successful two-second source window produces a 44,100-frame WAV at 22,050 Hz. Preserve the entire cue text and never obtain the target duration by cutting off the end. Prefer model-native rate control and pooled sentence timing; when even the complete SRT window is physically too short, use pitch-preserving tempo compression as a final fallback instead of aborting the entire voice job at one cue. This policy does not change the voice/model/runtime packages, alter OCR/ASR inference, or rewrite imported SRT.

The primary path remains model-native whole-cue resynthesis, with no splitting by Whisper pauses. Field evidence later showed that hard native-rate limits still failed on valid dense subtitles (first cue 9, then cue 17, with thousands of similarly dense cues in the supplied SRT). The current policy therefore keeps native fitting as the quality-first path but adds a narrowly owned `atempo` fallback that consumes the complete synthesized WAV and preserves pitch. It is not a tail cut or a substitute model.

## Timing ownership

Core verifies the existing Whisper analysis SHA and source identity, then maps its words to the imported cue. `BuildWholeCue` keeps the original SRT identity/start/end/text but adds a separate `voice_start`/`voice_end` envelope, bounded by the cue, from the earliest/latest mapped word. Internal pauses remain inside the envelope: they do not create more TTS calls or get reproduced individually. Missing words, mismatched cue identity or an empty intersection is an actionable error, not a fabricated SRT fallback.

This matches the measured **Whisper envelope within a cue**, not independently verified ground-truth speech boundaries. Incorrect SRT associations or ASR timestamps still need field review. One cue spanning several utterances is fitted as one continuous whole-cue window; per-word prosody matching is out of scope.

The text-only sample explicitly uses `timing_source: sample`. It keeps its natural duration when it fits the existing ten-second sample window. Longer samples are fitted without clipping. Project cues require `timing_source: whisper`; the sample exception cannot be applied to arbitrary project cues.

## Fitting and frame accounting

Piper is loaded once per worker and reused for all cues and attempts. Each attempt calls `voice.synthesize(text, syn_config=SynthesisConfig(length_scale=...))` with the complete normalized text. Only `length_scale` is overridden; the verified model/config bytes, generator/width noise, speaker and package pins remain unchanged. The pinned [Piper 1.7.0 config](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/src/piper/config.py) defines smaller scales as faster and larger scales as slower; its [voice implementation](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/src/piper/voice.py) passes that scale into the model before audio is generated.

The first attempt uses the model config's default scale and measures its full WAV. If necessary, the next scale is estimated as `current_scale * aim_frames / measured_frames`, with a bracketed midpoint when the estimate would leave the observed bounds. The model then reads the entire same cue again. There are at most ten attempts per uncached cue, constrained to `[0.85, 1.20]` times the config's base scale. Contiguous fragments of one sentence may pool up to 12 cues/12 seconds and use a lower model-native scale down to `0.45` before fallback.

When native or pooled sentence fitting succeeds, all model-produced speech samples are retained and only verified trailing silence is removed. If those paths cannot fit, the worker synthesizes the complete cue once at the verified base model rate, removes only verified trailing silence, and applies one or more FFmpeg `atempo` stages in the supported `(1, 2]` range. The full result is decoded and measured after every attempt; it is padded to the exact target only after it is no longer overlong. No `atrim`, `asetrate`, tail cutoff, text shortening, or SRT rewrite is permitted. Tempo-fallback cues are always marked `review` and persist their input/output frame counts, factor, attempt count and hashes.

Placement and target frame counts use the same absolute sample boundaries: `round(voice_end * rate) - round(voice_start * rate)`. The resulting project WAV must have exactly that many frames. The master includes the complete fitted clip; the former `min(cue_end, fitted_end)` tail cutoff is removed. Source audio settings, processed preview and final export keep consuming the same validated master track.

`fit`/`review` distinguishes review needs, not permission to deliver a wrong duration. All successful project cues have the exact target frame count. Native speaking-rate control avoids post-processing artifacts for normal cues. Tempo fallback prioritizes a complete deliverable over aborting the whole job, but dense cues can sound very fast and always require listening review. Matching a sample count or passing signal-integrity checks is not a listening assessment.

## Cache, validation and cancellation

The internal timing policy is `whole-cue-piper-tempo-fallback-v10`; application and engine versions are unchanged. Model/config SHA pins are unchanged. Cache keys include the speech envelope, timing source, worker and policy revision. Native entries use `fit_method: piper-length-scale`; fallback entries use `fit_method: piper-atempo` plus `tempo_factor`, `tempo_input_frames`, and `tempo_attempts`. Cache reuse independently verifies SHA, decoded frame counts, target duration and method-specific metadata. Old revision caches/masters are retained but not accepted as current results.

Core checks the reported placement, exact frame counts, non-clipping flag, cue order, native metadata and review status. It then independently hashes every clip and reads RIFF/fmt/data chunks to verify actual mono PCM16 frame counts, including WAVs with extra metadata chunks. Result/event `synthesis_calls` is zero on a cache hit and equals the actual attempt count on a miss; stored `synthesis_attempts` describes the cached clip's original creation. Existing master SHA/decoded-format/duration gates remain. Project reopen validates `VoiceRevision` and rejects old-policy TTS state while preserving files.

Before each synthesis attempt the worker emits cue index and attempt number. Core displays `lượt n/10` while keeping the percentage tied to completed cues; retries do not fake percentage advancement. This keeps the user informed while a model is regenerating the same sentence.

Cancellation/process ownership is unchanged: the owned Python/FFmpeg tree must exit before current-run cleanup; completed clip cache and previous accepted masters survive. A failed fit or failed validation cannot publish a partial/new master.

## Verification status

Automated source/duration contracts, Core contracts and real NGHI inference have run on Windows. Real cue 17-20 and the highest-density supplied cue (7732, 50 non-space characters in 0.8 seconds) completed with exact frame counts, `clipped=false`, and warm-cache reuse. Release/UI listening quality remains a user field test, especially for high tempo factors.

Offline definitions cover a two-second target, sample rounding, native controller direction/brackets/bounds, metadata validation, exact byte-preserving small padding, overflow/excess-silence rejection, timing-sensitive cache identity, no source-timing fallback and actual WAV header validation. They do not mock a model or fabricate production speech evidence. The opt-in four-sentence runtime harness obtains four real Whisper source windows from its supplied video, checks native metadata/attempt counts, and exercises fitting/cache/cancel/preview. A successful native fit and improved listening quality have not yet been demonstrated.

Later user-authorized field checks should include native faster/slower rereads targeting 2 s, quantized/stochastic convergence, complete audible final words, no hidden long silence, attempt messages, out-of-range/nonconvergent failure, warm cache with zero synthesis calls, cancellation during a reread/mixing, old-policy project reopen, and preview/export of the complete fitted master. This may take longer because the model can read a cue again. No speed-up, quality PASS or release claim belongs to this task.

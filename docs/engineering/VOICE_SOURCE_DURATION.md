# Whole-cue model-native voice duration matching

## Requested behavior

Match the full Vietnamese utterance to the corresponding source-speech duration without changing the playback speed of generated audio. A successful two-second source window produces a 44,100-frame WAV at 22,050 Hz, including only a small permitted trailing padding remainder. Preserve the entire cue text and never obtain the target duration by cutting off the end. This task does not optimize generation speed, change the voice/model/runtime packages, alter OCR/ASR inference, or rewrite imported SRT.

The user explicitly approved replacing the prior audio time-stretch implementation with model-native speaking-rate control, including resynthesizing a complete cue. This supersedes the earlier one-call-per-cue constraint: there is still no splitting by Whisper pauses, but there may be multiple whole-cue synthesis attempts. The previous `whole-cue-whisper-fit-v3` post-processing policy is retired.

## Timing ownership

Core verifies the existing Whisper analysis SHA and source identity, then maps its words to the imported cue. `BuildWholeCue` keeps the original SRT identity/start/end/text but adds a separate `voice_start`/`voice_end` envelope, bounded by the cue, from the earliest/latest mapped word. Internal pauses remain inside the envelope: they do not create more TTS calls or get reproduced individually. Missing words, mismatched cue identity or an empty intersection is an actionable error, not a fabricated SRT fallback.

This matches the measured **Whisper envelope within a cue**, not independently verified ground-truth speech boundaries. Incorrect SRT associations or ASR timestamps still need field review. One cue spanning several utterances is fitted as one continuous whole-cue window; per-word prosody matching is out of scope.

The text-only sample explicitly uses `timing_source: sample`. It keeps its natural duration when it fits the existing ten-second sample window. Longer samples are fitted without clipping. Project cues require `timing_source: whisper`; the sample exception cannot be applied to arbitrary project cues.

## Fitting and frame accounting

Piper is loaded once per worker and reused for all cues and attempts. Each attempt calls `voice.synthesize(text, syn_config=SynthesisConfig(length_scale=...))` with the complete normalized text. Only `length_scale` is overridden; the verified model/config bytes, generator/width noise, speaker and package pins remain unchanged. The pinned [Piper 1.7.0 config](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/src/piper/config.py) defines smaller scales as faster and larger scales as slower; its [voice implementation](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/src/piper/voice.py) passes that scale into the model before audio is generated.

The first attempt uses the model config's default scale and measures its full WAV. If necessary, the next scale is estimated as `current_scale * aim_frames / measured_frames`, with a bracketed midpoint when the estimate would leave the observed bounds. The model then reads the entire same cue again. There are at most ten attempts per uncached cue, constrained to `[0.5, 2]` times the config's base scale. These are engineering limits, not validated quality limits. Model randomness and quantized durations mean output is measured after every attempt; contradictory brackets are discarded, not treated as proof of exact control.

The accepted model-produced audio must already fit inside the target, short by at most **40 ms and 2% of the target**, with a one-frame minimum. All its PCM bytes are copied unchanged, then only that measured remainder is padded with silence. A 2 s clip may therefore contain up to 40 ms of appended silence, not necessarily exactly 2 s of audible speech. An overlong clip is never cut; a substantially short clip is never concealed by padding. There is no FFmpeg audio speed filter, rate change, resampling or fallback time stretching in cue fitting. FFmpeg is still used for the existing master FLAC encoder and preview/export. If the native attempts cannot converge within the bounds, the job fails with the cue ID and an actionable timing/text error; no new master is accepted.

Placement and target frame counts use the same absolute sample boundaries: `round(voice_end * rate) - round(voice_start * rate)`. The resulting project WAV must have exactly that many frames. The master includes the complete fitted clip; the former `min(cue_end, fitted_end)` tail cutoff is removed. Source audio settings, processed preview and final export keep consuming the same validated master track.

`fit`/`review` distinguishes native-rate review needs, not permission to deliver a wrong duration. All successful project cues have the exact target frame count including the small recorded padding. Final/base length-scale ratios below 0.8 or above 1.25 are marked for listening review. Native speaking-rate control avoids post-processing time-stretch artifacts but does not guarantee natural prosody, intelligibility or undistorted speech, especially at extreme rates. Matching a sample count or passing signal-integrity checks is not a listening assessment.

## Cache, validation and cancellation

The internal timing policy is `whole-cue-piper-rate-v4`; application and engine versions are unchanged. Model/config SHA pins are unchanged. Cache keys include the speech envelope and timing source. Cache reuse verifies hash, actual decoded frame count, target frame count, durations and native-rate review status. Entries include `fit_method: piper-length-scale`, base/final length scales, `generated_frames`, `padding_frames` and `synthesis_attempts`. Missing native metadata, an old post-processing method or excessive padding invalidates the entry. Old revision caches/masters are not accepted as new-policy results; files are retained, not deleted.

Core checks the reported placement, exact frame counts, non-clipping flag, cue order, native metadata and review status. It then independently hashes every clip and reads RIFF/fmt/data chunks to verify actual mono PCM16 frame counts, including WAVs with extra metadata chunks. Result/event `synthesis_calls` is zero on a cache hit and equals the actual attempt count on a miss; stored `synthesis_attempts` describes the cached clip's original creation. Existing master SHA/decoded-format/duration gates remain. Project reopen validates `VoiceRevision` and rejects old-policy TTS state while preserving files.

Before each synthesis attempt the worker emits cue index and attempt number. Core displays `lượt n/10` while keeping the percentage tied to completed cues; retries do not fake percentage advancement. This keeps the user informed while a model is regenerating the same sentence.

Cancellation/process ownership is unchanged: the owned Python/FFmpeg tree must exit before current-run cleanup; completed clip cache and previous accepted masters survive. A failed fit or failed validation cannot publish a partial/new master.

## Verification status

**Build, automated tests, inference, listening and UI tests: NOT RUN**, as requested by the user. This is source implementation with regression definitions, not technical/runtime PASS. Installed app payloads have not been rebuilt or changed. **Voice quality: WAITING FOR USER FIELD TEST.**

Offline definitions cover a two-second target, sample rounding, native controller direction/brackets/bounds, metadata validation, exact byte-preserving small padding, overflow/excess-silence rejection, timing-sensitive cache identity, no source-timing fallback and actual WAV header validation. They do not mock a model or fabricate production speech evidence. The opt-in four-sentence runtime harness obtains four real Whisper source windows from its supplied video, checks native metadata/attempt counts, and exercises fitting/cache/cancel/preview. A successful native fit and improved listening quality have not yet been demonstrated.

Later user-authorized field checks should include native faster/slower rereads targeting 2 s, quantized/stochastic convergence, complete audible final words, no hidden long silence, attempt messages, out-of-range/nonconvergent failure, warm cache with zero synthesis calls, cancellation during a reread/mixing, old-policy project reopen, and preview/export of the complete fitted master. This may take longer because the model can read a cue again. No speed-up, quality PASS or release claim belongs to this task.

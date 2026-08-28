# Whole-cue voice duration matching

## Requested behavior

Match the full Vietnamese utterance to the corresponding source-speech duration: a two-second source window produces a 44,100-frame WAV at 22,050 Hz. Preserve the entire cue text and never obtain the target duration by cutting off the end. This task does not optimize generation speed, change the voice/model/runtime packages, alter OCR/ASR inference, or rewrite imported SRT.

## Timing ownership

Core verifies the existing Whisper analysis SHA and source identity, then maps its words to the imported cue. `BuildWholeCue` keeps the original SRT identity/start/end/text but adds a separate `voice_start`/`voice_end` envelope, bounded by the cue, from the earliest/latest mapped word. Internal pauses remain inside the envelope: they do not create more TTS calls or get reproduced individually. Missing words, mismatched cue identity or an empty intersection is an actionable error, not a fabricated SRT fallback.

This matches the measured **Whisper envelope within a cue**, not independently verified ground-truth speech boundaries. Incorrect SRT associations or ASR timestamps still need field review. One cue spanning several utterances is fitted as one continuous whole-cue window; per-word prosody matching is out of scope.

The text-only sample explicitly uses `timing_source: sample`. It keeps its natural duration when it fits the existing ten-second sample window. Longer samples are fitted without clipping. Project cues require `timing_source: whisper`; the sample exception cannot be applied to arbitrary project cues.

## Fitting and frame accounting

Piper is loaded once per worker, reused, and called once per uncached cue with the complete normalized Vietnamese text. The first WAV is measured. Whole-utterance FFmpeg `atempo` then targets the required duration, chaining stages in `[0.5, 2]` rather than using a single factor above two. See the [FFmpeg atempo documentation](https://ffmpeg.org/ffmpeg-filters.html#atempo) for its sample-skipping warning and chained alternative.

The filter output is measured, not assumed to equal `raw_duration / tempo`. Up to ten tempo corrections run from the **original** WAV, never resynthesizing or repeatedly processing an already stretched file. Only a small nonnegative filter-rounding remainder (at most 10 ms and approximately 0.5% of the target, with a one-frame minimum) may be padded with silence. No output samples are discarded. If fitting does not converge, the job fails without promoting a master.

Placement and target frame counts use the same absolute sample boundaries: `round(voice_end * rate) - round(voice_start * rate)`. The resulting project WAV must have exactly that many frames. The master includes the complete fitted clip; the former `min(cue_end, fitted_end)` tail cutoff is removed. Source audio settings, processed preview and final export keep consuming the same validated master track.

`fit`/`review` now distinguishes tempo-review needs, not permission to deliver a wrong duration. All successful project cues have the exact target frame count. Raw/target ratios below 0.8 or above 1.25 are marked for listening review; this does not prevent fitting. Very large speed changes may sound unnatural. Matching a sample count or signal-integrity checks do not prove every word is intelligible or spoken correctly.

## Cache, validation and cancellation

The internal timing policy is `whole-cue-whisper-fit-v3`; application and engine versions are unchanged. Model/config SHA pins are unchanged. Cache keys additionally include the speech envelope and timing source. Cache reuse verifies hash, actual decoded frame count, target frame count, durations and tempo-review status. Old revision caches/masters are not accepted as new-policy results; files are retained, not deleted.

Core checks the reported placement, exact frame counts, non-clipping flag, cue order and review status. It then independently hashes every clip and reads RIFF/fmt/data chunks to verify actual mono PCM16 frame counts, including WAVs with extra metadata chunks. Existing master SHA/decoded-format/duration gates remain. Project reopen already validates `VoiceRevision` and therefore rejects old-policy TTS state while preserving the files.

Cancellation/process ownership is unchanged: the owned Python/FFmpeg tree must exit before current-run cleanup; completed clip cache and previous accepted masters survive. A failed fit or failed validation cannot publish a partial/new master.

## Verification status

**Build, automated tests, inference, listening and UI tests: NOT RUN**, as requested by the user. This is source implementation with regression definitions, not technical/runtime PASS. Installed app payloads have not been rebuilt or changed. **Voice quality: WAITING FOR USER FIELD TEST.**

Offline definitions cover a two-second target, sample rounding, speed-up/slow-down factor chains, exact byte-preserving padding, overflow rejection, timing-sensitive cache identity, no source-timing fallback and actual WAV header validation. They do not fabricate production speech evidence. The opt-in four-sentence runtime harness now obtains four real Whisper source windows from its supplied video before exercising fitting/cache/cancel/preview; it no longer uses arbitrary windows as source-timing evidence.

Later user-authorized field checks should include 2.4 s and 1.6 s speech fitted to 2 s, strong tempo adjustments, complete audible final words, same-cue timing changes invalidating cache, cancel during fitting/mixing, old project reopen, and preview/export of the complete fitted master. No speed-up or release claim belongs to this task.

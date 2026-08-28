# Editor Voice/TTS ownership

Current native-rate Voice task baseline: 516647a, updated 2026-08-29.
Exact models and evidence: [NGHI audit](EDITOR_NGHITTS_AUDIT.md).

## Owners

- EditorPage: default Ngọc Huyền selection, create/cancel/sample UI, successful track
  adoption, project persistence and processed-preview refresh.
- BiliSubApplication: one cleanup-aware editor-tts job per create or sample action.
- LocalTtsInstaller: separate private Python/Piper runtime, reviewed model/config
  sizes and hashes, packaged worker identity. No model substitution.
- LocalTtsService: source/Whisper provenance for a full project, whole-cue manifest,
  result validation, immutable per-run master/result, cleanup and progress.
- internal/tts/worker.py: one Piper model per job, whole-cue synthesis, validated
  clip cache, bounded model-native rate retries and bounded-memory FLAC assembly.
- VideoEditorService: existing shared source/voice audio graph for Preview/Export.

## Text and timing

The imported Vietnamese SRT owns text/order/timecode. Each cue is normalized and
passed to Piper as one complete text per attempt. Whisper supplies the bounded
source-speech envelope and karaoke timing; its pauses do not split TTS and acoustic classes do not
select a different voice. No synthetic male/female pitch profiles remain.

The sample action has its own real text cue and runs the same generator without
creating a source video or pretending to have run Whisper.

Natural speech is measured, then Piper may reread the full cue with a different
`length_scale` up to ten attempts. No generated audio is sped up, slowed down or
cut to force a fit. Only a small trailing padding remainder is permitted; bounds,
metadata and exact sample-count checks are defined in [duration policy](VOICE_SOURCE_DURATION.md).
Failure to fit stops the new master. Quality always requires listening; the new
native-rate route has NOT RUN build/runtime/field tests.

## Recovery and playback

A cancelled run preserves earlier completed clips and masters. Cleanup is awaited
before terminal cancellation, including transient Windows descendant file handles.
A subsequent run verifies clip hashes and reuses only matching complete entries.
Project reopen verifies the current NGHI manifest revision and master checksum.

Source audio policies are unchanged: keep mixes original audio with voice; duck
reduces the entire original mix; mute yields voice only. Processed previews seek
the same voice master at the source playhead and use the final render's graph.

No diarization, stem separation, identity classification or release is added by
this task. Voice quality: WAITING FOR USER FIELD TEST.

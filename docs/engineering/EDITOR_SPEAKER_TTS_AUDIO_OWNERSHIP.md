# Editor Voice/TTS ownership

Current Voice task baseline: 6b976bf, updated 2026-08-28.
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
  clip cache, bounded tempo fit and bounded-memory FLAC assembly.
- VideoEditorService: existing shared source/voice audio graph for Preview/Export.

## Text and timing

The imported SRT owns text/order/timecode. Each translated cue is normalized and
passed to Piper once as one complete text. Whisper analysis remains available for
provenance and karaoke; its pauses do not split TTS and acoustic classes do not
select a different voice. No synthetic male/female pitch profiles remain.

The sample action has its own real text cue and runs the same generator without
creating a source video or pretending to have run Whisper.

Natural speech is measured, optionally adjusted once with bounded FFmpeg atempo,
then marked fit/review. A long cue keeps its complete cached speech but its master
placement is bounded by the SRT interval; the UI warns that it needs review.
Quality always requires listening.

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

# BiliSub Studio Timeline Editor V2 - Master Plan

Status: APPROVED ARCHITECTURE, IMPLEMENTATION NOT STARTED
Date: 2026-09-06
Owner approval: Build V2 separately; merge into the Edit tab only after parity and field verification.

## 1. Immutable product goal

Add a timeline that can join multiple source videos into one output while preserving BiliSub Studio's guided Chinese-film localization workflow.

The result must support:

- multiple sequential video clips;
- clip reorder, trim, split and delete;
- Blur/Mosaic/Cover attached to the correct clip and time;
- Vietnamese SRT attached to a clip or the whole sequence;
- generated voice linked to subtitle cues;
- image/logo overlays;
- source audio and one music track;
- preview and final export from the same resolved timeline state;
- autosave, reopen, missing-media reporting and relink.

## 2. Non-negotiable safety boundary

- The existing Editor remains intact and usable during V2 development.
- V2 is implemented as a separate page, model, persistence path, preview controller and export path.
- V2 stays behind an internal feature flag until every release gate passes.
- Existing schema-7 projects are never modified in place by an experimental build.
- Migration operates on a copy and keeps a recoverable schema-7 backup.
- Source videos, SRT files, logos, music and exported files are never overwritten or deleted.
- The classic single-video Editor remains available as fallback for several releases after V2 promotion.
- No classic Editor code is removed until V2 has passed automated parity and real Windows field use.

## 3. Product boundary

Timeline V2 is not intended to become a generic nonlinear editor.

MVP includes:

- one primary video track with sequential clips;
- hard cuts only;
- clip-local effects;
- subtitle and voice tracks;
- image/logo overlays;
- source audio and one music track;
- processed preview and validated export.

MVP excludes:

- transitions;
- picture-in-picture and overlapping video tracks;
- keyframe animation;
- speed ramping;
- color grading;
- multicam;
- compound/nested sequences;
- collaboration.

These exclusions may change only through an accepted entry in `DECISIONS.md`.

## 4. Track order and ownership

Visual tracks are composited from bottom to top. Audio tracks are displayed below a separator.

```text
        00:00       00:10       00:20       00:30

V3 IMG  [          Logo toàn sequence                 ]
V2 SUB  [ Câu 1 ][ Câu 2 ]      [ Câu 3 ][ Câu 4    ]
FX      [ Blur       ]           [ Mosaic             ]
V1 VID  [ Video A       ][ Video B ][ Video C         ]
-------------------------------------------------------
A1 SRC  [ Audio A       ][ Audio B ][ Audio C         ]
A2 VOI  [ Voice 1 ][Voice 2]     [ Voice 3 ][Voice 4 ]
A3 MUS  [              Nhạc nền                       ]
```

Ownership rules:

| Item | Default anchor | Move/reorder behavior |
|---|---|---|
| Video source audio | Clip | Moves with the clip |
| Blur/Mosaic/Cover | Clip-local | Moves with the clip |
| Imported clip SRT | Clip-local | Moves with the clip |
| Generated voice | Subtitle cue ID | Moves with its cue |
| Clip logo | Clip-local | Moves with the clip |
| Sequence logo | Sequence-global | Keeps absolute sequence time |
| Music | Sequence-global | Keeps absolute sequence time |

## 5. Target architecture

```text
TimelineEditorPage
  -> TimelineEditorSession
       -> TimelineProjectStoreV2
       -> TimelineCommandHistory
       -> TimelineCompiler
            -> immutable EditorRenderPlan
                 -> TimelinePreviewController
                 -> TimelineProcessedPreviewService
                 -> TimelineExportService
```

The classic path remains separate:

```text
EditorPage
  -> EditorProjectStore schema 7
  -> EditorPlaybackController
  -> VideoEditorService
```

V2 must not call into mutable state owned by the classic `EditorPage`.

## 6. Time model

- Persist timeline positions as signed 64-bit microseconds.
- Convert to seconds only at FFmpeg/UI boundaries using invariant culture.
- Snap editing operations to the sequence frame grid.
- Keep source-local and sequence-global time explicit; never infer one from the other.

```text
ClipDuration = (SourceOutUs - SourceInUs) / PlaybackRate
SourceTimeUs = SourceInUs + (SequenceTimeUs - TimelineStartUs) * PlaybackRate
```

Every mapping operation requires deterministic boundary tests, including the exact frame before and after a cut.

## 7. Proposed persistence model

```text
TimelineProject
  Id: stable UUID or retained legacy ID
  Name
  Schema
  Assets[]
  Sequence
  UpdatedUtc

MediaAsset
  Id
  Path
  Fingerprint
  Width / Height / Duration / FrameRate
  HasAudio
  ThumbnailPath

TimelineClip
  Id
  AssetId
  TimelineStartUs
  SourceInUs
  SourceOutUs
  PlaybackRate
  SourceAudioSettings

TimelineTrack
  Id
  Type
  IsVisible / IsMuted / IsLocked
  Items[]
```

Project identity must not change when a source asset is relinked.

## 8. Editing invariants

### Move or reorder

- Clip-local effects, subtitles, voice and logos move with the owning clip.
- Sequence-global music and logos do not move.
- A move is one undoable transaction.

### Split

- Items wholly before the cut remain with the left clip.
- Items wholly after the cut move to the right clip with adjusted local time.
- Effects crossing the cut are split and clamped.
- Subtitle/voice cues are not cut through spoken content without an explicit policy.
- Undo restores the clip and every dependent item atomically.

### Delete

- The clip and clip-local dependents are removed together.
- Sequence-global items are preserved.
- Source files remain untouched.
- Undo restores the complete dependency set.

## 9. Preview and export parity

`TimelineCompiler` is the only owner that resolves project state into media operations.

```text
TimelineProject
  -> TimelineCompiler
  -> EditorRenderPlan
       -> Fast Preview
       -> Processed Preview
       -> Final Export
```

Fast Preview may use native source playback plus UI overlays. Processed Preview and Export must consume the same render plan and filter decisions.

Export strategy for MVP:

1. Validate all asset fingerprints.
2. Resolve clips and dependent timeline items.
3. Normalize output resolution, FPS, pixel format and 48 kHz audio.
4. Render deterministic temporary segments.
5. Apply clip-local effects, subtitle, voice and image state.
6. Concatenate normalized segments.
7. Mix sequence-global music/audio.
8. Validate streams, duration and A/V drift.
9. Atomically promote the final output.
10. Remove temporary state after success or completed cancellation cleanup.

## 10. UI direction

- Keep Project List as the entry surface.
- Open Timeline V2 only through an internal development action while it is experimental.
- Reuse the current visual language and inspector controls where ownership is safe.
- Use a resizable timeline panel below Preview/Inspector.
- Use viewport virtualization for clips and subtitle cues.
- Commit drag operations only on pointer release.
- Do not start FFmpeg for every pointer move.
- Show selected clip/item state in the Inspector.
- Keep the classic Editor available from the project card during the transition period.

## 11. Delivery gates

| Gate | Scope | Required exit evidence |
|---|---|---|
| G0 Baseline | Pin current source/binary/data backup | Recoverable baseline identified; no timeline code |
| G1 Classic contracts | Freeze current Editor behavior | Single-video autosave/reopen/preview/export contracts pass |
| G2 V2 shell | Separate page, flag and test data root | Classic path unchanged; V2 has no production writes |
| G3 Domain | Assets, clips, tracks and time mapping | Boundary/property tests pass |
| G4 Migration copy | Schema 7 to one-clip V2 copy | No source or schema-7 mutation |
| G5 One-clip parity | V2 renders one clip | Preview/export match classic behavior |
| G6 Multi-clip core | Add/reorder/trim/split/delete | Dependency and undo tests pass |
| G7 Effects and subtitle | Blur/SRT/logo timeline ownership | Move/split does not drift state |
| G8 Voice and audio | Cue-linked voice and music | A/V synchronization tests pass |
| G9 Processed preview | Revisioned proxy generation | No stale preview or leaked FFmpeg process |
| G10 Export | Segment render and concat | Mixed-format field matrix passes |
| G11 Promotion | Real-project copy and Windows QA | User approval; fallback remains available |

Only one gate may be `IN_PROGRESS`. A gate is complete only when source, tests, build and `STATUS.md` agree.

## 12. Required regression contracts

- `LegacySingleClipParity`
- `TimelineProjectMigrationCopy`
- `TimelineTimeMapping`
- `TimelineClipMoveDependency`
- `TimelineClipSplitDependency`
- `TimelineClipDeleteUndo`
- `TimelineSubtitleVoiceLink`
- `TimelinePreviewExportParity`
- `TimelineAudioSync`
- `TimelineMissingAssetRelink`
- `TimelineSourcePreservation`
- `TimelineAutosaveCrashRecovery`
- `TimelineCancellationCleanup`

## 13. Performance targets

- 100 clips on one sequence.
- Six-hour sequence duration.
- 10,000 subtitle cues.
- 2,000 effect items.
- Timeline scroll/zoom must not block the UI thread.
- Autosave must not interrupt playback.
- Preview cancellation must not leave child processes.
- Thumbnail, waveform and proxy caches must have bounded cleanup policies.

## 14. Stop conditions

Stop implementation and return to the last verified gate when:

- a classic Editor regression appears;
- project migration changes source or schema-7 data;
- preview and export resolve timing differently;
- A/V drift exceeds the accepted threshold;
- cancellation leaves FFmpeg/helper processes;
- the current gate requires an unapproved product decision;
- unrelated working-tree changes conflict with the gate.

## 15. Resume protocol

Every implementation session must begin with:

1. Read this file.
2. Read `STATUS.md`.
3. Read `DECISIONS.md`.
4. Inspect `git status` and the current diff.
5. Work only on the exact active checkpoint.

Every implementation session must end with:

1. Run targeted contracts.
2. Run the required build/smoke gate.
3. Update `STATUS.md` with exact evidence.
4. Append any approved architecture decision to `DECISIONS.md`.
5. Record one exact next action.

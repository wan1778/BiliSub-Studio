# Timeline Editor V2 - Decision Log

Date established: 2026-09-06

This log is append-only. Do not silently rewrite an accepted decision. A change requires a new decision that explicitly supersedes the old entry.

| ID | Status | Decision | Reason |
|---|---|---|---|
| DEC-001 | ACCEPTED | Build Timeline Editor V2 separately from the classic Editor. | The existing Editor is working and must remain a safe fallback. |
| DEC-002 | ACCEPTED | V2 uses a separate page, model, persistence path, preview controller and export path during development. | Prevents experimental state from contaminating production ownership. |
| DEC-003 | ACCEPTED | V2 remains behind an internal feature flag until promotion gate G11. | Allows testing without changing the normal user path. |
| DEC-004 | ACCEPTED | Schema-7 projects are not modified in place by experimental V2 builds. | Existing user data is a concrete compatibility requirement. |
| DEC-005 | ACCEPTED | Migration reads schema 7 and writes a separate V2 copy with backup evidence. | Makes migration reversible and testable. |
| DEC-006 | ACCEPTED | MVP has one sequential primary video track and hard cuts only. | Keeps the first delivery bounded and avoids generic-NLE scope. |
| DEC-007 | ACCEPTED | Track display order is IMG, SUB, FX, V1 video, separator, source audio, voice, music. | Matches visual compositing and separates audio ownership. |
| DEC-008 | ACCEPTED | Timeline persistence uses 64-bit microseconds. | Avoids accumulated floating-point and frame-boundary drift. |
| DEC-009 | ACCEPTED | Blur/Mosaic/Cover and clip subtitles use clip-local anchors by default. | Dependent edits must move with their source clip. |
| DEC-010 | ACCEPTED | Generated voice is linked to subtitle cue IDs. | Timing-only ownership is insufficient after clip reorder or subtitle edits. |
| DEC-011 | ACCEPTED | Music is sequence-global by default. | Reordering a video clip must not unexpectedly move the soundtrack. |
| DEC-012 | ACCEPTED | Project ID remains stable when an asset is relinked. | Source paths are not project identity in a multi-asset project. |
| DEC-013 | ACCEPTED | Preview and Export consume one immutable `EditorRenderPlan` from `TimelineCompiler`. | Prevents preview/export timing and filter drift. |
| DEC-014 | ACCEPTED | MVP export renders normalized segments and then concatenates them. | More observable, resumable and testable than one unbounded filter graph. |
| DEC-015 | ACCEPTED | The classic Editor remains available for several releases after V2 promotion. | Provides field rollback after real-user adoption. |
| DEC-016 | ACCEPTED | Only one delivery gate may be in progress. | Prevents cross-phase scope drift and forgotten acceptance criteria. |
| DEC-017 | ACCEPTED | User approval is required before changing MVP scope or promoting V2 into the normal Edit tab. | Product decisions must not be inferred during implementation. |
| DEC-018 | ACCEPTED | G8 MVP supports at most one sequence-global music track. | Keeps the approved guided-editor scope bounded and resolves PDEC-005. |
| DEC-019 | ACCEPTED | Timeline audio timing is exact in persisted/compiled microseconds; final media A/V drift may not exceed the larger of one 48 kHz sample or half one sequence video frame. | Gives G8 deterministic internal contracts and a realistic mux boundary for G10 field verification. |
| DEC-020 | ACCEPTED | G10 verifies the multi-segment export lifecycle (plan geometry, concat-list order, cleanup-aware AppJob, mixed-format plan matrix) through the `ITimelineExportSegmentRenderer` seam; the real multi-clip FFmpeg mux graph is deferred to a follow-up gate. | Keeps G10 honest without duplicating the classic FFmpeg graph inside the V2 namespace; user-approved scope on 2026-09-07. |
| DEC-021 | ACCEPTED | The classic Editor remains available for at least two verified public builds after V2 promotion, with V2 staying behind the internal flag and schema-7 as the untouched fallback. | Gives field rollback a concrete minimum window; user-approved scope on 2026-09-07; resolves PDEC-006. |
| DEC-022 | ACCEPTED | G12 ships a real multi-clip FFmpeg mux for video plus clip source audio only (normalized H.264/AAC segments, concat demuxer, probe validation against DEC-019-derived field drift); clip items, subtitle cues, voice cues and music are rejected explicitly until their graphs are ported. | Closes the DEC-020 debt without silently dropping edits or duplicating the classic filter graph; user-approved scope on 2026-09-07. |
| DEC-023 | ACCEPTED | G13 renders full-fidelity export: clip effects and subtitle burns reuse the classic `BuildFilter`/`BuildAss` builders over source-coordinate request views, voice cues and global music join in one final amix pass; logos and legacy music stay rejected. Effect payloads use the documented `effect:x,y,w,h:strength` encoding. | Completes the export graph without duplicating classic semantics; user-approved scope on 2026-09-07. |
| DEC-024 | ACCEPTED | G14 merges Timeline into the normal navigation with a real project export path; the classic Editor, its pages and its default selection stay untouched, and the internal flag remains only as an emergency kill-switch (`BILISUB_TIMELINE_V2=0` hides the entry). | User-approved promotion on 2026-09-07; fallback stays available per DEC-015/DEC-021. |
| DEC-025 | ACCEPTED | G15 adds clip editing interactions (select, trim, split, delete, reorder, atomic undo/redo, dirty save) through a headless `TimelineEditingController` wired to explicit page controls; drag interactions and preview playback stay deferred. | First genuinely editable V2 surface without scope creep; user-approved scope on 2026-09-07. |

## Pending product decisions

These decisions are intentionally deferred until the responsible gate:

| ID | Needed by | Question | Current recommendation |
|---|---|---|---|
| PDEC-001 | G3 | Sequence output resolution default | Use the first clip, allow explicit override. |
| PDEC-002 | G3 | Sequence FPS default | Choose one explicit sequence FPS; never vary inside output. |
| PDEC-003 | G6 | Ripple behavior after clip delete | Ripple subsequent V1 clips; keep sequence-global items fixed. |
| PDEC-004 | G7 | SRT import target | Attach to selected clip by default; offer sequence-global import. |
| PDEC-005 | G8 | Maximum music tracks in MVP | One music track. |
| PDEC-006 | G11 | Duration of classic fallback support | At least two verified public builds after promotion. |

PDEC-005 was resolved by DEC-018 on 2026-09-07.

PDEC-006 was resolved by DEC-021 on 2026-09-07.

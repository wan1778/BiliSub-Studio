# OCR topology and scan-state correction

> Superseded for Auto topology by `2026-08-23-ocr-full-pipeline-benchmark.md`. Public 4.0.8 implemented the independent lane/worker model described below, but field review rejected that model because Auto must benchmark and commit equal-count full FFmpeg+Python pipelines. The transactional Cancel/Fresh/Resume findings in this document remain applicable.

Pinned baseline: `15df1e4cd0b814e8efdfc1de14069c65895e3999` (`main`, public beta 4.0.7).

Field status: 4.0.7 is **FAIL**. Auto still commits one scan lane on a Ryzen 7 4800H / RTX 3050 Laptop 4 GB / 32 GB machine, and Cancel does not reliably return the page to a fresh scan with the matching checkpoint removed.

## Root causes

1. `OcrScanner.SelectParallelismAsync` treats a segment lane and a Python/PaddleOCR worker as the same capacity unit.
2. Auto rebuilds an N-worker pool before accepting N segment lanes and rejects the lane level when fewer than N workers start.
3. The manual lane safety gate is also capped by the GPU worker/VRAM policy instead of the FFmpeg segment/CPU/RAM policy.
4. CI explicitly requires one Python worker per segment lane, so the wrong ownership model passes regression.
5. `AppJob.Cancel` publishes terminal `cancelled` before pausable work has stopped or cleanup has completed.
6. Running Cancel and paused-checkpoint deletion are owned by different UI polling branches and race when Pause is completing.
7. `OcrCheckpointStore.RemoveAsync` swallows delete failures and does not verify that the checkpoint is absent.
8. Scan start has no explicit Fresh/Resume intent. The scanner silently resumes whenever a matching checkpoint exists.
9. Paused cue rendering can include results beyond the contiguous safe frontier, while telemetry does not expose live segment and worker topology separately.

## Correct ownership

- A **segment lane** owns one deterministic time segment, one FFmpeg decoder process and one lane-local `SubtitleTracker`.
- The **OCR worker pool** owns Python/PaddleOCR processes. All segment lanes submit frames to this shared bounded pool.
- Segment-lane count and OCR-worker count are selected and reported separately. A valid topology can be `4 FFmpeg lanes + 1 OCR worker` or `4 FFmpeg lanes + 4 OCR workers`.
- GPU/VRAM limits the GPU worker pool. CPU/RAM and video duration limit segment lanes. Live probes validate the pair but never require equality.
- A resumed checkpoint keeps its segment topology. Worker capacity may be safely rebuilt independently on the current machine.

## Scan state contract

| State | Primary action | Pause | Cancel | Checkpoint |
|---|---|---|---|---|
| Fresh | Quét từ đầu | disabled | disabled | absent |
| Running | disabled | enabled | enabled | not authoritative |
| Pausing | disabled | disabled | enabled | being prepared |
| Paused | Tiếp tục | disabled | Hủy và xóa | durable |
| Cancelling | disabled | disabled | remains visible | deletion in progress |
| Completed | Quét lại từ đầu | disabled | disabled | absent |
| Failed | Quét lại từ đầu or Tiếp tục when a valid checkpoint exists | disabled | according to checkpoint | verified |

Cancel is terminal only after all active OCR/FFmpeg work stops and the exact matching schema-4/schema-3 checkpoint is verified absent. Continue never silently becomes Fresh. Fresh removes the matching checkpoint before selecting a new segment topology.

## Progress and cue contract

- Aggregate progress is unique core work summed across all lanes.
- Safe frontier is the continuous processed prefix from time zero.
- Paused/restart cue preview is restricted to the safe frontier.
- Final completion may publish all reconciled cues.
- Telemetry reports segment lanes, active/completed lanes, OCR workers and worker kinds separately.

## Required regressions

1. Four segment lanes remain valid with one shared OCR worker.
2. A 16-logical-CPU / 32-GB fixture selects at least four segment lanes independently of 4-GB GPU worker capacity.
3. Manual `4` means four segments when duration/CPU/RAM allow it; it does not request four workers as a precondition.
4. Pausable job Cancel stays non-terminal until Core calls cancellation complete.
5. Cancel during Running and Pausing deletes the exact checkpoint before terminal state.
6. Cancel from Paused deletes the exact checkpoint and returns UI to Fresh.
7. Checkpoint deletion errors propagate; successful deletion is verified.
8. Continue preserves saved segment topology; Fresh deletes saved topology and reselects.
9. Paused checkpoint cues do not extend past the contiguous safe frontier.
10. Static Windows OCR contract rejects any worker-count-equals-lane-count requirement.

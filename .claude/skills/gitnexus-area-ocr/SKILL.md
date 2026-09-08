---
name: gitnexus-area-ocr
description: "Skill for the Ocr area of BiliSub-Studio. 256 symbols across 25 files."
---

# Ocr

256 symbols | 25 files | Cohesion: 72%

## When to Use

- Working with code in `csharp/`
- Understanding how as_list, as_mapping, merge_inline_lines work
- Modifying ocr-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | RunRoundAsync, ProcessAdaptiveWindowAsync, PublishProgress, RecognizeSingleAsync, ReadAsync (+43) |
| `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs` | ActiveFrameEnd, CanonicalTrackingRune, ConfidentFloor, CreateObservation, CreateObservation (+25) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrCueReconciler.cs` | MergeTouchingIdentical, MergeTouchingIdentical, ClassifyVisualCandidate, DashNormalize, EvaluateBoundaryAsync (+19) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrInstaller.cs` | CreatePrivateVenvAsync, EnsureAsync, EnsureBasePythonAsync, EnsurePrivatePythonAsync, ExplicitPythonEnvironment (+18) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrManager.cs` | RunAsync, BuildPoolLockedAsync, ConfigureWorkerPoolAsync, DesiredKinds, EnsureAsync (+12) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrWorkerClient.cs` | DisposeAsync, ClosedExceptionAsync, Compact, ParseResult, ProbeVisualChangesAsync (+8) |
| `internal/ocr/worker.py` | _same_text_row, _stitch_overlapping_text, as_list, as_mapping, merge_inline_lines (+8) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs` | BuildSegments, KeyAsync, ModeFor, NewAsync, RemoveAsync (+7) |
| `csharp/src/BiliSubStudio.Core/Hardware/HardwareService.cs` | BenchmarkAsync, RecommendedOcrSegmentLanes, Snapshot, ResourceSnapshot, GlobalMemoryStatusEx (+4) |
| `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelMetrics.cs` | EdgeIoU, ForegroundIoU, OtsuMask, SeparableConvolve, SobelMask (+4) |

## Entry Points

Start here when exploring this area:

- **`as_list`** (Function) — `internal/ocr/worker.py:37`
- **`as_mapping`** (Function) — `internal/ocr/worker.py:47`
- **`merge_inline_lines`** (Function) — `internal/ocr/worker.py:155`
- **`normalize_box`** (Function) — `internal/ocr/worker.py:101`
- **`parse_prediction`** (Function) — `internal/ocr/worker.py:207`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `OcrPixelMetrics` | Class | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelMetrics.cs` | 4 |
| `as_list` | Function | `internal/ocr/worker.py` | 37 |
| `as_mapping` | Function | `internal/ocr/worker.py` | 47 |
| `merge_inline_lines` | Function | `internal/ocr/worker.py` | 155 |
| `normalize_box` | Function | `internal/ocr/worker.py` | 101 |
| `parse_prediction` | Function | `internal/ocr/worker.py` | 207 |
| `decode_image` | Function | `internal/ocr/worker.py` | 65 |
| `emit` | Function | `internal/ocr/worker.py` | 32 |
| `main` | Function | `internal/ocr/worker.py` | 295 |
| `refine_short_text` | Function | `internal/ocr/worker.py` | 263 |
| `validate_device` | Function | `internal/ocr/worker.py` | 251 |
| `visual_change_scores` | Function | `internal/ocr/worker.py` | 74 |
| `ProbeAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrFrameExtractor.cs` | 22 |
| `RunAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrManager.cs` | 162 |
| `BuildOcrFilter` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelComparer.cs` | 165 |
| `PlanOcrFrame` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelComparer.cs` | 162 |
| `TryPlanOcrFrame` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrPixelComparer.cs` | 139 |
| `ReadAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | 1628 |
| `ReadAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | 1694 |
| `RecognizeFrameAsync` | Method | `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs` | 33 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `GenerateSampleAsync → Kill` | cross_community | 10 |
| `GenerateCuesAsync → Lease` | cross_community | 9 |
| `InspectAsync → TryDelete` | cross_community | 9 |
| `RecognizeSingleAsync → Sha256Async` | cross_community | 8 |
| `RecognizeSingleAsync → TryDelete` | cross_community | 8 |
| `Cancel_Click → IsChinesePunctuation` | cross_community | 8 |
| `Cancel_Click → IsHan` | cross_community | 8 |
| `InspectAsync → IsOwnedTool` | cross_community | 8 |
| `LoopAsync → DashNormalize` | cross_community | 8 |
| `LoopAsync → HasAdjacentDuplicateInsertion` | cross_community | 8 |

## How to Explore

1. `context({name: "as_list"})` — see callers and callees
2. `query({search_query: "ocr"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`

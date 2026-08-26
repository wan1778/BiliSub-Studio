# Codex handoff — BiliSub Studio

- Current main/base SHA: `c11905c5326676a734761a2d6fa33a6395779925`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-ACCURATE-01` follow-up — Accurate scanner now passes its every-frame mode through to the PTS-aware subtitle tracker.
- Task in progress: none.
- Exact next task: `OCR-STABILIZE-01` — prevent an every-frame text reveal/OCR variation from fragmenting a single visual subtitle into several cues, using the demonstrated 0–20 second scan before touching any unrelated OCR behavior.

## Root cause

The control labelled `Chính xác` was only a 4 fps sampling mode. Its timestamps
were synthesized as `start + frameIndex / fps`, so it could skip short subtitles
and could not retain variable-frame-rate timing. The tracker consequently used a
sample midpoint instead of the true frame boundary.

The first real every-frame scan also demonstrates a separate, still-unfixed
stabilization defect: visually continuous text can vary by a leading/trailing
glyph between adjacent frames. The similarity threshold treats those variants
as different cues, so the output fragments one subtitle even though every PTS
is read correctly.

## Changes made

- `accurate` now means every decoded video frame; `balanced` remains 2.5 fps and
  `fast` remains 1.5 fps.
- Accurate FFmpeg lanes preserve source timestamps with `-copyts` and emit one
  `showinfo` PTS/duration record for each JPEG frame.
- The scanner streams those PTS records in order with bounded buffering; it does
  not collect a video’s frames or timestamps in memory.
- The tracker accepts real frame duration and, for Accurate mode, starts/ends
  cues on source-frame boundaries instead of sampling midpoints.
- Accurate scanner construction now explicitly enables that PTS-aware tracker
  path (`exactFrameTiming: mode.EveryFrame`).
- Bumped OCR checkpoint schema to 5 so no old 4-fps Accurate checkpoint can be
  resumed under the new every-frame semantics.
- Added contract coverage for every-frame argument construction, PTS parsing,
  exact one-rune cue timing, and retained prior sampled-mode behavior.

## Files changed

- `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj --no-restore`: PASS, 0 warnings / 0 errors.
- `dotnet build csharp/BiliSubStudio.sln --no-restore`: PASS, including the WinUI application, 0 warnings / 0 errors.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-build`: PASS, 71/71.
- Local FFmpeg runtime probe on the supplied test video: PASS — `-copyts` plus
  `showinfo` returned sequential source PTS `59.600000`, `59.633313`,
  `59.666688`, with exact per-frame durations.
- Bounded Windows local OCR pipeline field test (new source, GPU, one lane,
  0–20 seconds of `C:\Users\Man PC\Downloads\test\*.mp4`): completed 600/600
  frames and emitted PTS-aligned output. This proves the every-frame path runs,
  but is **functional FAIL** versus the supplied Chinese SRT: it emitted 23
  cues where the reference has 14 in that interval; `你走吧` starts at
  `2.633313` instead of the reference `2.800`, and `一万年` fragments into
  multiple cues. Result artifact:
  `C:\Users\Man PC\AppData\Local\Temp\BiliSubOcrFieldProbe\state\accurate-20s.json`.
- Therefore timing has frame-level source PTS precision, but OCR subtitle
  output has not passed the functional accuracy/timing gate. No release may be
  made from this evidence.
- No version bump, release, PR, merge, or source-media overwrite was performed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task; release only after
  the relevant Windows gate and functional field test pass.

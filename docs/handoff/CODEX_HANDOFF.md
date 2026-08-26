# Codex handoff — BiliSub Studio

- Current main/base SHA: `08cb0f87a106258cb4f7b172dbaa7714b7289266`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-ACCURATE-01` — the accurate scan mode now decodes every source frame and uses FFmpeg presentation timestamps (PTS).
- Task in progress: none.
- Exact next task: field-test an Accurate OCR scan against a bounded representative portion of `C:\Users\Man PC\Downloads\test`, compare resulting text/timing against its Chinese SRT, then address the first demonstrated missing-character case only.

## Root cause

The control labelled `Chính xác` was only a 4 fps sampling mode. Its timestamps
were synthesized as `start + frameIndex / fps`, so it could skip short subtitles
and could not retain variable-frame-rate timing. The tracker consequently used a
sample midpoint instead of the true frame boundary.

## Changes made

- `accurate` now means every decoded video frame; `balanced` remains 2.5 fps and
  `fast` remains 1.5 fps.
- Accurate FFmpeg lanes preserve source timestamps with `-copyts` and emit one
  `showinfo` PTS/duration record for each JPEG frame.
- The scanner streams those PTS records in order with bounded buffering; it does
  not collect a video’s frames or timestamps in memory.
- The tracker accepts real frame duration and, for Accurate mode, starts/ends
  cues on source-frame boundaries instead of sampling midpoints.
- Bumped OCR checkpoint schema to 5 so no old 4-fps Accurate checkpoint can be
  resumed under the new every-frame semantics.
- Added contract coverage for every-frame argument construction, PTS parsing,
  exact one-rune cue timing, and retained prior sampled-mode behavior.

## Files changed

- `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`

## Tests and status

- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj --no-restore`: PASS, 0 warnings / 0 errors.
- `dotnet build csharp/BiliSubStudio.sln --no-restore`: PASS, including the WinUI application, 0 warnings / 0 errors.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-build`: PASS, 71/71.
- Local FFmpeg runtime probe on the supplied test video: PASS — `-copyts` plus
  `showinfo` returned sequential source PTS `59.600000`, `59.633313`,
  `59.666688`, with exact per-frame durations.
- Only compile/contract/FFmpeg-stream PASS: a full OCR scan through the WinUI app
  using the new build has not completed yet. It must not be called functional
  OCR PASS; missing-character recovery remains unproven.
- No version bump, release, PR, merge, or source-media overwrite was performed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task; release only after
  the relevant Windows gate and functional field test pass.

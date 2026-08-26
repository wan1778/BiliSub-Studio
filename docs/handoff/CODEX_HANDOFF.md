# Codex handoff — BiliSub Studio

- Current main/base SHA: `92cce21285715d6d935df3f2174091319a955bbe`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-STABILIZE-01` — stabilize every-frame reveal/fade variants and remove a demonstrated far-row OCR glyph contaminant.
- Task in progress: none.
- Exact next task: field-test a longer bounded Accurate OCR segment before treating the full-video OCR quality gate as PASS; use source-frame PTS as timing truth and record any intentional visual-text versus supplied-SRT differences separately.

## Root cause

The control labelled `Chính xác` was only a 4 fps sampling mode. Its timestamps
were synthesized as `start + frameIndex / fps`, so it could skip short subtitles
and could not retain variable-frame-rate timing. The tracker consequently used a
sample midpoint instead of the true frame boundary.

The first real every-frame scan demonstrated a stabilization defect: visually
continuous text can vary by a leading/trailing glyph between adjacent frames.
The similarity threshold treated those variants as different cues, fragmenting
one subtitle even though every PTS was read correctly. It also accepted a
separate, one-glyph OCR line far above the dominant subtitle baseline.

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
- Accurate mode now holds a proper-substring reading briefly: it remains part
  of a reveal/fade when it vanishes or changes quickly, but becomes a new cue
  when it persists for 0.75 seconds. This preserves `你走吧` while separating a
  later repeated `幸福` cue.
- OCR tracking and single-frame recognition remove a one-glyph line only when
  a high-confidence, 3+ glyph subtitle exists on a geometrically distant row.
  This removes the demonstrated `州`/`怡` contamination without discarding
  normal same-baseline text.
- Exact-frame cue commits now use source frame duration rather than the former
  fixed 120 ms minimum.
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
  0–20 seconds of `C:\Users\Man PC\Downloads\test\*.mp4`): PASS for the
  demonstrated regression. It completed 600/600 frames through NVDEC at
  0.62× realtime, retained source-frame PTS, merged reveal fragments, split
  the later persistent `幸福` cue, and removed the spurious `州` glyph. Result:
  `C:\Users\Man PC\AppData\Local\Temp\BiliSubOcrFieldProbe\state\accurate-20s.json`.
- The result has 15 visibly recognized cues while the supplied Chinese SRT has
  14 in the same 20 seconds because the video visibly shows `当然知晓` at
  13.8s while that SRT keeps the prior text through 14.4s. Do not force source
  PTS output to match a different subtitle track without a verified visual
  ground-truth decision.
- Full-video OCR accuracy and the WinUI user-flow remain untested. No release
  may be made from this bounded field evidence alone.
- No version bump, release, PR, merge, or source-media overwrite was performed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task; release only after
  the relevant Windows gate and functional field test pass.

# Codex handoff — BiliSub Studio

- Current main/base SHA: `ddc79ebf0182379c61594124f109848d1c29597c`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-TIMING-02` — keep a continuous subtitle together when OCR alternates between equivalent simplified/traditional glyphs on adjacent source frames.
- Task in progress: none.
- Exact next task: run the current Release build through the WinUI OCR user flow, then field-test a representative longer Accurate segment before treating the full-video OCR quality gate as PASS; use source-frame PTS as timing truth and record intentional visual-text versus supplied-SRT differences separately.

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

The 60-second field scan then demonstrated a persistent vertical left-side
overlay (`在原地`, x=0..62 and y=33..221) being joined with the horizontal
subtitle at the bottom of the OCR crop. It is not subtitle content.

The five-minute field scan then demonstrated a separate upper-right, one-glyph
overlay (`口`, x≈1225 and y≈30) that appeared briefly between two real bottom
subtitles. It is not a short subtitle cue.

The next real scan demonstrated a timing-fragmentation defect: on adjacent
source frames Paddle alternated between simplified `别` and traditional `別` in
the same visually continuous line (`别睡傻了`). The tracker treated those
spellings as different text and emitted multiple short cues.

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
- OCR tracking and single-frame recognition choose the bottom-most horizontal
  text baseline, then remove vertical or far-above lines. This removes the
  demonstrated `州`/`怡` noise and `在原地` overlay without discarding normal
  same-baseline subtitle lines.
- For lower-screen ROIs only, a lone glyph in the upper normalized frame band
  is ignored. The same glyph remains valid for a user-selected upper ROI, so
  this does not turn a global one-character-caption rule into a data-loss rule.
- Exact-frame cue commits now use source frame duration rather than the former
  fixed 120 ms minimum.
- Tracker text comparison now treats a curated set of equivalent
  simplified/traditional Han glyphs as the same for temporal tracking only.
  It retains the best recognised spelling in the actual cue/SRT and still
  separates genuinely different text.
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
- `dotnet build csharp/BiliSubStudio.sln -c Release --no-restore`: PASS, 0 warnings / 0 errors. Debug build was not used for this final check because the separate `build dev` test window held its DLL open.
- Local FFmpeg runtime probe on the supplied test video: PASS — `-copyts` plus
  `showinfo` returned sequential source PTS `59.600000`, `59.633313`,
  `59.666688`, with exact per-frame durations.
- Bounded Windows local OCR pipeline field test (new source, GPU, one lane,
  0–60 seconds of `C:\Users\Man PC\Downloads\test\*.mp4`): PASS for the
  demonstrated regressions. It completed 1,800/1,800 frames through NVDEC at
  0.64× realtime, retained source-frame PTS, merged reveal fragments, split
  the later persistent `幸福` cue, and emitted no `州`, `怡`, or `在原地`
  contamination. Result:
  `C:\Users\Man PC\AppData\Local\Temp\BiliSubOcrFieldProbe\state\accurate-60-s.json`.
- Five-minute field scan before the final glyph fix: 9,000/9,000 source frames
  completed, 162/164 supplied-SRT cues had matching text with temporal overlap,
  and it exposed the otherwise isolated `口` false cue at 292.867–293.033.
- Targeted streaming field test after the glyph fix: a temporary 20-second
  clip of source 280–300 seconds completed 600/600 frames and emitted eight
  expected lower-screen cues with no `口` cue. The source file was not changed;
  the temporary clip is at
  `C:\Users\Man PC\AppData\Local\Temp\BiliSubOcrFieldProbe\scene-280-300.mp4`.
- Targeted full OCR pipeline field test after the simplified/traditional
  tracking fix: 0–146 seconds of the supplied video completed 4,380/4,380
  source frames through GPU, one lane, at 0.651× realtime. The demonstrated
  line is now one cue, exactly `144.166687 → 145.233313`, rather than being
  fragmented by `别`/`別`. Result:
  `C:\Users\Man PC\AppData\Local\Temp\BiliSubOcrFieldProbe\state\accurate-146-s.json`.
- The supplied Chinese SRT is not an exact visual-overlay ground truth: it
  omits visibly recognised on-screen text such as `当然知晓` at 13.8s. Do not
  force source PTS output to match a different subtitle track without a
  verified visual ground-truth decision.
- The WinUI OCR page opens in the current debug build, but its Windows file
  picker is hosted by `PickerHost.exe` and was not targetable by the test
  automation; a human WinUI file-selection/scan flow remains field-test work.
  Full-video OCR accuracy remains untested. No release may be made from this
  bounded field evidence alone.
- No version bump, release, PR, merge, or source-media overwrite was performed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task; release only after
  the relevant Windows gate and functional field test pass.

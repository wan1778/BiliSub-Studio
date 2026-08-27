# Codex handoff — BiliSub Studio

- Current main/base SHA: `3838bef4a50c7ecb204ae42475ed27d0c31b86b3`.
- Current branch: `main`.
- PR: none.
- Last completed task: `OCR-TEXT-05` — retry a blank result only when an active cue makes it suspicious.
- Task in progress: none.
- Exact next task: `OCR-TEXT-06` — retain a valid sampled-mode one-rune subtitle without treating ordinary OCR noise as dialogue.

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

The Release WinUI OCR flow then demonstrated that a deeply nested portable
build path creates a `libpaddle.pyd` path of 255 characters. Windows failed to
load the native module (`DLL load failed: The filename or extension is too
long`), so the worker exited before it could emit Ready.

The post-runtime-fix 300-second Accurate scan found a second temporal
fragmentation issue: Paddle alternated `?` and `？` for one unchanged on-screen
caption. The tracker treated the punctuation glyphs as different text and
emitted adjacent 100-ms cues.

`OCR-TEXT-01` then showed that valid Chinese text containing fullwidth Latin
letters (for example `ＶＩＰ`) was rejected or preserved in a less useful form.
`OCR-TEXT-02` addressed a different defect: a high-confidence active cue could
keep an OCR omission forever when the fuller compatible reading arrived on later
frames at lower confidence. A single longer reading is not safe enough to trust,
because it may instead be an edge-glyph hallucination.

`OCR-TEXT-03` reproduced an independent lane-boundary loss: `Reconcile()`
merged time and confidence but retained the text from whichever OCR lane happened
to be enumerated first. Therefore a real fuller compatible cue could be lost.

`OCR-TEXT-04` found a decision-order defect: the initial OCR confidence was
evaluated before the known non-subtitle overlay filter. A high-confidence overlay
could therefore affect whether the lower-confidence dialogue received an
enhanced recognition pass.

`OCR-TEXT-05` found that the full scan had no recovery path for a blank primary
read. Two adjacent misses could close an active cue even though an enhanced
read of the same frame could still recover its text. Retrying every blank would
double work during long background sections, so only an active cue is evidence
enough to make a blank suspicious.

That same scan showed three non-dialogue lower-ROI false positives with the
same geometry: a persistent left/top branding overlay, a stylized scene title,
and another left/top label. Normal dialogue was centered in the lower band.

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
- `OcrInstaller` now calculates the final Paddle native DLL path before
  creating its private environment. At 220+ characters it moves only the OCR
  worker, models and venv to compact `%LocalAppData%\BiliSub Studio\OCRBootstrap\store`.
  Short portable installs keep their existing app-local layout.
- `OCR-TIMING-03` treats the equivalent ASCII/Chinese punctuation forms as one
  tracking glyph only, preserving the originally selected caption spelling and
  source-frame PTS boundaries.
- `OCR-OVERLAY-02` adds a geometry-specific left/top overlay guard for the
  default lower subtitle ROI only; custom upper ROIs retain their text.
- Bumped OCR checkpoint schema to 5 so no old 4-fps Accurate checkpoint can be
  resumed under the new every-frame semantics.
- Added contract coverage for every-frame argument construction, PTS parsing,
  exact one-rune cue timing, and retained prior sampled-mode behavior.
- `OCR-TEXT-01` folds only fullwidth Latin letters inside otherwise valid
  Chinese OCR text; it does not alter fullwidth digits or Chinese punctuation.
- `OCR-TEXT-02` records a compatible longer active-text variant and promotes it
  only after two consecutive reads. Checkpointing waits while that evidence is
  unresolved, preventing an inconsistent resume snapshot.
- `OCR-TEXT-03` keeps the normal overlap and similarity gates, but selects the
  longer string only when it is a strict textual superset of the existing OCR
  cue. It never synthesizes a string from two similar readings.
- `OCR-TEXT-04` applies the existing overlay filter to both normal and enhanced
  reads before the shared enhanced-pass decision/preference logic. It preserves
  the reviewed geometry rules, thresholds, preprocessing, tracker and FFmpeg.
- `OCR-TEXT-05` requests one enhanced pass for a successful but undetected OCR
  read only while the lane tracker has an active cue. Detected low-confidence
  reads keep the pre-existing retry path; worker failures are not retried.

## Files changed

- `csharp/src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrInstaller.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrOverlayFilterRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrRuntimePathRegression.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/ChineseSubtitleNormalizer.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/SubtitleTracker.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/ChineseOcrFullwidthRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerFullerTextRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrLaneReconcileFullerTextRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrEnhancedFilterOrderRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrActiveCueBlankRecoveryRegression.cs`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`

## Tests and status

- `dotnet build csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj --no-restore`: PASS, 0 warnings / 0 errors.
- `dotnet build csharp/BiliSubStudio.sln --no-restore`: PASS, including the WinUI application, 0 warnings / 0 errors.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-build`: PASS, 71/71.
- `dotnet build csharp/BiliSubStudio.sln -c Release --no-restore`: PASS, 0 warnings / 0 errors. Debug build was not used for this final check because the separate `build dev` test window held its DLL open.
- `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-restore`: PASS, 71/71, including the short/long OCR private-runtime path guard.
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
- Release runtime field test under the same deep build root that previously
  failed: PASS. `PrepareOcrAsync("gpu")` created the compact private runtime,
  Paddle `3.2.0` reported CUDA enabled, and one actual video frame returned
  `陈长安` at `0.997593` confidence. The source video was not changed.
- Post-runtime-fix Accurate field scan: PASS for frame timing/runtime. The
  source 0–300 seconds completed 9,000/9,000 frames at 0.650x realtime and
  kept source PTS. It revealed two adjacent cues for the same `所以?` caption
  solely because Paddle alternated ASCII and Chinese question marks.
- Targeted OCR-TIMING-03 runtime test: PASS. A temporary 89–93 second clip
  completed 120/120 frames with the Release GPU runtime. After the fix,
  `所以?` is exactly one source-frame-bounded cue from 2.333333 to 2.833333;
  the former 66-ms `所以？` duplicate is absent. The temporary clip did not
  modify source media.
- Targeted OCR-OVERLAY-02 runtime test: PASS. A temporary 139–144 second clip
  completed 150/150 source frames with GPU OCR. The demonstrated stylized
  scene-title false cues are absent, while normal dialogue `大黄啊大黄` remains
  from `2.666667` to `3.966667`. The clip did not modify source media.
- The supplied Chinese SRT is not an exact visual-overlay ground truth: it
  omits visibly recognised on-screen text such as `当然知晓` at 13.8s. Do not
  force source PTS output to match a different subtitle track without a
  verified visual ground-truth decision.
- The WinUI OCR page, picker, metadata read and `Chính xác` mode selection
  were observed in the current Release build. The full post-fix UI worker gate
  was validated via a path-bound Release runtime probe because window automation
  began returning the user-installed app's build identity after a new launch;
  do not risk interacting with that user session. A human `Prepare OCR` +
  `Test frame` field-test remains desirable.
  Full-video OCR accuracy remains untested. No release may be made from this
  bounded field evidence alone.
- Post-sync targeted verification at `6cfa1b8`: PASS —
  `python csharp/scripts/verify_ocr_scanner_contract.py`.
- Post-sync targeted verification at `6cfa1b8`: PASS —
  `python csharp/scripts/verify_ocr_worker_contract.py`.
- Post-sync targeted verification at `6cfa1b8`: PASS —
  `dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj --no-restore`, 71/71.
- `OCR-TEXT-01` and `OCR-TEXT-02` are source/contract PASS. They have not yet
  received a new full Windows OCR field run on the supplied video after these
  two text-specific commits; this remains required before claiming functional
  OCR-quality PASS or making a release.
- OCR-TEXT-03 targeted verification: PASS — scanner and Paddle worker contract
  scripts, then Core contract tests 71/71. The new regression verifies both
  lane orders, a different-meaning near-match, and equivalent raw-lane output
  after a simulated resume. No Windows video field test has been run for this
  reconciliation-only change.
- OCR-TEXT-04 targeted verification: PASS — scanner and Paddle worker contract
  scripts, then Core contract tests 71/71. The regression confirms a filtered
  overlay cannot request or win enhanced OCR, while retained low-confidence
  dialogue does request it. No Windows video field test has been run after the
  new decision ordering.
- OCR-TEXT-05 targeted verification: PASS — scanner and Paddle worker contract
  scripts, then Core contract tests 71/71. The regression covers no background
  blank retry, active-cue blank retry, worker-error exclusion, and active-cue
  continuity after a recovered frame. No Windows video field test has been run
  after this narrowly scoped blank-recovery change.
- No version bump, release, PR, merge, or source-media overwrite was performed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task; release only after
  the relevant Windows gate and functional field test pass.

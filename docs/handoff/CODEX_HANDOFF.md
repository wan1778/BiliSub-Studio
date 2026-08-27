# Codex handoff — BiliSub Studio

- Current main/base SHA: `d5ab2df1724618f1772a0a8fb11e20e1817e99c9`.
- Current branch: `main`.
- PR: none.
- Last completed task: `RELEASE-4.0.50` — publish the bundled Voice, ASR UTF-8 and OCR Auto worker-topology update.
- Task in progress: none.
- Exact next task: field-test `4.0.50` in the Windows Editor with an imported fully translated SRT: click one Create voice action with no timing cache, cancel during timing, retry, then preview the resulting track. Also test OCR Auto on the 1.5 GB-free-VRAM machine and confirm a failed 8-worker probe can commit 5, 6 or 7 when its resource and throughput gates pass.

## Root cause

The Voice inspector exposed two sequential actions: the user had to first start
Whisper timing, then return to start TTS. TTS itself already fits each generated
Ngọc Huyền clip to the Whisper word/pause windows, but the UI prevented it from
acquiring that prerequisite automatically. It also did not explicitly show the
single licensed local reading model available to the product.

The local ASR worker runs Python with `-I` for isolation. On Windows that mode
ignores `PYTHONIOENCODING`, so a Chinese Whisper segment was serialized with the
active cp1252 console encoding and failed with `UnicodeEncodeError`. This was
reproduced against the supplied video. The installed log's historical matching
failure is `ASR local chưa hoàn chỉnh sau khi cài` from build 4.0.34; no
`editor-asr` job is recorded for the later 4.0.48 build, but its worker argument
path has the same UTF-8 defect.

GitHub Actions runs #499 and #500 failed before the WinUI compile/package gate
because `docs/migration/CSHARP_CODE_MAP.generated.md` still described the retired
`CreateAsr_Click` method. `verify.ps1` intentionally rejects a stale generated
code map, so the red X was an enforceable documentation-derived contract failure,
not a compiler or ASR model failure.

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

`OCR-TEXT-06` found that the tracker required three observations for every
one-rune candidate, including the 2.5 fps and 1.5 fps sampled modes. That was
appropriate for every-frame Accurate OCR but unnecessarily rejected a stable,
high-confidence one-rune subtitle in sampled scans.

`OCR-TIME-02` found that the sampled Balanced/Fast path still synthesised each
timestamp as `startAt + frameIndex / fps`. A resumed scan then used that inferred
value as a new FFmpeg `-ss` seek. This is inaccurate for a non-keyframe seek or
VFR source and can compound timing error over repeated resumes.

That same scan showed three non-dialogue lower-ROI false positives with the
same geometry: a persistent left/top branding overlay, a stylized scene title,
and another left/top label. Normal dialogue was centered in the lower band.

## Changes made

- Voice now presents the supported reading model explicitly: `Ngọc Huyền · tiếng
  Việt local`. It is a real pinned local model, not a placeholder selection.
- `Tạo voice và tự canh timecode` is the only user entry point. It reuses a valid
  saved Whisper timing document, or creates one cleanup-aware ASR job itself,
  waits for it to finish, then starts the existing TTS job.
- The separate `Phân tích word timing` button and its event owner were removed.
  Cancellation still owns the active ASR or TTS job, and no event handler calls
  another event handler.
- `ASR-UTF8-01` retains isolated Python mode and adds `-X utf8` before the ASR
  worker path. This makes its JSON stdout UTF-8 even when the Windows console
  defaults to cp1252; no model/runtime/GPU policy changed.
- `CI-MAP-01` regenerates `CSHARP_CODE_MAP.generated.md` with the actual Voice
  owner methods (`SelectedVoiceModel` and `EnsureVoiceTimingAsync`) and removes
  the retired `CreateAsr_Click` entry.
- `OCR-AUTO-FALLBACK-01` keeps the fast base ladder `1 → 2 → 4 → 8 → 16`, but
  after a failed higher level it restores the last PASS worker pool and probes
  descending intermediate levels. For example, a rejected 8-worker topology
  now tests 7, then 6, then 5; the first resource-safe topology with at least
  10% throughput gain commits. This changes no OCR model, ROI, frame sampling
  policy, source PTS path or manual topology behavior.

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
- `OCR-TEXT-06` keeps three hits for Accurate one-rune and every low-confidence
  candidate, but accepts a sampled high-confidence one-rune candidate on two
  compatible hits. Overlay filtering remains upstream and unchanged.
- `OCR-TIME-02` enables FFmpeg `-copyts` and reads ordered `showinfo` PTS after
  the sampled `fps` filter. Balanced/Fast cue boundaries and resume checkpoints
  now use those real source timestamps instead of synthetic frame-count timing.

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
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrSampledOneRuneRegression.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/OcrTrackerModeRegression.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/scripts/verify_editor_voice_start_contract.py`
- `csharp/scripts/verify_editor_voice_restart_contract.py`
- `csharp/src/BiliSubStudio.Core/Editor/LocalAsrService.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `docs/engineering/EDITOR_EVENT_MAP.md`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `csharp/Directory.Build.props`
- `update/release-notes.json`
- `update/beta.json`
- `docs/migration/CSHARP_CODE_MAP.generated.md`
- `docs/handoff/CODEX_HANDOFF.md`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrTopologyBenchmark.cs`
- `csharp/src/BiliSubStudio.Core/Ocr/OcrScanner.cs`
- `csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs`
- `csharp/scripts/verify_ocr_scanner_contract.py`
- `csharp/src/BiliSubStudio.App/Pages/HardwarePage.xaml`
- `docs/migration/CSHARP_WINUI3_CALL_MAP.md`
- `docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md`

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
- OCR-TEXT-06 targeted verification: PASS — scanner and Paddle worker contract
  scripts, then Core contract tests 71/71. The regression pins the high and low
  sampled one-rune cases and unchanged Accurate three-source-frame behavior.
  No Windows video field test has been run for the sampled-mode adjustment.
- OCR-TIME-02 targeted verification: PASS — `python
  csharp/scripts/verify_ocr_scanner_contract.py`, `python
  csharp/scripts/verify_ocr_worker_contract.py`, and Core contract tests 71/71.
  The regression asserts `-copyts`, post-sampling `showinfo`, and an actual
  preserved PTS of `1000` seconds. The supplied-video FFmpeg probe also returned
  sampled source PTS `1000` and `1000.4` after a seek, which the old synthetic
  formula could not represent.
- GitHub Actions Windows installer workflow #498: PASS, 5m 50s. It created the
  155 MB installer artifact and published `v4.0.49`; release manifest commit:
  `52fa30238e06f1b9a6740c203cbd15d678179ac9`.
- Version/release: beta `4.0.0-beta.63-csharp-p5` is published as public
  prerelease `4.0.49`. No PR, merge beyond the normal main release-manifest
  commit, or source-media overwrite was performed.
- Not a functional OCR-quality PASS: Windows CI proves build/package/smoke only.
  The full new PTS path still needs the remaining supplied-video field matrix:
  CFR/VFR, seek, one/four lanes, and Pause/Resume.
- VOICE-UX-01 source/contract/build PASS: Voice start and cancel/retry contracts
  PASS; Core contract tests 71/71; WinUI Release build PASS with 0 warnings and
  0 errors. This is not a Windows TTS functional PASS: no live Whisper/TTS run,
  cancellation, retry, Preview or Export playback was performed in this task.
- ASR-UTF8-01 source/contract/build PASS: Voice ASR contracts PASS, Core
  contracts 71/71, WinUI Release build PASS with 0 warnings / 0 errors and
  migration static contract PASS. Runtime probe on six seconds of the supplied
  video with `-I -X utf8`: PASS — exit 0, ready 1, Chinese segments 4, complete
  1. A full Editor UI ASR/TTS run with the newly compiled application has not
  been performed; source media was not overwritten.
- CI-MAP-01 local verification: PASS — generator wrote the expected map and
  `verify.ps1` advanced through the generated-map gate. GitHub Actions #499 and
  #500 remain historical failures; the next pushed commit must be checked for a
  passing Windows workflow before calling CI PASS.
- OCR-AUTO-FALLBACK-01 targeted verification: PASS — OCR scanner static
  contract, Voice start/reopen contracts, Core contracts 71/71 and WinUI
  Release solution build with 0 warnings / 0 errors. The regression simulates
  8 then 7 failing and proves the selector restores 4 before choosing viable
  6. No live 5/6/7-worker Windows OCR scan or fresh packaged build has been
  field-tested yet.
- RELEASE-4.0.50 PASS: `4.0.0-beta.64-csharp-p5` / public `4.0.50` carries
  exactly the already committed Voice one-action UX, ASR UTF-8 worker output and
  OCR Auto intermediate topology fallback. `verify.ps1` rebuilt the self-contained
  beta-64 WinUI publish and its real startup/layout smoke passed. Local installer
  packaging is blocked only because this machine has no Inno Setup 7 `ISCC.exe`;
  the GitHub Windows workflow installs and verifies that compiler before packaging.
  GitHub Actions Windows workflow #502 PASS on
  `038a924f370fd581b0c1edf0c88394a8ef92d4d7`: contract/build, installer package,
  install/startup smoke, exact source identity, artifact upload and publish all
  passed. GitHub Release `v4.0.50` and the verified update manifest commit
  `d5ab2df1724618f1772a0a8fb11e20e1817e99c9` are live. This is CI/package PASS,
  not a new full Editor ASR/TTS/OCR functional field-test PASS; no source media
  is changed.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. Commit/push each completed task. Release requires
  explicit user authorization and a passing Windows build/package gate; never
  label a compile-only gate as functional field-test PASS.

# OCR Gold Harness

This executable is an opt-in runtime quality gate. It invokes OCR only through the public
`BiliSubApplication` / `OcrScanRequest` job path, inventories every decoded source frame with
ffprobe, and refuses to report `PASS` unless real fixtures were scanned and scored.

The manifest binds generated fixtures to their SHA-256 hashes. Generate them with
an explicitly licensed Chinese font before running the corpus. Missing fixtures, hashes, tools,
or malformed manifests exit with code `2`; quality failures exit with code `1`.

```powershell
python csharp/scripts/generate_ocr_gold_fixture.py --font C:\path\to\NotoSansCJKsc-Regular.otf --font-license OFL-1.1 --font-source-url https://github.com/notofonts/noto-cjk
python csharp/scripts/compare_ocr_gold.py --manifest csharp/tests/BiliSubStudio.OcrGoldHarness/fixtures/manifest.json --scratch D:\scratch\bilisub-ocr-gold --runtime-seed C:\BiliSubStudio
```

`offsetPts` is the PTS of the first frame after caption visibility (the half-open end boundary),
not the last visible frame. The generator binds each onset/end to the first native frame at or
after the render threshold, never the nearest frame. Candidate onset is the first observation's
source PTS; candidate end is the production cue's media end plus stream start time and must land
on a native frame boundary. Both observation PTS must also resolve in the complete inventory.
Temporal pairing never reads cue text; CER is calculated afterward over Unicode runes
using `ChineseSubtitleNormalizer.TryNormalize`.

All app state, build outputs, worktrees, and JSON reports are placed under the external scratch
directory. The harness rejects scratch paths inside the repository.

The v4 corpus includes the original accurate/balanced hard cues, actual VFR with alternating
60/140 ms PTS gaps and a nonzero scan offset, fractional CFR at exactly 30000/1001 (quantized to
the container time base), and a 70-second two-lane video. Its scan is [5,65), with a cue spanning
the 35-second core boundary and captions in both non-overlapping tails. The same render also
runs with four requested lanes to exercise the decoupled inference-worker queue; the runtime
report records actual worker count/kinds independently of selected and completed lanes. Generation verifies
every decoded timestamp against the intended cadence, as well as exact frame counts.

All cases require recall/precision 1, CER 0, and zero onset/offset timing error in seconds and
native frames. Frame accounting reflects production segmentation with the request's scan offset,
checks contiguous core ownership, requires the requested lane count and every lane completed,
and compares decoded totals by exact equality including overlap. Reports include segments,
expected lane frames and overlap counts. Aggregate equality is not a per-frame decode identity
trace: a compensating duplicate/drop cannot be excluded by the public aggregate counter alone.

CPU-only checks: `dotnet run --project csharp/tests/BiliSubStudio.OcrGoldHarness -- --self-test`.

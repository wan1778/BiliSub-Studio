using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Live cue list UX policy: auto-follow khi ở cuối + incremental render qua common prefix.
// Không phụ thuộc text pattern; pure logic, chạy không cần UI/video.
internal static class OcrLiveViewPolicyContract
{
    public static Task RunAsync()
    {
        // IsNearEnd boundaries.
        Check(OcrLiveViewPolicy.IsNearEnd(968, 372, 1340), "offset+viewport==scrollable must follow");
        Check(OcrLiveViewPolicy.IsNearEnd(1000, 372, 1340), "beyond end must follow");
        Check(OcrLiveViewPolicy.IsNearEnd(904, 372, 1340), "exactly 64px above end must follow");
        Check(!OcrLiveViewPolicy.IsNearEnd(903, 372, 1340), "65px above end must not follow");
        Check(OcrLiveViewPolicy.IsNearEnd(0, 372, 0), "empty list (scrollable 0) must follow");
        Check(OcrLiveViewPolicy.IsNearEnd(0, 200, 100), "short list fits viewport must follow");
        Check(OcrLiveViewPolicy.IsNearEnd(0, 0, 500), "zero viewport must follow");
        Check(OcrLiveViewPolicy.IsNearEnd(400, 100, 600, thresholdPx: 100), "custom threshold respected");
        Check(!OcrLiveViewPolicy.IsNearEnd(399, 100, 600, thresholdPx: 100), "custom threshold respected (outside)");

        // CommonCuePrefixLength.
        var a = new OcrCue(1, 2, "你好", .9);
        var b = new OcrCue(2, 3, "再见", .8);
        var c = new OcrCue(3, 4, "谢谢", .7);
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([], []) == 0, "both empty => 0");
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b], [a, b]) == 2, "identical => full");
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b], [a, b, c]) == 2, "appended tail => old count");
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b, c], [a, b]) == 2, "shrunk list => new count");
        // Tail merge: cùng count, cue cuối đổi End/Text => prefix = count-1.
        var merged = new OcrCue(2, 5, "再见朋友", .8);
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b, c], [a, merged]) == 1, "tail merge => first-changed index");
        // Confidence/support/review đổi NHƯNG Start/End/Text giữ => vẫn common (không rebuild vô ích).
        var sameIdentity = b with { Confidence = .1, RawSupportCount = 99, ConfidentSupportCount = 50, NeedsReview = true };
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b], [a, sameIdentity]) == 2, "metadata-only change must stay common");
        // Text đổi giữa chừng => dừng đúng index.
        var edited = new OcrCue(2, 3, "再见！", .8);
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a, b, c], [a, edited, c]) == 1, "mid-list edit => first-changed index");
        Check(OcrLiveViewPolicy.CommonCuePrefixLength([a], [b]) == 0, "first differs => 0");

        // Scale sanity: 10k cues append-only => prefix = 10000 (O(n) compares, ms-scale).
        var big = Enumerable.Range(0, 10000).Select(i => new OcrCue(i, i + 1, "字" + i, .9)).ToArray();
        var bigger = big.Concat([new OcrCue(10000, 10001, "尾", .9)]).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prefix = OcrLiveViewPolicy.CommonCuePrefixLength(big, bigger);
        sw.Stop();
        Check(prefix == 10000, "10k append-only prefix must be 10000");
        Check(sw.Elapsed < TimeSpan.FromSeconds(1), $"10k prefix scan must be trivial (took {sw.Elapsed.TotalMilliseconds:0}ms)");

        // Overload cho dòng pipeline (live-quét-xong = SRT): cùng semantics.
        var ra = new OcrExportRow(1, 2, "你好", false);
        var rb = new OcrExportRow(2, 3, "再见", true);
        Check(OcrLiveViewPolicy.CommonRowPrefixLength([ra, rb], [ra, rb]) == 2, "rows identical => full");
        Check(OcrLiveViewPolicy.CommonRowPrefixLength([ra, rb], [ra]) == 1, "rows shrunk => new count");
        Check(OcrLiveViewPolicy.CommonRowPrefixLength([ra], [rb]) == 0, "rows differ => 0");
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

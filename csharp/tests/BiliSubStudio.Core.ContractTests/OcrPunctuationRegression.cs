using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Regression từ field-test 02:00.300-02:02.033: caption "第二，也许有效" liên tục nhưng Paddle
// đọc rớt dấu ， ở cue 49. Candidate punctuation-only propose pixel verifier, merge bắt buộc Same.
// Không strip punctuation cho output text, không hardcode dấu/câu.
internal static class OcrPunctuationRegression
{
    private static readonly OcrRegion TestRoi = new(0, 0, 1, 1);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 4000000;

    private static OcrCue Prov(double s, double e, string t, double c)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    public static async Task RunAsync()
    {
        var same = new FakePixelComparer(PixelCheckResult.Same);
        var ct = CancellationToken.None;

        // 1. Fixture field 47-50 touching: 47↔48 exact, 48↔49 punctuation-only, 50 khác hẳn.
        var chain = new[] {
            Prov(120.300, 120.733, "第二，也许有效", 0.99),
            Prov(120.733, 121.933, "第二，也许有效", 0.98),
            Prov(121.933, 122.033, "第二也许有效", 0.97),
            Prov(122.100, 123.400, "第一，别想", 0.99),
        };
        var merged = await OcrCueReconciler.MergeTouchingIdenticalAsync(chain, "v.mp4", same, ct);
        Check(merged.Count == 2, "field 47-50 must collapse to [merged, cue50]");
        Check(merged[0].Start == 120.300 && merged[0].End == 122.033, "merged cue must span 120.300-122.033");
        Check(merged[1].Text == "第一，别想" && merged[1].Start == 122.100, "cue 50 must stay separate");
        Check(merged[0].FirstObservation?.Pts == 120.300 && merged[0].LastObservation?.Pts == 122.033,
            "chain merge must preserve First=47.First and Last=49.Last");

        // 2. Negative: khác Han (效/无) nhưng punctuation-normalized gần giống => không candidate => tách, 0 call.
        var countingNeg = new FakePixelComparer(PixelCheckResult.Same);
        var negative = new[] {
            Prov(10.000, 10.500, "第二，也许有效", 0.99),
            Prov(10.500, 11.000, "第二，也许无效", 0.98),
        };
        var negMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(negative, "v.mp4", countingNeg, ct);
        Check(negMerged.Count == 2, "punctuation-normalized Han difference must stay separate");
        Check(countingNeg.Calls == 0, "non-candidate pair must not reach pixel comparer");

        // 3. Punctuation-only candidate + Different => tách + NeedsReview.
        var diff = new FakePixelComparer(PixelCheckResult.Different);
        var diffMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(chain[1..3], "v.mp4", diff, ct);
        Check(diffMerged.Count == 2 && diffMerged.All(c => c.NeedsReview),
            "Different pixel must keep punctuation pair separate with NeedsReview");

        // 4. Punctuation-only candidate + Unavailable => tách + NeedsReview.
        var unav = new FakePixelComparer(PixelCheckResult.Unavailable);
        var unavMerged = await OcrCueReconciler.MergeTouchingIdenticalAsync(chain[1..3], "v.mp4", unav, ct);
        Check(unavMerged.Count == 2 && unavMerged.All(c => c.NeedsReview),
            "Unavailable pixel must keep punctuation pair separate with NeedsReview");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private sealed class FakePixelComparer : IPixelComparer
    {
        private readonly PixelCheckResult _result;
        public int Calls;
        public FakePixelComparer(PixelCheckResult result) => _result = result;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelComparison(_result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                System.Drawing.Rectangle.Empty, null, null, null));
        }
    }
}

using System.Drawing;
using System.Reflection;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Reconcile Replay / Targeted Regression: replay reconciliation từ checkpoint đã lưu
// KHÔNG gọi PaddleOCR worker, KHÔNG extract frame mới ngoài candidate boundaries.
// 7 asserts bên dưới chạy determinism, không cần video thật (media file rác vẫn PASS
// vì không path nào chạm FFmpeg/worker khi đã truyền fake comparer + explicit window).
internal static class OcrReplayContract
{
    private static readonly OcrRegion TestRoi = new(0.05, 0.65, 0.90, 0.29);
    private static readonly RationalTimeBase TestTb = new(1, 90000);
    private static readonly OcrFrameGeometry TestGeo = new(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
    private static long _spts = 9000000;
    private const double Window = 0.1; // explicit => bỏ qua FFmpeg probe như production đã probe

    private static OcrCue Prov(double s, double e, string t, double c)
    {
        var s1 = System.Threading.Interlocked.Increment(ref _spts);
        var s2 = System.Threading.Interlocked.Increment(ref _spts);
        return new OcrCue(s, e, t, c, 0, 0, false,
            new PixelObservation(s, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s1, TestTb),
            new PixelObservation(e, new OcrBox(10, 10, 200, 40), TestRoi, TestGeo, s2, TestTb));
    }

    private static OcrCue[] SeedCues() =>
    [
        Prov(10.0, 11.0, "龙无期", 0.99),
        Prov(11.0, 12.0, "龙无期", 0.98),
        Prov(20.0, 21.0, "这种人，不像人", 0.99),
        Prov(21.0, 22.0, "像被擦过的棋子", 0.98),
    ];

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BiliReplay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Media rác (không phải video): replay PASS trên file này chứng minh không path nào
            // chạm FFmpeg probe/extract hay PaddleOCR worker (file thật hỏng sẽ nổ ở đó).
            var garbage = Path.Combine(root, "not-a-video.mp4");
            var random = new byte[1024];
            new Random(7).NextBytes(random);
            await File.WriteAllBytesAsync(garbage, random);
            var request = new OcrScanRequest(garbage, TestRoi, "balanced", "cpu", "1", 1.0, 60.0, 0);
            var paths = AppPaths.FromRoot(root);
            var assembly = typeof(OcrScanRequest).Assembly;
            var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
                ?? throw new InvalidOperationException("missing OcrCheckpointStore");
            var segmentType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")
                ?? throw new InvalidOperationException("missing OcrScanSegment");
            var laneType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")
                ?? throw new InvalidOperationException("missing OcrLaneCheckpoint");
            var parallelType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrParallelCheckpoint")
                ?? throw new InvalidOperationException("missing OcrParallelCheckpoint");
            var store = Activator.CreateInstance(storeType, paths)
                ?? throw new InvalidOperationException("cannot create OcrCheckpointStore");
            var newAsync = storeType.GetMethod("NewAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing NewAsync");
            var saveAsync = storeType.GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing SaveAsync");
            var segmentCtor = segmentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 5);
            var laneCtor = laneType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 7);
            var parallelCtor = parallelType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 5);
            var listType = typeof(List<>).MakeGenericType(laneType);
            var listAdd = listType.GetMethod("Add") ?? throw new InvalidOperationException("missing List.Add");

            async Task SeedAsync(OcrCue[] cues)
            {
                var freshTask = (Task)(newAsync.Invoke(store, [request, 1, CancellationToken.None])
                    ?? throw new InvalidOperationException("NewAsync returned null"));
                await freshTask;
                var fresh = freshTask.GetType().GetProperty("Result")?.GetValue(freshTask)
                    ?? throw new InvalidOperationException("NewAsync returned no checkpoint");
                var keyProp = parallelType.GetProperty("Key") ?? throw new InvalidOperationException("missing Key");
                var schemaProp = parallelType.GetProperty("Schema") ?? throw new InvalidOperationException("missing Schema");
                var key = keyProp.GetValue(fresh) ?? throw new InvalidOperationException("checkpoint key is null");
                var schema = schemaProp.GetValue(fresh) ?? throw new InvalidOperationException("checkpoint schema is null");
                var segment = segmentCtor.Invoke([0, 0d, 60d, 0d, 60d]);
                var lane = laneCtor.Invoke([segment, 60d, new List<OcrCue>(cues), null, 10, 5, true]);
                var lanes = Activator.CreateInstance(listType) ?? throw new InvalidOperationException("cannot create lane list");
                listAdd.Invoke(lanes, [lane]);
                var checkpoint = parallelCtor.Invoke([schema, key, 1, lanes, 0]);
                await (Task)saveAsync.Invoke(store, [request, checkpoint, CancellationToken.None])!;
            }

            string CheckpointFile()
            {
                var files = Directory.GetFiles(Path.Combine(root, "Data", "OCRCheckpoints"), "*.json");
                if (files.Length != 1) throw new InvalidOperationException($"expected 1 checkpoint file, found {files.Length}");
                return files[0];
            }

            var app = new BiliSubApplication(paths);
            var ct = CancellationToken.None;

            // 1+2+3: full-checkpoint replay trên media rác, scripted comparer:
            // Same đúng boundary A-B (Pts 11↔11), Different mọi boundary khác.
            // Gate generic: mọi boundary trong continuation window đều tới comparer (A-B và C-D).
            await SeedAsync(SeedCues());
            var bytesBefore = await File.ReadAllBytesAsync(CheckpointFile());
            var scripted = new ScriptedPixelComparer((l, r) =>
                Math.Abs(l - 11.0) < 1e-9 && Math.Abs(r - 11.0) < 1e-9 ? PixelCheckResult.Same : PixelCheckResult.Different);
            var replayed = await app.ReconcileCheckpointAsync(request, scripted, ct, Window);
            Check(replayed.Count == 3, $"replay must merge A+B and keep C+D separate (got {replayed.Count})");
            Check(replayed[0].Start == 10.0 && replayed[0].End == 12.0 && replayed[0].Text == "龙无期",
                "replay must merge A+B into 10-12 龙无期");
            Check(replayed[1].NeedsReview && replayed[2].NeedsReview,
                "Different pixel must flag C+D with NeedsReview");
            Check(scripted.Calls == 2, $"generic gate must verify A-B and C-D (calls={scripted.Calls})");

            // 3: replay == production full-scan reconcile trên cùng cues (cùng OcrScanner.ReconcileAsync).
            var directScripted = new ScriptedPixelComparer((l, r) =>
                Math.Abs(l - 11.0) < 1e-9 && Math.Abs(r - 11.0) < 1e-9 ? PixelCheckResult.Same : PixelCheckResult.Different);
            var direct = await OcrCueReconciler.MergeTouchingIdenticalAsync(SeedCues(), garbage, directScripted, ct, Window);
            Check(direct.Count == replayed.Count, "replay must equal production reconcile cue count");
            for (var i = 0; i < direct.Count; i++)
                Check(direct[i].Start == replayed[i].Start && direct[i].End == replayed[i].End
                    && direct[i].Text == replayed[i].Text && direct[i].NeedsReview == replayed[i].NeedsReview,
                    $"replay cue {i} differs from production reconcile");

            // 4: targeted range replay giữ đúng 1 cue context mỗi phía + trace chẩn đoán.
            // B-C ngoài window => không candidate; C-D trong window => candidate + Different.
            var countingRange = new ScriptedPixelComparer((l, r) =>
                Math.Abs(l - 11.0) < 1e-9 && Math.Abs(r - 11.0) < 1e-9 ? PixelCheckResult.Same : PixelCheckResult.Different);
            var (ranged, trace) = await app.ReconcileRangeAsync(request, countingRange, 11.5, 20.5, ct, Window);
            Check(trace.Count == 3, $"range 11.5-20.5 with 1-cue context each side must trace 3 boundaries (got {trace.Count})");
            Check(trace[0].LeftStart == 10.0 && trace[2].RightEnd == 22.0,
                "range trace must span left context A start to right context D end");
            Check(trace[0].IsCandidate && trace[0].Pixel == PixelCheckResult.Same && trace[0].Merged,
                "A-B boundary must be candidate + Same + merged");
            Check(!trace[1].IsCandidate && !trace[1].Merged,
                "out-of-window boundary B-C must be non-candidate and unmerged");
            Check(trace[2].IsCandidate && trace[2].Pixel == PixelCheckResult.Different && !trace[2].Merged,
                "in-window distinct boundary C-D must be candidate + Different + unmerged");
            Check(ranged.Count == 3 && ranged[0].Start == 10.0 && ranged[0].End == 12.0,
                "range merge must collapse A+B only");

            // 5: replay không được sửa checkpoint gốc (raw immutable).
            var bytesAfter = await File.ReadAllBytesAsync(CheckpointFile());
            Check(bytesBefore.AsSpan().SequenceEqual(bytesAfter), "replay must not rewrite the raw checkpoint file");

            // 6: checkpoint thiếu provenance => safe-fail, không reconstruct/fabricate.
            var bare = SeedCues().Select(cue => cue with { FirstObservation = null, LastObservation = null }).ToArray();
            await SeedAsync(bare);
            await ThrowsAsync<CheckpointNotEligibleForReplayException>(
                () => app.ReconcileCheckpointAsync(request, new FakePixelComparer(PixelCheckResult.Same), ct, Window),
                "checkpoint without pixel provenance must be rejected for replay");

            // 7: Different ở mọi boundary => giữ tách + NeedsReview tất cả (single generic pass).
            await SeedAsync(SeedCues());
            var diff = new FakePixelComparer(PixelCheckResult.Different);
            var held = await app.ReconcileCheckpointAsync(request, diff, ct, Window);
            Check(held.Count == 4 && held.All(c => c.NeedsReview),
                "Different pixel must keep all cues separate with NeedsReview");
            Check(diff.Calls == 2, $"single pass must verify A-B and C-D once each (calls={diff.Calls})");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
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
                Rectangle.Empty, null, null, null));
        }
    }

    // Verdict theo (left.Pts, right.Pts): mô phỏng pixel thật (Same trong instance, Different qua handoff).
    private sealed class ScriptedPixelComparer : IPixelComparer
    {
        private readonly Func<double, double, PixelCheckResult> _verdict;
        public int Calls;
        public ScriptedPixelComparer(Func<double, double, PixelCheckResult> verdict) => _verdict = verdict;
        public Task<PixelComparison> CheckAsync(string videoPath, PixelObservation left, PixelObservation right, CancellationToken cancellationToken)
        {
            Calls++;
            var result = _verdict(left.Pts, right.Pts);
            return Task.FromResult(new PixelComparison(result,
                new FrameSample(left.Pts, left.Pts, 0),
                new FrameSample(right.Pts, right.Pts, 0),
                Rectangle.Empty, null, null, null));
        }
    }
}

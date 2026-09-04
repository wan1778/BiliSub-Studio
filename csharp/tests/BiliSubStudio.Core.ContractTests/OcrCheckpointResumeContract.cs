using System.Linq;
using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Checkpoint resume float tolerance: writer BuildSegments tính CoreEnd = offset +
// duration*N/N bằng floating-point nên có thể sludge ~1e-12 trên ScanEnd
// (field: ScanEnd 7469.418 < CoreEnd 7469.418000000001). Validator phải dùng cùng
// tolerance ±0.001 như mọi bound check lân cận, nếu không checkpoint hợp lệ bị
// reject và Resume luôn báo "Không còn checkpoint OCR phù hợp".
internal static class OcrCheckpointResumeContract
{
    private const double Duration = 7469.418;

    public static async Task RunAsync()
    {
        var assembly = typeof(OcrResult).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var segmentType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrScanSegment")
            ?? throw new InvalidOperationException("missing OcrScanSegment");
        var laneType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrLaneCheckpoint")
            ?? throw new InvalidOperationException("missing OcrLaneCheckpoint");
        var parallelType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrParallelCheckpoint")
            ?? throw new InvalidOperationException("missing OcrParallelCheckpoint");
        var isValid = storeType.GetMethod("IsValidCheckpoint", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing IsValidCheckpoint");
        var schema = (int)storeType.GetField("Schema", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        var segmentConstructor = segmentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 5);
        var laneConstructor = laneType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 7);
        var parallelConstructor = parallelType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 5);
        var laneListType = typeof(List<>).MakeGenericType(laneType);
        var laneAdd = laneListType.GetMethod("Add")!;

        object Checkpoint(double coreEnd, double scanEnd)
        {
            var segment = segmentConstructor.Invoke([0, 0d, coreEnd, 0d, scanEnd]);
            var lane = laneConstructor.Invoke([segment, scanEnd, new List<OcrCue>(), null, 10, 5, false]);
            var lanes = Activator.CreateInstance(laneListType)!;
            laneAdd.Invoke(lanes, [lane]);
            return parallelConstructor.Invoke([schema, "key", 1, lanes, 0]);
        }

        bool Valid(object checkpoint, double duration = Duration, double offset = 0)
            => (bool)isValid.Invoke(null, [checkpoint, duration, offset])!;

        // 1. Exact valid: ScanEnd == CoreEnd.
        Check(Valid(Checkpoint(Duration, Duration)), "exact ScanEnd==CoreEnd must be valid");
        // 2. Tiny floating drift (field repro): ScanEnd 7469.418, CoreEnd 7469.418000000001.
        Check(Valid(Checkpoint(7469.418000000001, Duration)), "1e-12 float drift must be valid");
        // 3. Drift dưới tolerance 0.0005.
        Check(Valid(Checkpoint(Duration + 0.0005, Duration)), "0.0005 drift must be valid");
        // 4. Boundary tolerance: drift 0.0009 (ngay trong 1ms) => VALID.
        // Không assert đúng 0.001: float không biểu diễn được 0.001 chính xác nên biên
        // tuyệt đối là may rủi ULP; hơn nữa span check độc lập
        // |CoreEnd - duration| <= 0.001 cũng reject đúng tại biên tuyệt đối.
        // 0.0009 fail dưới code cũ (strict ScanEnd < CoreEnd) và pass với fix => đúng regression.
        Check(Valid(Checkpoint(Duration + 0.0009, Duration)), "0.0009 drift must be valid");
        // 5. Real invalid: vượt tolerance.
        Check(!Valid(Checkpoint(Duration + 0.0011, Duration)), "0.0011 drift must be invalid");
        Check(!Valid(Checkpoint(Duration + 0.002, Duration)), "0.002 drift must be invalid");

        // 6. Checkpoint schema hiện tại + valid provenance => LoadAsync không null.
        var root = Path.Combine(Path.GetTempPath(), "BiliReplayResume_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Path.Combine(root, "video.mp4");
            await File.WriteAllBytesAsync(video, new byte[1024]);
            var region = new OcrRegion(0.05, 0.65, 0.90, 0.29);
            var request = new OcrScanRequest(video, region, "accurate", "gpu", "1", 1.0, 100.0, 0);
            var paths = AppPaths.FromRoot(root);
            var store = Activator.CreateInstance(storeType, paths)
                ?? throw new InvalidOperationException("cannot create OcrCheckpointStore");
            var newAsync = storeType.GetMethod("NewAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing NewAsync");
            var saveAsync = storeType.GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing SaveAsync");
            var loadAsync = storeType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing LoadAsync");
            var freshTask = (Task)newAsync.Invoke(store, [request, 1, CancellationToken.None])!;
            await freshTask;
            var fresh = freshTask.GetType().GetProperty("Result")?.GetValue(freshTask)
                ?? throw new InvalidOperationException("NewAsync returned no checkpoint");
            // Gắn cue có provenance đầy đủ (pixel observation 2 đầu) rồi save/load roundtrip.
            var tb = new RationalTimeBase(1, 90000);
            var geo = new OcrFrameGeometry(0, 0, 1920, 1080, 1280, 320, 0, 0, 1280, 320);
            var cue = new OcrCue(10.0, 11.0, "龙无期", 0.99, 5, 5, false,
                new PixelObservation(10.0, new OcrBox(10, 10, 200, 40), region, geo, 900001, tb),
                new PixelObservation(11.0, new OcrBox(10, 10, 200, 40), region, geo, 990001, tb));
            var lanesProp = parallelType.GetProperty("Lanes") ?? throw new InvalidOperationException("missing Lanes");
            var lanesList = (System.Collections.IList)lanesProp.GetValue(fresh)!;
            var firstLane = lanesList[0]!;
            var cuesProp = laneType.GetProperty("Cues") ?? throw new InvalidOperationException("missing Cues");
            ((List<OcrCue>)cuesProp.GetValue(firstLane)!).Add(cue);
            await (Task)saveAsync.Invoke(store, [request, fresh, CancellationToken.None])!;
            var loadTask = (Task)loadAsync.Invoke(store, [request, CancellationToken.None])!;
            await loadTask;
            var loaded = loadTask.GetType().GetProperty("Result")?.GetValue(loadTask);
            Check(loaded is not null, "schema-15 checkpoint with valid provenance must load (not null)");
            var loadedCues = (System.Collections.IList)cuesProp.GetValue(
                ((System.Collections.IList)lanesProp.GetValue(loaded)!)![0]!)!;
            Check(loadedCues.Count == 1, "loaded checkpoint must preserve the provenance cue");
            var loadedCue = (OcrCue)loadedCues[0]!;
            Check(loadedCue.FirstObservation?.SourcePts == 900001 && loadedCue.LastObservation?.SourcePts == 990001,
                "loaded cue must preserve pixel provenance (SourcePts both ends)");
            Check(OcrCueReconciler.HasPixelProvenance(loadedCues.Cast<OcrCue>().First()),
                "loaded cue must satisfy HasPixelProvenance for replay");
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
}

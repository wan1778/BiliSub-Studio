using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Danh sách loại trừ lúc xuất SRT: khớp chính xác Start (±1ms) + Text từng chữ.
// Không khớp mờ, không lan; thiếu/hỏng file = không loại trừ (không fail export);
// xóa file là hoàn tác. Checkpoint/resume không đụng.
internal static class OcrExportExclusionContract
{
    public static async Task RunAsync()
    {
        ExactMatchDrops();
        NormalizedFossilMatches();
        ToleranceBoundary();
        TextMismatchKeeps();
        NullEntriesPassthrough();
        DroppedSamplesCapped();
        await LoadMissingAndCorruptAsync();
        await LoadRoundTripAsync();
        MarkReviewedKeepsOthers();
        await SaveExclusionsMergesAsync();
        await RemoveExclusionsRoundTripAsync();
    }

    private static OcrCue Cue(double start, string text) =>
        new OcrCue(start, start + 1.0, text, 0.99);

    private static void ExactMatchDrops()
    {
        var cues = new[]
        {
            Cue(10.0, "你好"),
            Cue(11.0, "团支书画画 1269559910510"),
            Cue(12.0, "再见"),
        };
        var result = OcrExportExclusions.Apply(cues,
            [new OcrCueExclusion(11.0, "团支书画画 1269559910510")]);
        Check(result.Kept.Count == 2, "khớp chính xác phải bỏ đúng 1 câu");
        Check(result.Dropped == 1, "đếm bỏ sai");
        Check(result.Kept[0].Text == "你好" && result.Kept[1].Text == "再见",
            "câu lành kề bên bị vạ lây");
    }

    private static void NormalizedFossilMatches()
    {
        // Entry hóa thạch ghi chữ thô (kèm space/dấu/fullwidth), cue ở export
        // đã normalize -> khớp tuyệt đối hụt (entry chết, cue rác sống lại).
        // Fallback chuẩn hóa 2 vế cứu đúng entry (18/09: 8 entry gồm notebook
        // 729.333 suýt vô hình).
        var cues = new[] { Cue(10.0, "你好"), Cue(11.0, "再见") };
        var trailingSpace = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(10.0, "你好 ")]);
        Check(trailingSpace.Dropped == 1 && trailingSpace.Kept.Count == 1
            && trailingSpace.Kept[0].Text == "再见", "entry space thừa phải khớp");
        // Không khớp bừa: chữ khác vẫn giữ dù space giống nhau.
        var wrongText = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(10.0, "再见 ")]);
        Check(wrongText.Dropped == 0 && wrongText.Kept.Count == 2, "khác chữ không được lan");
        // Lệch giờ vẫn hụt (fallback chỉ nới chữ, không nới giờ).
        var late = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(10.002, "你好 ")]);
        Check(late.Dropped == 0, "lệch giờ phải hụt");
    }

    private static void ToleranceBoundary()
    {
        var cues = new[] { Cue(20.0, "川"), Cue(30.0, "川") };
        // Bien dust-proof (cach xa 0.001 gap 2×): production so <= 0.001 giữ nguyên.
        var atEdge = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(20.0005, "川")]);
        Check(atEdge.Dropped == 1 && atEdge.Kept.Count == 1, "lệch trong 1ms phải bỏ");
        var pastEdge = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(30.002, "川")]);
        Check(pastEdge.Dropped == 0 && pastEdge.Kept.Count == 2, "lệch quá 1ms phải giữ");
    }

    private static void TextMismatchKeeps()
    {
        var cues = new[] { Cue(40.0, "你们口中的那位真爱") };
        var result = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(40.0, "你们口中的那位假爱")]);
        Check(result.Dropped == 0 && result.Kept.Count == 1, "khác chữ phải giữ");
        var nullText = OcrExportExclusions.Apply(cues,
            [new OcrCueExclusion(40.0, null!), new OcrCueExclusion(41.0, "x")]);
        Check(nullText.Dropped == 0, "entry null phải bỏ qua");
    }

    private static void NullEntriesPassthrough()
    {
        var cues = new[] { Cue(50.0, "你好") };
        Check(OcrExportExclusions.Apply(cues, null).Kept.Count == 1, "null entries phải passthrough");
        Check(OcrExportExclusions.Apply(cues, []).Kept.Count == 1, "entries rỗng phải passthrough");
        Check(OcrExportExclusions.Apply([], [new OcrCueExclusion(50.0, "你好")]).Dropped == 0,
            "cues rỗng không được đếm bỏ");
    }

    private static void DroppedSamplesCapped()
    {
        var cues = Enumerable.Range(0, 10).Select(index => Cue(index, " rác" + index)).ToArray();
        var entries = cues.Select(cue => new OcrCueExclusion(cue.Start, cue.Text)).ToArray();
        var result = OcrExportExclusions.Apply(cues, entries);
        Check(result.Dropped == 10 && result.Kept.Count == 0, "phải bỏ hết 10");
        Check(result.DroppedSamples.Count == 5, "mẫu bỏ phải cắt ở 5");
        // Dấu thập phân theo culture máy ($"" display-only, không phải logic).
        Check(result.DroppedSamples[0].Contains("0.000") || result.DroppedSamples[0].Contains("0,000"),
            "mẫu phải có giờ để soát");
    }

    private static async Task LoadMissingAndCorruptAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-excl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, request) = OpenStore(root);
            var missing = await LoadAsync((object)store, request);
            Check(missing.Count == 0, "thiếu file phải về rỗng");
            Check(!Directory.Exists(Path.Combine(root, "Data")),
                "load không được tạo thư mục");
            var key = await StoreKeyAsync((object)store, request);
            var dir = Path.Combine(root, "Data", "OCRCheckpoints");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, key + ".exclude.json"), "{hong", CancellationToken.None);
            var corrupt = await LoadAsync((object)store, request);
            Check(corrupt.Count == 0, "file hỏng phải về rỗng, không fail export");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task LoadRoundTripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-excl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, request) = OpenStore(root);
            var key = await StoreKeyAsync((object)store, request);
            var dir = Path.Combine(root, "Data", "OCRCheckpoints");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, key + ".exclude.json"),
                """
                [{"start":2054.833,"text":"川"},{"start":727.5,"text":"团文书画画。副班长逃课 1269559910510"}]
                """, CancellationToken.None);
            var loaded = await LoadAsync((object)store, request);
            Check(loaded.Count == 2, "round-trip mất entry");
            Check(Math.Abs(loaded[0].Start - 2054.833) < 1e-9 && loaded[0].Text == "川",
                "chữ Hoa round-trip sai");
            var cues = new[] { Cue(2054.833, "川"), Cue(2055.2, "你们口中的那位真爱") };
            var result = OcrExportExclusions.Apply(cues, loaded);
            Check(result.Dropped == 1 && result.Kept.Count == 1
                && result.Kept[0].Text == "你们口中的那位真爱",
                "entry thật không bỏ đúng câu 川 / giữ câu thoại");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (dynamic Store, OcrScanRequest Request) OpenStore(string root)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var video = Path.Combine(root, "v.mp4");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(video, new byte[] { 1, 2, 3 });
        var paths = AppPaths.FromRoot(root);
        var store = Activator.CreateInstance(storeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [paths], culture: null)
            ?? throw new InvalidOperationException("cannot create store");
        var request = new OcrScanRequest(video, new OcrRegion(0, 0, 1, 1),
            "accurate", "gpu", "auto", 1, 100);
        return (store, request);
    }

    private static async Task<IReadOnlyList<OcrCueExclusion>> LoadAsync(object store, OcrScanRequest request)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var load = storeType.GetMethod("LoadExclusionsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing LoadExclusionsAsync");
        var task = (Task<IReadOnlyList<OcrCueExclusion>>)load.Invoke(store, [request, CancellationToken.None])!;
        return await task;
    }

    private static void MarkReviewedKeepsOthers()
    {
        var entries = new List<OcrCueVerdict>
        {
            new(10.0, "川", "drop", "noise", "m", "2026-09-16"),
            new OcrCueVerdict(11.0, "你好", "keep", "caption", "m", "2026-09-16") { Reviewed = true },
            new(12.0, "再见", "drop", "noise", "m", "2026-09-16"),
        };
        var (marked, found) = OcrVerdictJournal.TryMarkReviewed(entries, 12.0, "再见");
        Check(found, "đánh dấu đúng câu phải found");
        Check(marked.Count == 3, "đánh dấu không được mất câu");
        Check(marked[2].Reviewed && !marked[0].Reviewed && marked[1].Reviewed,
            "chỉ câu được chọn đổi cờ, câu khác giữ nguyên");
        var (same, missed) = OcrVerdictJournal.TryMarkReviewed(marked, 99.0, "không có");
        Check(!missed && same.Count == 3, "không khớp phải báo miss, giữ nguyên list");
        var (again, refound) = OcrVerdictJournal.TryMarkReviewed(marked, 12.0, "再见");
        Check(refound && again.Count == 3, "duyệt lại câu đã duyệt vẫn found (idempotent)");
    }

    private static async Task SaveExclusionsMergesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-excl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, request) = OpenStore(root);
            var saved = await SaveAsync((object)store, request,
                [new OcrCueExclusion(10.0, "川"), new OcrCueExclusion(11.0, "你好")]);
            Check(saved == 2, "lưu mới phải báo 2");
            var again = await SaveAsync((object)store, request,
                [new OcrCueExclusion(10.0005, "川"), new OcrCueExclusion(12.0, "再见")]);
            Check(again == 1, "trùng (±1ms + chữ) không được đếm lại, câu mới phải thêm");
            var loaded = await LoadAsync((object)store, request);
            Check(loaded.Count == 3, "gộp file phải có đúng 3 entry");
            var cues = new[] { Cue(10.0, "川"), Cue(11.0, "你好"), Cue(12.0, "再见"), Cue(13.0, "明天见") };
            var result = OcrExportExclusions.Apply(cues, loaded);
            Check(result.Dropped == 3 && result.Kept.Count == 1 && result.Kept[0].Text == "明天见",
                "exclusion sau append phải bỏ đúng 3, giữ câu lành");
            var resaved = await SaveAsync((object)store, request, []);
            Check(resaved == 0, "append rỗng phải báo 0, không đụng file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<int> SaveAsync(object store, OcrScanRequest request, IReadOnlyList<OcrCueExclusion> additions)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var save = storeType.GetMethod("SaveExclusionsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SaveExclusionsAsync");
        var task = (Task<int>)save.Invoke(store, [request, additions, CancellationToken.None])!;
        return await task;
    }

    // Hoàn tác xoá tay (21/09): nút "Hoàn tác xóa" trong app gọi đúng entry đã
    // ghi. Gỡ xong câu quay lại SRT, câu lành không vạ lây; no-op phải an toàn.
    private static async Task RemoveExclusionsRoundTripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-excl-rm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, request) = OpenStore(root);
            await SaveAsync((object)store, request,
                [new OcrCueExclusion(10.0, "川"), new OcrCueExclusion(11.0, "你好"), new OcrCueExclusion(12.0, "再见")]);
            var removed = await RemoveAsync((object)store, request, [new OcrCueExclusion(11.0, "你好")]);
            Check(removed == 1, "gỡ đúng 1 entry");
            var loaded = await LoadAsync((object)store, request);
            Check(loaded.Count == 2 && loaded.All(entry => entry.Text != "你好"),
                "sau gỡ phải còn 2 entry và mất đúng câu đã gỡ");
            var cues = new[] { Cue(10.0, "川"), Cue(11.0, "你好"), Cue(12.0, "再见") };
            var result = OcrExportExclusions.Apply(cues, loaded);
            Check(result.Dropped == 2 && result.Kept.Count == 1 && result.Kept[0].Text == "你好",
                "câu đã gỡ exclusion phải trở lại SRT");
            // Không khớp / gỡ hai lần: no-op, file giữ nguyên.
            var noop = await RemoveAsync((object)store, request,
                [new OcrCueExclusion(11.0, "你好"), new OcrCueExclusion(99.0, "không có")]);
            Check(noop == 0, "gỡ hụt phải báo 0, không đụng file");
            Check((await LoadAsync((object)store, request)).Count == 2, "no-op không được phá file");
            // Dung sai ±1ms giống append (hoàn tác entry mình vừa ghi bất kể
            // tick hiển thị).
            var tolerant = await RemoveAsync((object)store, request, [new OcrCueExclusion(10.0005, "川")]);
            Check(tolerant == 1, "gỡ lệch trong ±1ms phải khớp như append");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<int> RemoveAsync(object store, OcrScanRequest request, IReadOnlyList<OcrCueExclusion> removals)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var remove = storeType.GetMethod("RemoveExclusionsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing RemoveExclusionsAsync");
        var task = (Task<int>)remove.Invoke(store, [request, removals, CancellationToken.None])!;
        return await task;
    }

    private static async Task<string> StoreKeyAsync(object store, OcrScanRequest request)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var keyAsync = storeType.GetMethod("KeyAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing KeyAsync");
        var task = (Task<string>)keyAsync.Invoke(store, [request, 19, CancellationToken.None])!;
        return await task;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

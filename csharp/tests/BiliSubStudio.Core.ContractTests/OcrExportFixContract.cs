using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Đắp chữ sửa AI lúc xuất SRT (luật cuối 16/09): fix khớp Start (±1ms) + chữ cũ
// từng chữ thì thay chữ đúng; chạy SAU loại trừ, TRƯỚC normalize. Không khớp mờ,
// không lan. File .fix.json cạnh checkpoint, xóa là hoàn tác. Lưu hỏng/thiếu =
// xuất như cũ, không bao giờ chặn export.
internal static class OcrExportFixContract
{
    public static async Task RunAsync()
    {
        ApplyReplacesExactMatch();
        ApplyMatchesNormalizedFossil();
        ApplySkipsNonMatch();
        ApplyAfterExclusion();
        ApplyFlagReviewMarksCue();
        ApplySameTextFlagOnlyMarks();
        await SaveFixesMergesAsync();
    }

    private static OcrCue Cue(double start, string text) =>
        new(start, start + 1.0, text, 0.99);

    private static void ApplyReplacesExactMatch()
    {
        var cues = new[] { Cue(10.0, "说了多少变了"), Cue(11.0, "你好") };
        var result = OcrExportFixes.Apply(cues,
            [new OcrCueFix(10.0, "说了多少变了", "说了多少遍了")]);
        Check(result.Fixed == 1, "phải sửa đúng 1 câu");
        Check(result.Cues[0].Text == "说了多少遍了", "chữ sửa sai");
        Check(result.Cues[0].Start == 10.0 && result.Cues[0].End == 11.0, "sửa không được đụng giờ");
        Check(result.Cues[1].Text == "你好", "câu lành phải nguyên");
        Check(result.FixedSamples.Count == 1, "thiếu mẫu kiểm tra");
    }

    private static void ApplyMatchesNormalizedFossil()
    {
        // Fix hóa thạch ghi chữ cũ thô (space thừa), cue ở export đã normalize
        // -> khớp tuyệt đối hụt (fix chết). Fallback chuẩn hóa 2 vế cứu đúng.
        var cues = new[] { Cue(10.0, "你好"), Cue(11.0, "再见") };
        var result = OcrExportFixes.Apply(cues, [new OcrCueFix(10.0, "你好 ", "您好")]);
        Check(result.Fixed == 1 && result.Cues[0].Text == "您好", "fix space thừa phải khớp");
        Check(result.Cues[1].Text == "再见", "câu lành phải nguyên");
        var wrongText = OcrExportFixes.Apply(cues, [new OcrCueFix(10.0, "再见 ", "xxx")]);
        Check(wrongText.Fixed == 0, "khác chữ không được lan");
    }

    private static void ApplySkipsNonMatch()
    {
        var cues = new[] { Cue(10.0, "说了多少变了") };
        // Lệch giờ quá 1ms -> hụt (vô hại).
        var late = OcrExportFixes.Apply(cues, [new OcrCueFix(10.002, "说了多少变了", "说了多少遍了")]);
        Check(late.Fixed == 0 && late.Cues[0].Text == "说了多少变了", "lệch giờ phải hụt");
        // Khác chữ -> hụt.
        var other = OcrExportFixes.Apply(cues, [new OcrCueFix(10.0, "说了多少遍了", "xxx")]);
        Check(other.Fixed == 0, "khác chữ cũ phải hụt");
        // Chữ mới rỗng -> bỏ qua.
        var empty = OcrExportFixes.Apply(cues, [new OcrCueFix(10.0, "说了多少变了", "")]);
        Check(empty.Fixed == 0, "chữ mới rỗng phải bỏ qua");
        // Không danh sách = đường cũ.
        var none = OcrExportFixes.Apply(cues, null);
        Check(none.Fixed == 0 && none.Cues.Count == 1, "null phải giữ nguyên");
        Check(OcrExportFixes.Apply([], [new OcrCueFix(1.0, "a", "b")]).Fixed == 0, "rỗng phải 0");
    }

    private static void ApplyAfterExclusion()
    {
        // Câu đã xóa thì khỏi sửa: exclusion chạy trước, fix chỉ thấy câu còn lại.
        var cues = new[] { Cue(10.0, "水印88"), Cue(11.0, "说了多少变了") };
        var kept = OcrExportExclusions.Apply(cues, [new OcrCueExclusion(10.0, "水印88")]);
        Check(kept.Dropped == 1 && kept.Kept.Count == 1, "exclusion trước sai");
        var patched = OcrExportFixes.Apply(kept.Kept,
            [new OcrCueFix(10.0, "水印88", "xxx"), new OcrCueFix(11.0, "说了多少变了", "说了多少遍了")]);
        Check(patched.Fixed == 1 && patched.Cues[0].Text == "说了多少遍了",
            "fix sau exclusion sai (câu đã xóa không được sống lại)");
    }

    // Fix tai-sửa-chữ (FlagReview) thì cue hiện ⚠️; fix thường không đổi cờ.
    private static void ApplyFlagReviewMarksCue()
    {
        var cues = new[] { Cue(10.0, "云烟很天"), Cue(11.0, "你好") };
        var patched = OcrExportFixes.Apply(cues,
            [new OcrCueFix(10.0, "云烟很天", "云烟很大", FlagReview: true)]);
        Check(patched.Fixed == 1 && patched.Cues[0].Text == "云烟很大", "fix sai chữ");
        Check(patched.Cues[0].NeedsReview, "fix tai-sửa phải gắn cờ review");
        Check(!patched.Cues[1].NeedsReview, "câu không fix không được gắn cờ");
        var plain = OcrExportFixes.Apply(cues,
            [new OcrCueFix(10.0, "云烟很天", "云烟很大")]);
        Check(plain.Fixed == 1 && !plain.Cues[0].NeedsReview, "fix thường không được gắn cờ");
    }

    // Fix giữ-nguyên-chữ (F2 chữ-cảnh-nghi-vấn) chỉ gắn cờ, không đổi chữ,
    // không ghi mẫu "A→A".
    private static void ApplySameTextFlagOnlyMarks()
    {
        var cues = new[] { Cue(10.0, "金陵武道九班档案") };
        var patched = OcrExportFixes.Apply(cues,
            [new OcrCueFix(10.0, "金陵武道九班档案", "金陵武道九班档案", FlagReview: true)]);
        Check(patched.Fixed == 1, "fix giữ chữ phải khớp");
        Check(patched.Cues[0].Text == "金陵武道九班档案", "giữ chữ không được đổi");
        Check(patched.Cues[0].NeedsReview, "phải gắn cờ review");
        Check(patched.FixedSamples.Count == 0, "không được ghi mẫu A→A");
    }

    private static (object Store, OcrScanRequest Request) OpenStore(string root)
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
        var request = new OcrScanRequest(video, new OcrRegion(0.22, 0.79, 0.54, 0.20),
            "accurate", "gpu", "auto", 0.5, 100.0);
        return (store, request);
    }

    private static async Task<int> SaveAsync(object store, OcrScanRequest request, IReadOnlyList<OcrCueFix> additions)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var save = storeType.GetMethod("SaveFixesAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing SaveFixesAsync");
        var task = (Task<int>)save.Invoke(store, [request, additions, CancellationToken.None])!;
        return await task;
    }

    private static async Task<IReadOnlyList<OcrCueFix>> LoadAsync(object store, OcrScanRequest request)
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var storeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OcrCheckpointStore");
        var load = storeType.GetMethod("LoadFixesAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing LoadFixesAsync");
        var task = (Task<IReadOnlyList<OcrCueFix>>)load.Invoke(store, [request, CancellationToken.None])!;
        return await task;
    }

    private static async Task SaveFixesMergesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-fix-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, request) = OpenStore(root);
            var saved = await SaveAsync((object)store, request,
                [new OcrCueFix(10.0, "变", "遍"), new OcrCueFix(11.0, "你好", "您好")]);
            Check(saved == 2, "lưu mới phải báo 2");
            // Trùng (Start ±1ms + chữ cũ) thì bản sau GHI ĐÈ, không đếm.
            var overwrite = await SaveAsync((object)store, request,
                [new OcrCueFix(10.0005, "变", "辨"), new OcrCueFix(12.0, "再见", "再會")]);
            Check(overwrite == 1, "ghi đè không được đếm, câu mới phải thêm");
            var loaded = await LoadAsync((object)store, request);
            Check(loaded.Count == 3, "gộp file phải có đúng 3 entry");
            var first = loaded.First(entry => Math.Abs(entry.Start - 10.0) < 0.01);
            Check(first.NewText == "辨", "bản sau phải thắng");
            var result = OcrExportFixes.Apply(
                [Cue(10.0, "变"), Cue(11.0, "你好"), Cue(12.0, "再见"), Cue(13.0, "明天见")],
                loaded);
            Check(result.Fixed == 3 && result.Cues[3].Text == "明天见", "đắp sau gộp sai");
            var resaved = await SaveAsync((object)store, request, []);
            Check(resaved == 0, "append rỗng phải báo 0, không đụng file");
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

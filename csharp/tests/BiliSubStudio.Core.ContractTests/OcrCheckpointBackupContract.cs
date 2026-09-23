using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// File .bak chống crash chỉ cần lúc đang quét; quét xong sạch thì tự xóa cho
// gọn đĩa (vài chục MB). File chính + sổ tay (loại/fix) giữ nguyên để tiếp
// tục/xuất lại. Tạm dừng/hủy/lỗi thì không dọn.
internal static class OcrCheckpointBackupContract
{
    [ModuleInitializer]
    internal static void Install()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Program).TypeHandle);
        var field = typeof(Program).GetField("Tests", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Core contract list");
        var tests = field.GetValue(null) as List<(string Name, Func<Task> Test)>
            ?? throw new InvalidOperationException("Core contract list type drifted");
        tests.Add(("OCR checkpoint backup is deleted after clean scan, main file stays", RunAsync));
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BiliCkptBak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var garbage = Path.Combine(root, "not-a-video.mp4");
            await File.WriteAllBytesAsync(garbage, [1, 2, 3, 4]);
            var request = new OcrScanRequest(garbage, new OcrRegion(0.05, 0.65, 0.90, 0.29), "balanced", "cpu", "1", 1.0, 60.0, 0);
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
            var deleteBackupAsync = storeType.GetMethod("DeleteBackupAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing DeleteBackupAsync");
            var segmentCtor = segmentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 5);
            var laneCtor = laneType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 7);
            var parallelCtor = parallelType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(x => x.GetParameters().Length == 5);
            var listType = typeof(List<>).MakeGenericType(laneType);
            var listAdd = listType.GetMethod("Add") ?? throw new InvalidOperationException("missing List.Add");

            async Task SaveOnceAsync()
            {
                var freshTask = (Task)(newAsync.Invoke(store, [request, 1, CancellationToken.None])
                    ?? throw new InvalidOperationException("NewAsync returned null"));
                await freshTask;
                var fresh = freshTask.GetType().GetProperty("Result")?.GetValue(freshTask)
                    ?? throw new InvalidOperationException("NewAsync returned no checkpoint");
                var keyProp = parallelType.GetProperty("Key") ?? throw new InvalidOperationException("missing Key");
                var schemaProp = parallelType.GetProperty("Schema") ?? throw new InvalidOperationException("missing Schema");
                var segment = segmentCtor.Invoke([0, 0d, 60d, 0d, 60d]);
                var lane = laneCtor.Invoke([segment, 60d, new List<OcrCue>(), null, 10, 5, true]);
                var lanes = Activator.CreateInstance(listType) ?? throw new InvalidOperationException("cannot create lane list");
                listAdd.Invoke(lanes, [lane]);
                var checkpoint = parallelCtor.Invoke([schemaProp.GetValue(fresh), keyProp.GetValue(fresh), 1, lanes, 0]);
                await (Task)saveAsync.Invoke(store, [request, checkpoint, CancellationToken.None])!;
            }

            var checkpointDir = Path.Combine(root, "Data", "OCRCheckpoints");
            await SaveOnceAsync();
            await SaveOnceAsync();
            Check(Directory.GetFiles(checkpointDir, "*.json").Length == 1, "phải có đúng 1 file checkpoint chính");
            Check(Directory.GetFiles(checkpointDir, "*.json.bak").Length == 1, "lưu lần 2 phải xoay ra .bak");
            await (Task)deleteBackupAsync.Invoke(store, [request, CancellationToken.None])!;
            Check(Directory.GetFiles(checkpointDir, "*.json.bak").Length == 0, ".bak phải bị xóa sau quét xong");
            Check(Directory.GetFiles(checkpointDir, "*.json").Length == 1, "file checkpoint chính phải còn nguyên");
            // Mất rồi gọi nữa cũng không được throw.
            await (Task)deleteBackupAsync.Invoke(store, [request, CancellationToken.None])!;
            Check(Directory.GetFiles(checkpointDir, "*.json").Length == 1, "gọi dọn thừa làm mất file chính");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

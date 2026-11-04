using System.Reflection;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Listen-only (Whisper-only, 23/09): the project id is stable across the file fingerprint so stopping
// and resuming reattaches to the right journal; reading a partial journal tolerates missing/corrupt files (it never throws
// out to the live UI).
internal static class OcrListenContract
{
    public static Task RunAsync()
    {
        ProjectIdStable();
        ProjectIdChangesWithDuration();
        MissingVideoThrows();
        MissingCheckpointReadsEmpty();
        CorruptCheckpointReadsEmpty();
        ValidCheckpointParses();
        return Task.CompletedTask;
    }

    private static string MakeVideo()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocr-listen-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static void ProjectIdStable()
    {
        var video = MakeVideo();
        try
        {
            var first = BiliSubApplication.OcrListenProjectId(video, 100.0);
            var second = BiliSubApplication.OcrListenProjectId(video, 100.0);
            Check(first == second && first.StartsWith("nghe-", StringComparison.Ordinal),
                "mã project nghe phải ổn định để nối đúng sổ dở");
        }
        finally { TryDelete(video); }
    }

    private static void ProjectIdChangesWithDuration()
    {
        var video = MakeVideo();
        try
        {
            var short_ = BiliSubApplication.OcrListenProjectId(video, 100.0);
            var long_ = BiliSubApplication.OcrListenProjectId(video, 200.0);
            Check(short_ != long_, "khác thời lượng phải khác mã project");
        }
        finally { TryDelete(video); }
    }

    private static void MissingVideoThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ocr-listen-missing-" + Guid.NewGuid().ToString("N") + ".mp4");
        var thrown = false;
        try { BiliSubApplication.OcrListenProjectId(missing, 10.0); }
        catch (FileNotFoundException) { thrown = true; }
        Check(thrown, "video không có phải báo rõ, không đẻ mã bừa");
    }

    private static void MissingCheckpointReadsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ocr-listen-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var cues = ReadCheckpoint(missing);
        Check(cues is null, "thiếu sổ dở phải báo không có để thử file tiếp, không throw");
    }

    private static void CorruptCheckpointReadsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocr-listen-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{khong phai json");
            var cues = ReadCheckpoint(path);
            Check(cues is null, "sổ dở hỏng phải báo không có để thử file tiếp, không throw");
        }
        finally { TryDelete(path); }
    }

    private static void ValidCheckpointParses()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocr-listen-valid-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"schema":2,"key":"k","model_revision":"r","device":"cpu","compute_type":"int8",
                 "frontier":10.0,"complete":false,
                 "cues":[
                   {"start":1.0,"end":2.0,"text":"你好","average_log_probability":-0.1,"no_speech_probability":0.1,"words":[]},
                   {"start":5.0,"end":4.0,"text":"ngược giờ","words":[]},
                   {"start":6.0,"end":7.0,"text":"   ","words":[]},
                   {"start":8.0,"end":9.0,"words":[]}
                 ]}
                """);
            var cues = ReadCheckpoint(path);
            Check(cues is not null && cues.Count == 1, "chỉ câu lành được đọc, câu hỏng bị bỏ");
            Check(cues![0].Start == 1.0 && cues[0].End == 2.0 && cues[0].Text == "你好",
                "giờ và chữ câu lành phải giữ nguyên");
        }
        finally { TryDelete(path); }
    }

    private static IReadOnlyList<OcrCue>? ReadCheckpoint(string path)
    {
        var method = typeof(BiliSubApplication).Assembly
            .GetType("BiliSubStudio.Core.Application.BiliSubApplication")
            ?.GetMethod("TryReadOcrListenCheckpoint", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing listen checkpoint reader");
        return (IReadOnlyList<OcrCue>?)method.Invoke(null, [path]);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

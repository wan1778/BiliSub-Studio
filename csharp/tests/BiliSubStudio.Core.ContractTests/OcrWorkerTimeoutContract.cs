using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// TIMEOUT FORENSICS regression: treo 90s phai de lai bang chung (loai op +
// so anh) thay vi cau chung chung. DescribePayload la pure, test qua reflection
// (internal nhu cac contract khac).
internal static class OcrWorkerTimeoutContract
{
    public static Task RunAsync()
    {
        var method = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrWorkerClient")!
            .GetMethod("DescribePayload", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing DescribePayload");
        object? Call(IReadOnlyDictionary<string, object?> payload)
            => method.Invoke(null, [payload]);

        // ValueTuple qua reflection mat ten (Op/Images/Bytes -> Item1/Item2/Item3).
        dynamic Batch() => Call(new Dictionary<string, object?> { ["images_base64"] = new[] { "a", "b", "c", "d", "e", "f", "g", "h" } })!;
        Check(Batch().Item1 == "batch" && Batch().Item2 == 8 && Batch().Item3 == 8, "batch of 8 must describe as batch/8/8B");
        dynamic Probe() => Call(new Dictionary<string, object?> { ["probe_images_base64"] = new[] { "a", "b" } })!;
        Check(Probe().Item1 == "probe" && Probe().Item2 == 2 && Probe().Item3 == 2, "probe of 2 must describe as probe/2/2B");
        dynamic Single() => Call(new Dictionary<string, object?> { ["image_base64"] = "a" })!;
        Check(Single().Item1 == "single" && Single().Item2 == 1 && Single().Item3 == 1, "single must describe as single/1/1B");
        dynamic Empty() => Call(new Dictionary<string, object?>())!;
        Check(Empty().Item1 == "single" && Empty().Item2 == 1 && Empty().Item3 == 0, "unknown payload must fall back to single/1/0B");
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

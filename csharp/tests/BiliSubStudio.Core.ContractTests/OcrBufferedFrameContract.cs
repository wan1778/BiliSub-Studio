using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrBufferedFrameContract
{
    public static Task RunAsync()
    {
        var scanner = typeof(OcrScanner);
        var timingType = scanner.GetNestedType("FrameTiming", BindingFlags.NonPublic)!;
        var frameType = scanner.GetNestedType("OcrBufferedFrame", BindingFlags.NonPublic)!;
        var timing = Activator.CreateInstance(
            timingType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [17L, 0.5, (double?)0.04],
            null)!;
        var jpeg = new byte[] { 0xff, 0xd8, 0x01, 0x02, 0xff, 0xd9 };
        var frame = Activator.CreateInstance(
            frameType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [timing, jpeg],
            null)!;
        var base64 = frameType.GetProperty("Base64")!;
        var first = (string)base64.GetValue(frame)!;
        var second = (string)base64.GetValue(frame)!;

        Check(ReferenceEquals(first, second), "buffered frame encoded the same JPEG more than once");
        Check(Convert.FromBase64String(first).SequenceEqual(jpeg), "buffered frame changed JPEG transport bytes");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

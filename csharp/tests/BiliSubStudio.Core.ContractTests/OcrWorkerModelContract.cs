using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Model contract pin: installer constants == worker ready payload == validator check == validator message.
// Production intended: PP-OCRv6_medium_det + PP-OCRv6_medium_rec (checkpoint schema binds Medium identity).
// Regression: validator từng check Medium nhưng message hardcode "Small", khiến log đảo ngược sự thật.
internal static class OcrWorkerModelContract
{
    public static Task RunAsync()
    {
        var assembly = typeof(OcrScanRequest).Assembly;
        var runtimeType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrRuntime")
            ?? throw new InvalidOperationException("missing OcrRuntime");
        var clientType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrWorkerClient")
            ?? throw new InvalidOperationException("missing OcrWorkerClient");
        var installerType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OcrInstaller");
        var det = (string?)installerType.GetField("DetectionModel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("missing DetectionModel constant");
        var rec = (string?)installerType.GetField("RecognitionModel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("missing RecognitionModel constant");
        Check(det == "PP-OCRv6_medium_det", $"intended detection model drifted: {det}");
        Check(rec == "PP-OCRv6_medium_rec", $"intended recognition model drifted: {rec}");

        var runtimeCtor = runtimeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 5);
        var clientCtor = clientType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 1);
        var validate = clientType.GetMethod("ValidateReady", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing ValidateReady");
        var runtime = runtimeCtor.Invoke(["python", "worker.py", "models", "cuda:0", "GPU"]);
        var client = clientCtor.Invoke([runtime]);

        // 1. Ready payload đúng Medium + device khớp => PASS, không ném.
        try
        {
            validate.Invoke(client, ["{\"type\":\"ready\",\"models\":[\"" + det + "\",\"" + rec + "\"],\"device\":\"GPU:0\"}"]);
        }
        catch (TargetInvocationException error)
        {
            throw new InvalidOperationException("validator rejected the intended Medium ready payload", error.InnerException);
        }

        // 2. Ready payload Small (model cũ) => InvalidDataException, message PHẢI nêu đúng Medium expected.
        try
        {
            validate.Invoke(client, ["{\"type\":\"ready\",\"models\":[\"PP-OCRv6_small_det\",\"PP-OCRv6_small_rec\"],\"device\":\"GPU:0\"}"]);
            throw new InvalidOperationException("validator accepted stale Small models");
        }
        catch (TargetInvocationException error)
        {
            var failure = error.InnerException as InvalidDataException
                ?? throw new InvalidOperationException("validator threw wrong exception for Small models", error.InnerException);
            Check(failure.Message.Contains(det, StringComparison.Ordinal) && failure.Message.Contains(rec, StringComparison.Ordinal),
                $"validator message must name the expected Medium contract, got: {failure.Message}");
            Check(!failure.Message.Contains("Small", StringComparison.Ordinal),
                $"validator message must not mention Small, got: {failure.Message}");
        }

        // 3. Device sai => InvalidDataException (không liên quan model, giữ nguyên).
        try
        {
            validate.Invoke(client, ["{\"type\":\"ready\",\"models\":[\"" + det + "\",\"" + rec + "\"],\"device\":\"CPU\"}"]);
            throw new InvalidOperationException("validator accepted wrong device");
        }
        catch (TargetInvocationException error)
        {
            if (error.InnerException is not InvalidDataException)
                throw new InvalidOperationException("validator threw wrong exception for device mismatch", error.InnerException);
        }
        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

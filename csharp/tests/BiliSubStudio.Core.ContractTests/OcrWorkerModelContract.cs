using System.Reflection;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Model tier contract (2026-09-26): Accurate scans run Medium, Balanced/Fast
// run Small (2.8x cheaper per image, dialogue-equivalent — A/B 2026-09-26).
// Pins: installer constants == worker ready payload == validator check.
// The old single-tier guard (Small always rejected) is intentionally replaced:
// a client constructed for a tier accepts exactly that tier and rejects
// anything else, with the message naming the expected pair.
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
        var smallDet = (string?)installerType.GetField("SmallDetectionModel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("missing SmallDetectionModel constant");
        var smallRec = (string?)installerType.GetField("SmallRecognitionModel", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("missing SmallRecognitionModel constant");
        Check(smallDet == "PP-OCRv6_small_det", $"light detection model drifted: {smallDet}");
        Check(smallRec == "PP-OCRv6_small_rec", $"light recognition model drifted: {smallRec}");
        Check(!string.Equals(det, smallDet, StringComparison.Ordinal), "tiers must differ");

        var runtimeCtor = runtimeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 5);
        var clientCtor = clientType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.GetParameters().Length == 3);
        var validate = clientType.GetMethod("ValidateReady", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing ValidateReady");
        var runtime = runtimeCtor.Invoke(["python", "worker.py", "models", "cuda:0", "GPU"]);
        var mediumClient = clientCtor.Invoke([runtime, det, rec]);
        var smallClient = clientCtor.Invoke([runtime, smallDet, smallRec]);
        var defaultClient = clientCtor.Invoke([runtime, null, null]);

        // 1. Matching tier + device => PASS, no throw.
        TryValidate(validate, mediumClient, Payload(det, rec, "GPU:0"), "validator rejected the intended Medium ready payload");
        TryValidate(validate, smallClient, Payload(smallDet, smallRec, "GPU:0"), "validator rejected the intended Small ready payload");
        TryValidate(validate, defaultClient, Payload(det, rec, "GPU:0"), "default client must accept Medium");

        // 2. Crossed tiers => InvalidDataException naming the expected pair.
        ExpectMismatch(validate, mediumClient, Payload(smallDet, smallRec, "GPU:0"), det, rec);
        ExpectMismatch(validate, smallClient, Payload(det, rec, "GPU:0"), smallDet, smallRec);

        // 3. Wrong device => InvalidDataException (model-independent, unchanged).
        try
        {
            validate.Invoke(mediumClient, [Payload(det, rec, "CPU")]);
            throw new InvalidOperationException("validator accepted wrong device");
        }
        catch (TargetInvocationException error)
        {
            if (error.InnerException is not InvalidDataException)
                throw new InvalidOperationException("validator threw wrong exception for device mismatch", error.InnerException);
        }
        return Task.CompletedTask;
    }

    private static string Payload(string det, string rec, string device) =>
        "{\"type\":\"ready\",\"models\":[\"" + det + "\",\"" + rec + "\"],\"det_model\":\"" + det
        + "\",\"rec_model\":\"" + rec + "\",\"device\":\"" + device + "\"}";

    private static void TryValidate(MethodInfo validate, object client, string payload, string message)
    {
        try
        {
            validate.Invoke(client, [payload]);
        }
        catch (TargetInvocationException error)
        {
            throw new InvalidOperationException(message, error.InnerException);
        }
    }

    private static void ExpectMismatch(MethodInfo validate, object client, string payload, string expectedDet, string expectedRec)
    {
        try
        {
            validate.Invoke(client, [payload]);
            throw new InvalidOperationException("validator accepted mismatched tier models");
        }
        catch (TargetInvocationException error)
        {
            var failure = error.InnerException as InvalidDataException
                ?? throw new InvalidOperationException("validator threw wrong exception for tier mismatch", error.InnerException);
            Check(failure.Message.Contains(expectedDet, StringComparison.Ordinal)
                && failure.Message.Contains(expectedRec, StringComparison.Ordinal),
                $"validator message must name the expected pair, got: {failure.Message}");
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

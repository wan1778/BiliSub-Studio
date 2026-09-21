using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.3 GATE SHADOW regression (OCR Gates v1.3):
// shadow chi ghi chep: bucketize on dinh + format histogram text|blank.
// Bat ky loi shadow nao cung khong duoc anh huong production (ghi o scanner).
internal static class OcrGateShadowContract
{
    public static Task RunAsync()
    {
        Check(OcrGateShadow.Bucketize(0) == 0, "score 0 must bucket 0");
        Check(OcrGateShadow.Bucketize(0.05) == 0, "score 0.05 must bucket 0");
        Check(OcrGateShadow.Bucketize(0.1) == 1, "score 0.1 must bucket 1");
        Check(OcrGateShadow.Bucketize(0.999) == 9, "score 0.999 must bucket 9");
        Check(OcrGateShadow.Bucketize(1.0) == 9, "score 1.0 must clamp 9");
        Check(OcrGateShadow.Bucketize(-0.5) == 0, "negative must clamp 0");
        Check(OcrGateShadow.Bucketize(2.5) == 9, "overflow must clamp 9");
        Check(OcrGateShadow.Bucketize(double.NaN) == 0, "NaN must sink 0, never throw");
        Check(OcrGateShadow.Bucketize(double.PositiveInfinity) == 0, "infinity must sink 0, never throw");

        var text = new long[OcrGateShadow.Buckets];
        var blank = new long[OcrGateShadow.Buckets];
        text[0] = 12; text[9] = 3; blank[5] = 7;
        Check(OcrGateShadow.FormatHistogram(text, blank) == "12,0,0,0,0,0,0,0,0,3|0,0,0,0,0,7,0,0,0,0",
            "histogram must format text|blank in bucket order");
        var bad = false;
        try { OcrGateShadow.FormatHistogram(new long[3], blank); }
        catch (ArgumentException) { bad = true; }
        Check(bad, "wrong bucket count must throw loudly, not silently misalign");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

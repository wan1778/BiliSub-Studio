using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrRuntimePathRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var installer = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OcrInstaller");
        var requiresCompactStorage = installer.GetMethod("RequiresCompactStorage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR runtime path guard");

        var shortPath = (bool)(requiresCompactStorage.Invoke(null, [@"E:\BiliSub\Tools\OCR"])
            ?? throw new InvalidOperationException("OCR runtime path guard returned null"));
        if (shortPath)
            throw new InvalidOperationException("short portable OCR path unexpectedly moved to LocalAppData");

        var deepPath = @"C:\workspace\" + new string('x', 190) + @"\Tools\OCR";
        var compactPath = (bool)(requiresCompactStorage.Invoke(null, [deepPath])
            ?? throw new InvalidOperationException("OCR runtime path guard returned null"));
        if (!compactPath)
            throw new InvalidOperationException("deep portable OCR path can still create an unloadable Paddle DLL path");
    }
}

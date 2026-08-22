using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrTextFilterRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        foreach (var value in new[] { "B站", "VIP会员", "OpenAI模型", "今天看4K视频" })
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(value, out var normalized) || normalized != value)
                throw new InvalidOperationException($"legitimate mixed Chinese OCR text was rejected: {value}");
        }

        foreach (var value in new[] { "你好 ABC", "你好 A N", "你好 テスト", "你好 테스트" })
        {
            if (ChineseSubtitleNormalizer.TryNormalize(value, out _))
                throw new InvalidOperationException($"foreign-script OCR garbage entered Chinese SRT: {value}");
        }

        Console.WriteLine("PASS  Chinese OCR filter keeps mixed tokens and rejects standalone garbage");
    }
}

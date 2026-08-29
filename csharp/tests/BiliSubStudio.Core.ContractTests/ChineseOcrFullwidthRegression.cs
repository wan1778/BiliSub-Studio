using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

internal static class ChineseOcrFullwidthRegression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        static string Normalize(string input)
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(input, out var output))
                throw new InvalidOperationException($"valid Chinese OCR text was rejected: {input}");
            return output;
        }

        static void Reject(string input)
        {
            if (ChineseSubtitleNormalizer.TryNormalize(input, out var output))
                throw new InvalidOperationException($"invalid Chinese OCR text was accepted: {input} -> {output}");
        }

        if (!string.Equals(Normalize("你是ＶＩＰ会员"), "你是VIP会员", StringComparison.Ordinal))
            throw new InvalidOperationException("fullwidth uppercase Latin was not folded inside a Chinese token");
        if (!string.Equals(Normalize("ＯｐｅｎＡＩ模型"), "OpenAI模型", StringComparison.Ordinal))
            throw new InvalidOperationException("mixed fullwidth Latin case was not folded inside a Chinese token");
        if (!string.Equals(Normalize("一 万年前"), "一万年前", StringComparison.Ordinal))
            throw new InvalidOperationException("OCR whitespace split one continuous Chinese subtitle");
        if (!string.Equals(Normalize("OpenAI模型"), "OpenAI模型", StringComparison.Ordinal))
            throw new InvalidOperationException("mixed Latin-Chinese token spacing was changed");
        if (!string.Equals(Normalize("第 8 集"), "第 8 集", StringComparison.Ordinal))
            throw new InvalidOperationException("meaningful digit spacing was changed");

        Reject("你好 ＶＩＰ");
        Reject("ＶＩＰ");
        Reject("１２３");

        if (!string.Equals(Normalize("你有１２３人"), "你有１２３人", StringComparison.Ordinal))
            throw new InvalidOperationException("fullwidth digits were changed by Latin-only normalization");
        if (!string.Equals(Normalize("你好！"), "你好！", StringComparison.Ordinal))
            throw new InvalidOperationException("Chinese fullwidth punctuation was changed by Latin-only normalization");
    }
}

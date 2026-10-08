using System.Text;

namespace BiliSubStudio.Core.Editor;

internal static class EditorVoiceCuePlanner
{
    private const double TouchToleranceSeconds = .05;
    // A stable caption can remain on screen for several seconds before one bad
    // OCR frame flashes at its tail. Cue count and text similarity are the real
    // safety bounds; eight seconds still prevents unrelated scene-wide merges.
    private const double FlickerMaximumSeconds = 8;
    private const double FlickerCueMaximumSeconds = .25;
    private const int FlickerMaximumCues = 8;

    public static IReadOnlyList<EditorSubtitleCue> Build(IReadOnlyList<EditorSubtitleCue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count < 2) return source.ToArray();
        var result = new List<EditorSubtitleCue>(source.Count);
        for (var first = 0; first < source.Count;)
        {
            var last = first;
            var maximumEnd = source[first].End;
            while (last + 1 < source.Count && last - first + 1 < FlickerMaximumCues)
            {
                var candidate = source[last + 1];
                if (candidate.Start > maximumEnd + TouchToleranceSeconds) break;
                maximumEnd = Math.Max(maximumEnd, candidate.End);
                if (maximumEnd - source[first].Start > FlickerMaximumSeconds) break;
                last++;
            }

            var collapseEnd = FindFlickerEnd(source, first, last);
            if (collapseEnd <= first)
            {
                result.Add(source[first]);
                first++;
                continue;
            }

            var cluster = source.Skip(first).Take(collapseEnd - first + 1).ToArray();
            var representative = cluster
                .OrderByDescending(cue => cue.End - cue.Start)
                .ThenBy(cue => Normalize(cue.VietnameseText).Length)
                .ThenBy(cue => cue.Start)
                .First();
            result.Add(representative with
            {
                Start = cluster.Min(cue => cue.Start),
                End = cluster.Max(cue => cue.End),
            });
            first = collapseEnd + 1;
        }
        return result;
    }

    public static IReadOnlyList<EditorSubtitleCue> RemoveUnspeakable(IReadOnlyList<EditorSubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return cues.Where(cue => VietnameseTtsTextNormalizer.HasSpeakableUnits(cue.VietnameseText)).ToArray();
    }

    // Vá cửa sổ voice trước khi đọc (23/09): mảnh timecode quá hẹp (<0.15s)
    // thì không giọng nào đọc vừa -> worker giữ im lặng oan. Vá bằng 2 cách,
    // đều KHÔNG đụng chữ, KHÔNG lấn câu khác, KHÔNG đổi thứ tự; 07/10 thêm:
    // KHÔNG lùi Start sớm hơn giờ sub (chỉ nới End phải):
    // 1. Hấp thụ mảnh trùng Y HỆT: mảnh hẹp kề (hở 0..1s, không chồng) với câu
    //    chữ y hệt từng chữ thì gộp vào câu lành (giữ Id + chữ câu lành, nới
    //    khung phủ cả mảnh). Chỉ gộp khi một bên hẹp + một bên lành nên thoại
    //    lặp lành mạnh không bao giờ bị gộp oan.
    // 2. Nới mảnh hẹp còn lại vào khoảng im PHẢI (tối đa 2s, chừa 0.05s
    //    với câu sau). Mảnh chạm khít 2 bên thì không có chỗ nới -> giữ nguyên
    //    cho worker xử lý (im lặng + cờ review, hiếm).
    public static IReadOnlyList<EditorSubtitleCue> RepairVoiceWindows(IReadOnlyList<EditorSubtitleCue> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const double MinVoiceWindowSeconds = .15;
        const double MaxAbsorbGapSeconds = 1.0;
        const double MaxExtendSeconds = 2.0;
        const double NeighborClearanceSeconds = .05;
        if (source.Count < 2) return source.ToArray();
        static bool Sliver(EditorSubtitleCue cue) => cue.End - cue.Start < MinVoiceWindowSeconds;
        static bool Healthy(EditorSubtitleCue cue) => cue.End - cue.Start >= MinVoiceWindowSeconds;
        static bool Identical(string left, string right) =>
            !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal);

        var absorbed = new List<EditorSubtitleCue>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var cue = source[index];
            if (Sliver(cue))
            {
                if (absorbed.Count > 0)
                {
                    var prev = absorbed[^1];
                    var gap = cue.Start - prev.End;
                    if (Healthy(prev) && gap >= 0 && gap <= MaxAbsorbGapSeconds
                        && Identical(cue.VietnameseText, prev.VietnameseText))
                    {
                        absorbed[^1] = prev with { End = Math.Max(prev.End, cue.End) };
                        continue;
                    }
                }
                if (index + 1 < source.Count)
                {
                    var next = source[index + 1];
                    var gap = next.Start - cue.End;
                    if (Healthy(next) && gap >= 0 && gap <= MaxAbsorbGapSeconds
                        && Identical(cue.VietnameseText, next.VietnameseText))
                    {
                        absorbed.Add(next with { Start = Math.Min(next.Start, cue.Start) });
                        index++;
                        continue;
                    }
                }
            }
            absorbed.Add(cue);
        }

        var repaired = new List<EditorSubtitleCue>(absorbed.Count);
        for (var index = 0; index < absorbed.Count; index++)
        {
            var cue = absorbed[index];
            if (!Sliver(cue) || string.IsNullOrWhiteSpace(cue.VietnameseText))
            {
                repaired.Add(cue);
                continue;
            }
            var start = cue.Start;
            var end = cue.End;
            if (index + 1 < absorbed.Count)
            {
                var next = absorbed[index + 1];
                // Chỉ nới phải khi hở thật (chừa 0.05s với câu sau); chạm khít thì thôi.
                // Không nới lùi Start (07/10): giờ sub là tuyệt đối, voice không đọc sớm.
                if (next.Start - NeighborClearanceSeconds > end)
                    end = Math.Min(next.Start - NeighborClearanceSeconds, end + MaxExtendSeconds);
            }
            repaired.Add(end == cue.End ? cue : cue with { End = end });
        }
        return repaired;
    }

    // Nới cue CHẬT bằng khe im kề PHẢI (01/10, luật L-bệnh; 07/10 user bắt
    // buộc sub hiện là voice đọc luôn nên CẤM mượn khe trái: Start không bao
    // giờ lùi sớm hơn giờ sub, chỉ End nới phải lấy chỗ đọc. Cue cần pace
    // vượt nhịp tự nhiên (need > 1) được mượn thời gian từ khoảng hở PHẢI để
    // hạ need — trị gốc thay vì ép voice. KHÔNG trần cố định: mượn đúng số
    // thiếu (required - window), lấy tới đâu khe phải cho tới đó (chừa 0.05s
    // với cue sau), không bao giờ lấn/vượt cue khác, không đụng chữ, không
    // đổi thứ tự, Start giữ nguyên tuyệt đối. Chỉ VieNeu (cần bảng pace
    // preset); Piper giữ nguyên. Chạy sau RepairVoiceWindows, trước MapToCues
    // để manifest/validator thấy khung đã nới là ground truth. Khe phải không
    // đủ thì worker atempo ≤1.5x (micro 2.5x) gánh phần còn lại (vẫn review).
    // Không phụ thuộc thứ tự duyệt (biên nào cũng tính từ cue gốc).
    public static IReadOnlyList<EditorSubtitleCue> WidenTightWindows(
        IReadOnlyList<EditorSubtitleCue> source, string voice, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count == 0 || !TtsVoiceCatalog.IsVieneuVoice(voice)) return source.ToArray();
        var pace = TtsVoiceCatalog.PaceWordsPerSecond(voice);
        if (pace <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return source.ToArray();
        const double NeighborClearanceSeconds = .05;
        var widened = new List<EditorSubtitleCue>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var cue = source[index];
            var window = cue.End - cue.Start;
            var words = CountSpeakableWords(cue.VietnameseText);
            if (window <= 0 || words <= 0)
            {
                widened.Add(cue);
                continue;
            }
            // Đo GPU 02-03/10 (Ngọc Huyền 4.36 chữ/s): overhead cue ngắn theo
            // số chữ, vì mào đầu prosody + đuôi dấu câu rơi vào chữ cuối:
            // 1 chữ "Dung" +0.19s; 2 chữ "Đừng hoảng." +0.52s, "Cẩn thận."
            // +0.66s, "Không động." +0.67s ("Vào đi." bloat +0.9s); 3 chữ
            // "Đừng cựa quậy." +0.35s. Không tier này estimator need 0.999
            // (vừa khít oan) rồi worker chết ở need thực 1.7-2.1x (vụ #2837,
            // #6470). Khe không đủ thì worker atempo/micro-band + review gánh
            // phần còn lại (trần giữ nguyên, không nới).
            var overhead = words <= 1 ? .27 : words == 2 ? .70 : words <= 4 ? .40 : 0;
            var required = words / pace + overhead;
            var need = required / window;
            if (need <= 1)
            {
                widened.Add(cue);
                continue;
            }
            var shortfall = required - window;
            // Chỉ mượn khe PHẢI (07/10): Start giữ nguyên giờ sub để voice
            // không bao giờ đọc sớm; End nới phải lấy chỗ đọc (chừa 0.05s với
            // cue sau). rightEdge tính từ cue gốc (Start cue sau không bao giờ
            // bị nới sớm nữa) nên thứ tự duyệt không ảnh hưởng kết quả.
            var rightEdge = index == source.Count - 1
                ? durationSeconds
                : source[index + 1].Start - NeighborClearanceSeconds;
            var rightAvail = Math.Max(0, rightEdge - cue.End);
            if (rightAvail <= 0)
            {
                widened.Add(cue);
                continue;
            }
            var take = Math.Min(shortfall, rightAvail);
            var end = Math.Min(durationSeconds, cue.End + take);
            widened.Add(end == cue.End ? cue : cue with { End = end });
        }
        return widened;
    }

    private static int CountSpeakableWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var character in token)
            {
                if (char.IsLetterOrDigit(character))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static int FindFlickerEnd(IReadOnlyList<EditorSubtitleCue> cues, int first, int last)
    {
        if (last <= first) return first;
        for (var right = last; right > first; right--)
        {
            if (cues.Skip(first).Take(right - first + 1).All(cue => cue.End - cue.Start > FlickerCueMaximumSeconds))
                continue;
            for (var index = first; index <= right; index++)
            {
                for (var previous = Math.Max(first, index - 2); previous < index; previous++)
                    if (LooksLikeSameCaption(cues[previous].VietnameseText, cues[index].VietnameseText))
                        return right;
            }
        }
        return first;
    }

    private static bool LooksLikeSameCaption(string left, string right)
    {
        var first = Normalize(left);
        var second = Normalize(right);
        if (first.Length < 2 || second.Length < 2) return false;
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        if (shorter.Length >= 6 && shorter.Length * 100 >= longer.Length * 65
            && longer.Contains(shorter, StringComparison.Ordinal))
            return true;

        var firstTokens = Tokens(left);
        var secondTokens = Tokens(right);
        var minimum = Math.Min(firstTokens.Count, secondTokens.Count);
        return minimum >= 3 && firstTokens.Intersect(secondTokens).Count() * 100 >= minimum * 60;
    }

    private static HashSet<string> Tokens(string value) => value
        .Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Normalize)
        .Where(token => token.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }
}

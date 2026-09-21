using System.Diagnostics;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Tools;

namespace BiliSubStudio.Core.Ocr;

// E.5 — VAD 3 co (OCR Gates v1.3), DOT NAY CHI DUNG-NHIN (shadow).
//
// Co che: giai ma audio 1 lan/scan (16kHz mono) -> RMS moi 0.2s -> co:
// - NO_SUB khi yen lang (nang luong thap, co hysteresis): de xuat bo qua.
// - SUSPICIOUS khi co tieng (khong phan biet noi/nhac bang nang luong re:
//   trung thuc nhan SUSPICIOUS, khong doan). HAS_SUB chi tu whisper (E.5b).
// Quy tac hoi dong (E.4+): co VAD khong bao gio du mot minh de skip.
// Dot nay: ghi vote + outcome vao histogram 2x2, khong skip, khong fail lane.
public enum VadFlag
{
    NoSub = 0,
    Suspicious = 1,
    HasSub = 2,
}

public sealed record VadTimeline(double FrameSeconds, VadFlag[] Flags)
{
    public VadFlag At(double mediaSeconds)
    {
        if (Flags.Length == 0 || FrameSeconds <= 0) return VadFlag.Suspicious;
        var index = (int)Math.Floor(mediaSeconds / FrameSeconds);
        if (index < 0) return Flags[0];
        if (index >= Flags.Length) return Flags[^1];
        return Flags[index];
    }
}

public static class OcrVadProbe
{
    public const double FrameSeconds = 0.2;
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(5);

    // Thuan, test truc tiep: RMS -> co. Nguong 3 nhanh (khong doan khi thieu moc):
    // - max <= 0.015: ca doan yen -> NoSub het.
    // - min > 0.05: ca doan ro rang co nang luong -> Suspicious het (khong co moc
    //   yen de so, khong dam bao NoSub).
    // - con lai (lan lon): tach o min + 25% bien do.
    // Hysteresis +-0.7s (v1.3): doan nghi ngan giua 2 doan tieng khong lat co.
    public static VadTimeline ComputeFlags(IReadOnlyList<double> rms)
    {
        if (rms.Count == 0) return new VadTimeline(FrameSeconds, []);
        var finite = rms.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return new VadTimeline(FrameSeconds, Enumerable.Repeat(VadFlag.Suspicious, rms.Count).ToArray());
        var min = finite.Min();
        var max = finite.Max();
        double threshold;
        if (max <= 0.015) threshold = double.PositiveInfinity;
        else if (min > 0.05) threshold = 0;
        else threshold = Math.Max(0.015, min + 0.25 * (max - min));
        var flags = new VadFlag[rms.Count];
        for (var i = 0; i < rms.Count; i++)
            flags[i] = rms[i] < threshold ? VadFlag.NoSub : VadFlag.Suspicious;
        // Hysteresis: khe NoSub <= 0.7s kep giua Suspicious thi giu Suspicious.
        var bridge = Math.Max(1, (int)Math.Round(0.7 / FrameSeconds));
        var i0 = 0;
        while (i0 < flags.Length)
        {
            if (flags[i0] != VadFlag.NoSub) { i0++; continue; }
            var i1 = i0;
            while (i1 < flags.Length && flags[i1] == VadFlag.NoSub) i1++;
            var leftVoice = i0 > 0 && flags[i0 - 1] == VadFlag.Suspicious;
            var rightVoice = i1 < flags.Length && flags[i1] == VadFlag.Suspicious;
            if (leftVoice && rightVoice && i1 - i0 <= bridge)
                for (var k = i0; k < i1; k++) flags[k] = VadFlag.Suspicious;
            i0 = i1;
        }
        return new VadTimeline(FrameSeconds, flags);
    }

    // Giai ma audio thanh RMS 0.2s. Tra null khi khong co tieng / loi (VAD vang
    // mat am tham, khong fail scan). Chi goi 1 lan/scan, share moi lane.
    // Dung CaptureBytesAsync (binary) + sample rate thap 2kHz cho energy: video
    // 10h ~144MB transient, video 2h ~29MB. Khong dung RunAsync (string stdout
    // lam hong binary) va khong ghi file tam.
    public static async Task<VadTimeline?> DecodeAsync(
        ToolManager tools,
        ProcessRunner processes,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var ffmpeg = await tools.EnsureFfmpegAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DecodeTimeout);
            var bytes = await processes.CaptureBytesAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin",
                 "-i", Path.GetFullPath(source.Trim()),
                 "-map", "0:a:0", "-ac", "1", "-ar", "2000",
                 "-f", "s16le", "-"],
                timeout.Token);
            return ComputeFlags(RmsFrames(bytes, 2000));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static IReadOnlyList<double> RmsFrames(byte[] pcm, int rate = 2000)
    {
        var perFrame = (int)(rate * FrameSeconds);
        var total = pcm.Length / 2;
        var frames = new List<double>(total / perFrame + 1);
        for (var start = 0; start + perFrame <= total; start += perFrame)
        {
            double sum = 0;
            for (var i = 0; i < perFrame; i++)
            {
                var sample = (short)(pcm[(start + i) * 2] | (pcm[(start + i) * 2 + 1] << 8));
                var normalized = sample / 32768.0;
                sum += normalized * normalized;
            }
            frames.Add(Math.Sqrt(sum / perFrame));
        }
        return frames;
    }
}

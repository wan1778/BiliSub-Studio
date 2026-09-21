using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.5 VAD + ROI SHADOW regression (OCR Gates v1.3):
// VAD chi de xuat (khong bao gio lenh skip mot minh); ROI chi hoc vung lap.
internal static class OcrGateVadRoiContract
{
    public static Task RunAsync()
    {
        // VAD: yen lang -> NoSub, co tieng -> Suspicious (khong doan speech/music).
        var quiet = OcrVadProbe.ComputeFlags([0.001, 0.002, 0.001, 0.002, 0.001]);
        Check(quiet.Flags.All(flag => flag == VadFlag.NoSub), "silence must vote NoSub");
        var loud = OcrVadProbe.ComputeFlags([0.2, 0.25, 0.18, 0.3, 0.22]);
        Check(loud.Flags.All(flag => flag == VadFlag.Suspicious), "voice must vote Suspicious, never HasSub from energy");
        // Hysteresis: khe yen 0.6s kep giua tieng thi giu Suspicious.
        var bridged = OcrVadProbe.ComputeFlags([0.3, 0.3, 0.001, 0.001, 0.001, 0.3, 0.3]);
        Check(bridged.Flags.All(flag => flag == VadFlag.Suspicious), "sub-0.7s silence gap between voice must stay Suspicious");
        // Khe yen dai thi tach that.
        var split = OcrVadProbe.ComputeFlags([0.3, 0.3, 0.001, 0.001, 0.001, 0.001, 0.001, 0.001, 0.3, 0.3]);
        Check(split.Flags[2] == VadFlag.NoSub && split.Flags[5] == VadFlag.NoSub, "long silence must stay NoSub");
        // At(): trong/am bien/last.
        Check(loud.At(0.0) == VadFlag.Suspicious, "At(0) must return first flag");
        Check(loud.At(100000) == VadFlag.Suspicious, "At(beyond end) must clamp last flag");
        Check(new VadTimeline(0.2, []).At(5) == VadFlag.Suspicious, "empty timeline must abstain to Suspicious");
        // RmsFrames: im lang 0.2s -> ~0; so am 0.2s -> >0.
        var silent = OcrVadProbe.RmsFrames(new byte[800], 2000);
        Check(silent.Count == 1 && silent[0] < 1e-9, "digital silence must measure ~0");
        var tone = new byte[800];
        for (var i = 0; i < 400; i++)
        {
            var sample = (short)(10000 * System.Math.Sin(i * 0.5));
            tone[i * 2] = (byte)(sample & 0xFF);
            tone[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        var toneRms = OcrVadProbe.RmsFrames(tone, 2000);
        Check(toneRms.Count == 1 && toneRms[0] > 0.1, "tone must measure energy");

        // ROI: chu lap 5 sample lien tiep cung box -> zone; sub doi thi khong.
        var zones = new OcrRoiStaticZones();
        var wm = new OcrLine("bilibili", 0.99, [1150, 650, 1260, 700]);
        var sub1 = new OcrLine("你好吗", 0.99, [400, 600, 600, 650]);
        var sub2 = new OcrLine("我很好", 0.99, [410, 600, 590, 650]);
        for (var i = 0; i < 4; i++)
        {
            var filtered = zones.Observe([wm, i % 2 == 0 ? sub1 : sub2]);
            Check(filtered == 0, "no zone before 5 repeats, nothing would-filter");
        }
        Check(zones.Zones.Count == 0, "4 repeats must not learn yet");
        var filtered5 = zones.Observe([wm, sub1]);
        Check(zones.Zones.Count == 1 && zones.Zones[0].Text == "bilibili", "5th repeat must learn watermark zone");
        Check(filtered5 == 1, "watermark line inside learned zone must would-filter");
        var filteredSub = zones.Observe([sub2]);
        Check(filteredSub == 0, "dialogue outside zone must not would-filter");
        // Gian doan streak thi reset, khong hoc nham.
        var zones2 = new OcrRoiStaticZones();
        for (var i = 0; i < 4; i++) zones2.Observe([wm]);
        zones2.Observe([sub1]);
        for (var i = 0; i < 4; i++) zones2.Observe([wm]);
        Check(zones2.Zones.Count == 0, "broken streak must reset, not learn");

        // Hermetic bytes->flags (khong can ffmpeg): 2s tone / 2s im / 2s tone @2kHz
        // -> 30 khung 0.2s: Susp x10, NoSub x10, Susp x10. Chaine day du Rms+Compute.
        var pcm = new byte[12000 * 2];
        for (var s = 0; s < 12000; s++)
        {
            short sample = 0;
            if (s < 4000 || s >= 8000)
                sample = (short)(10000 * System.Math.Sin(s * 0.5));
            pcm[s * 2] = (byte)(sample & 0xFF);
            pcm[s * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        var timeline = OcrVadProbe.ComputeFlags(OcrVadProbe.RmsFrames(pcm, 2000));
        Check(timeline.Flags.Length == 30, $"6s audio must yield 30 frames (got {timeline.Flags.Length})");
        Check(timeline.Flags.Take(10).All(flag => flag == VadFlag.Suspicious), "tone segment must vote Suspicious");
        Check(timeline.Flags.Skip(10).Take(10).All(flag => flag == VadFlag.NoSub), "silence segment must vote NoSub");
        Check(timeline.Flags.Skip(20).All(flag => flag == VadFlag.Suspicious), "second tone must vote Suspicious");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

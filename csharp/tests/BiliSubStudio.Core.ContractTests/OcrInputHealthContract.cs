using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.0 INPUT HEALTH PRE-FLIGHT regression (OCR Gates v1.3):
// mo video phai check truoc, hong thi fail to (giu checkpoint), lech thi warn
// chu khong chay tiep lang le. Chi test parser thuan (khong can FFmpeg that).
internal static class OcrInputHealthContract
{
    public static Task RunAsync()
    {
        // 1. Video sach: co hinh + co tieng, CFR, timebase 1/90000, duration khop.
        var healthy = OcrInputHealth.ParseProbeOutput(
            "codec_type=video\nwidth=1280\nheight=720\navg_frame_rate=25/1\nr_frame_rate=25/1\ntime_base=1/90000\nduration=300.000000\n"
            + "codec_type=audio\n",
            "duration=300.040000\n");
        var healthyReport = OcrInputHealth.BuildReport(healthy, 60, true);
        Check(healthy.HasVideo && healthy.HasAudio, "healthy must detect video+audio");
        Check(healthyReport.Passed && healthyReport.Warnings.Count == 0,
            $"healthy must pass silent (failures={healthyReport.Failures.Count} warnings={healthyReport.Warnings.Count})");

        // 2. Video cam: khong audio -> WARN (VAD nghi), khong FAIL.
        var noAudio = OcrInputHealth.ParseProbeOutput(
            "codec_type=video\nwidth=1280\nheight=720\navg_frame_rate=25/1\nr_frame_rate=25/1\ntime_base=1/90000\nduration=300.000000\n",
            "duration=300.000000\n");
        var noAudioReport = OcrInputHealth.BuildReport(noAudio, 60, true);
        Check(!noAudio.HasAudio, "mute video must report no audio");
        Check(noAudioReport.Passed, "mute video must not fail");
        Check(noAudioReport.Warnings.Count == 1 && noAudioReport.Warnings[0].Contains("VAD nghi"),
            "mute video must warn VAD abstains");

        // 3. VFR + timebase tho + lech duration: 3 WARN rieng biet.
        var weird = OcrInputHealth.ParseProbeOutput(
            "codec_type=video\nwidth=1920\nheight=1080\navg_frame_rate=30000/1001\nr_frame_rate=60/1\ntime_base=1/10\nduration=295.000000\n"
            + "codec_type=audio\n",
            "duration=300.500000\n");
        var weirdReport = OcrInputHealth.BuildReport(weird, 60, true);
        Check(weirdReport.Passed, "weird-but-decodable must not fail");
        Check(weirdReport.Warnings.Count == 3,
            $"VFR + coarse-timebase + duration drift must warn thrice (got {weirdReport.Warnings.Count})");

        // 4. PTS di lui -> WARN (tracker tu chiu), khong FAIL.
        Check(!OcrInputHealth.CheckPtsMonotonic([0.0, 0.04, 0.08, 0.04, 0.12]),
            "backward PTS must be detected");
        Check(OcrInputHealth.CheckPtsMonotonic([0.0, 0.04, 0.04, 0.08]),
            "equal PTS (duplicate timestamp) must be allowed");
        var dipReport = OcrInputHealth.BuildReport(healthy, 60, false);
        Check(dipReport.Passed && dipReport.Warnings.Count == 1,
            "PTS dip must warn once, not fail");

        // 5. Khong kich thuoc / khong frame -> FAIL (dung scan, giu checkpoint).
        var noSize = OcrInputHealth.ParseProbeOutput("codec_type=video\n", "");
        var noSizeReport = OcrInputHealth.BuildReport(noSize, 60, true);
        Check(!noSizeReport.Passed, "missing dimensions must fail");
        var noFramesReport = OcrInputHealth.BuildReport(healthy, 0, true);
        Check(!noFramesReport.Passed, "zero decoded frames must fail");

        // 6. Kep duoi stream (P0 container-padding p02): container thua stream thi kep
        // theo stream; stream khong ro hoac duoi da trong thi giu nguyen.
        Check(OcrInputHealth.ClampStreamEnd(6726.913741, 6726.666625) == 6726.666625,
            "container beyond stream must clamp to stream end");
        Check(OcrInputHealth.ClampStreamEnd(300.0, 0) == 300.0,
            "unknown stream end must keep requested end");
        Check(OcrInputHealth.ClampStreamEnd(300.0, 300.04) == 300.0,
            "requested end within stream must keep requested end");
        Check(OcrInputHealth.ClampStreamEnd(300.0, 299.99) == 300.0,
            "sub-deadband drift must not rewrite checkpoints");

        return Task.CompletedTask;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

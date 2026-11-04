using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// E.4 COUNCIL + CANARY regression (OCR Gates v1.3):
// B-reuse only carries over on near-dup when a previous result exists; canary dual-trigger;
// a hit is a mismatch (Detected differs, or same detected with different text).
internal static class OcrGateCouncilContract
{
    public static Task RunAsync()
    {
        // ShouldReuse: requires previous + finite + within eps.
        Check(OcrGateShadow.ShouldReuse(0.0, true), "exact-dup with previous must reuse");
        Check(OcrGateShadow.ShouldReuse(0.02, true), "eps boundary must reuse");
        Check(!OcrGateShadow.ShouldReuse(0.020001, true), "above eps must not reuse");
        Check(!OcrGateShadow.ShouldReuse(0.0, false), "no previous must not reuse");
        Check(!OcrGateShadow.ShouldReuse(null, true), "missing band score (old worker) must not reuse");
        Check(!OcrGateShadow.ShouldReuse(double.NaN, true), "NaN must not reuse");

        // ShouldTriggerCanary: 50 images OR 60s.
        Check(!OcrGateShadow.ShouldTriggerCanary(49, 59.9), "below both thresholds must not trigger");
        Check(OcrGateShadow.ShouldTriggerCanary(50, 0), "50th image must trigger");
        Check(OcrGateShadow.ShouldTriggerCanary(0, 60), "60s silence must trigger");
        Check(OcrGateShadow.ShouldTriggerCanary(1000, 1000), "both over must trigger");

        // IsCanaryHit: mismatch is a hit, blank-blank is not a hit.
        Check(OcrGateShadow.IsCanaryHit(false, "", true, "abc"), "carried blank but got text must hit");
        Check(OcrGateShadow.IsCanaryHit(true, "abc", false, ""), "carried text but got blank must hit");
        Check(OcrGateShadow.IsCanaryHit(true, "abc", true, "abd"), "same-detected different text must hit");
        Check(!OcrGateShadow.IsCanaryHit(false, "", false, ""), "blank-blank must not hit");
        Check(!OcrGateShadow.IsCanaryHit(true, "abc", true, "abc"), "identical must not hit");

        // D.1 flag histogram: flag-free means free (recover=false + active null/empty).
        Check(OcrGateShadow.FlagBucket(false, null) == 0, "no flags must bucket free");
        Check(OcrGateShadow.FlagBucket(false, "") == 0, "empty active text must bucket free");
        Check(OcrGateShadow.FlagBucket(true, null) == 1, "recover only must bucket recover");
        Check(OcrGateShadow.FlagBucket(false, "三") == 2, "active only must bucket active");
        Check(OcrGateShadow.FlagBucket(true, "三") == 3, "both flags must bucket both");

        // Fail-closed 16/9: VAD-silence is unreliable system-wide (8 canary hits overnight)
        // so the council is off by default; the pure functions stay for logic tests, audit stays.
        Check(OcrGateShadow.CouncilEnabledDefault == false, "council phải tắt fail-closed");

        // Council v1 SKIP (E.5 enforce): silence + still + blank anchor + within cap. Missing any one means read.
        Check(OcrGateShadow.ShouldCouncilSkip(true, 0.01, true, 0), "unanimous blank run must skip");
        Check(OcrGateShadow.ShouldCouncilSkip(true, 0.02, true, 4), "eps/cap boundary must skip");
        Check(!OcrGateShadow.ShouldCouncilSkip(false, 0.01, true, 0), "voice must never skip");
        Check(!OcrGateShadow.ShouldCouncilSkip(true, 0.020001, true, 0), "above eps must read");
        Check(!OcrGateShadow.ShouldCouncilSkip(true, null, true, 0), "missing band score must read");
        Check(!OcrGateShadow.ShouldCouncilSkip(true, double.NaN, true, 0), "NaN band must read");
        Check(!OcrGateShadow.ShouldCouncilSkip(true, 0.01, false, 0), "unanchored chain must read");
        Check(!OcrGateShadow.ShouldCouncilSkip(true, 0.01, true, 5), "cap reached must force a real read");

        // 18/09: skip samples (false blanks, not a real read) must never reach the tracker
        // (a fake ObserveEmpty closes the open cue unfairly) and must never become a
        // scene-change anchor (a false blank hides the real text edge). The lane loop has no
        // harness, so pin the call-site structure in OcrScanner (precedent:
        // TranslationQualityPolicyContract).
        SkipNeverFeedsTracker();

        // ROI learning gate: only learn when the ROI covers the upper half (watermarks live there).
        // For a bottom subtitle strip (current video Y=0.82) any repeated text is just repeated dialogue.
        Check(OcrRoiStaticZones.LearningEnabledFor(new OcrRegion(0, 0, 1, 1)), "full-frame ROI must learn");
        Check(OcrRoiStaticZones.LearningEnabledFor(new OcrRegion(0, 0.49, 1, 0.51)), "ROI above middle must learn");
        Check(!OcrRoiStaticZones.LearningEnabledFor(new OcrRegion(0.22, 0.82, 0.54, 0.15)), "bottom-strip ROI must not learn");
        Check(!OcrRoiStaticZones.LearningEnabledFor(new OcrRegion(0, 0.5, 1, 0.5)), "boundary Y=0.5 must not learn");

        return Task.CompletedTask;
    }

    private static void SkipNeverFeedsTracker()
    {
        // Resolve upward from the DLL directory to the repo root (the test CWD
        // is unstable, so Directory.GetCurrentDirectory() would wrongly silent-skip
        // — this test forbids silent, a missing file is a fail).
        string? scannerPath = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "csharp", "src", "BiliSubStudio.Core", "Ocr", "OcrScanner.cs");
            if (File.Exists(candidate)) { scannerPath = candidate; break; }
            dir = dir.Parent;
        }
        Check(scannerPath is not null, "không tìm thấy OcrScanner.cs từ thư mục test");
        var scanner = File.ReadAllText(scannerPath!);
        Check(scanner.Contains("skippedSample = true", StringComparison.Ordinal),
            "nhánh skip phải đánh dấu mẫu bỏ qua");
        Check(scanner.Contains("if (!skippedSample)", StringComparison.Ordinal),
            "tracker/anchor phải né mẫu skip");
        Check(scanner.Contains("skippedSample = false", StringComparison.Ordinal),
            "đọc thật phục hồi (transition re-read) phải gỡ cờ skip");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

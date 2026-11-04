namespace BiliSubStudio.Core.Ocr;

// E.3 — B/C SHADOW MODE (OCR Gates v1.3).
//
// Iron rule: shadow only RECORDS, never skips/decides.
// Every function here is pure so contract tests call them directly. Exact histograms
// live in OcrScanTelemetryCounters (scanner); calibration data is offline.
public static class OcrGateShadow
{
    public const int Buckets = 10;

    // Score 0..1 -> bucket 0..9. Non-finite (NaN/inf, the worker blocked something but the
    // low-level value is untrusted) sinks to 0, never throws because shadow.
    public static int Bucketize(double score)
    {
        if (!double.IsFinite(score) || score <= 0) return 0;
        if (score >= 1) return Buckets - 1;
        return Math.Min(Buckets - 1, (int)(score * Buckets));
    }

    // "t0,t1,...,t9|b0,b1,...,b9": text first, blank after, for offline reading.
    public static string FormatHistogram(long[] text, long[] blank)
    {
        if (text.Length != Buckets || blank.Length != Buckets)
            throw new ArgumentException($"Shadow histogram phai co {Buckets} ngan.", nameof(text));
        return string.Join(",", text) + "|" + string.Join(",", blank);
    }

    // E.4a B-REUSE (enforced): near-dup carries the previous result forward, does NOT call Paddle.
    // Default eps 0.02 (offline worker metric: <=0.05 was correct 20/20; take the safe margin).
    // Two-member council: B-reuse + canary audit (E.4b). C-edge joins at E.5.
    // 2026-09-13: canary caught ~15 hits/scan in production (even text->text: Paddle changes
    // characters between two near-dup frames like 凉/涼) -> OFF by default until re-tuned.
    // Machinery (shadow/canary/chain-cap) is kept so it takes one line to re-enable.
    public const bool ReuseEnabledDefault = false;
    public const double ReuseEpsDefault = 0.02;
    public static bool ShouldReuse(double? bandScore, bool hasPrevious, double eps = ReuseEpsDefault)
        => hasPrevious && bandScore.HasValue && double.IsFinite(bandScore.Value) && bandScore.Value <= eps;

    // Council v1 SKIP (E.5 enforce): skip calling Paddle on the sample IF AND ONLY IF all 3:
    // (1) VAD silent (NoSub) + (2) B standing still (band<=eps) + (3) the blank-full-read anchor
    // chain is still valid (prevBlankAnchored: only becomes true via a full-read blank; skip keeps it,
    // a full-read with text/audit/resume drops it) + (4) not past the consecutive cap.
    // C (text-presence) currently ABSTAINS — always treated as SUSPICIOUS, does NOT vote: tier-1 edge-density
    // overlaps text/blank in bucket 0 (production shadow_c: text 17553 vs blank 12004
    // in the same bucket) so it is not yet vote-worthy; tier-2 CC/stroke comes later, then add a 4th parameter.
    // B-reuse does NOT use this path (ReuseEnabledDefault=false, fully retired): council is the only
    // skip mechanism, with anchoring + cap + canary replacing carry.
    // 2026-09-16: OFF council skip fail-closed after 8 overnight canary hits, all
    // ear:NoSub (VAD reported silence but auditing read text out). The VAD leg is systematically wrong, so every
    // silent-still skip is suspected of eating real text; the canary only spot-checks 抽查 samples, it cannot catch all.
    // Measured saving ~0.1% so switching it off costs nothing. Re-enable only once VAD is fixed +
    // parity re-run + pinned tests. ShouldCouncilSkip is kept so the logic stays tested.
    // 2026-09-17: conditionally re-ENABLED — a timeline from Whisper EAR (distinguishes
    // real speech/silence, not energy) is exactly "VAD fixed". Only enabled when
    // the timeline has the HasSub flag (passed by OcrScanner); energy/legacy sidecar timelines
    // stay OFF. Multi-gate conservatism + cap + canary audit + kill-switch on canary-hit
    // are unchanged — a wrong skip is caught immediately by the canary in the log.
    public const bool CouncilEnabledDefault = false;
    public const long CouncilMaxSkips = 5;
    public static bool ShouldCouncilSkip(
        bool vadSilent,
        double? bandScore,
        bool prevBlankAnchored,
        long consecutiveSkips,
        double eps = ReuseEpsDefault,
        long maxSkips = CouncilMaxSkips)
        => vadSilent
        && prevBlankAnchored
        && consecutiveSkips < maxSkips
        && bandScore.HasValue
        && double.IsFinite(bandScore.Value)
        && bandScore.Value <= eps;

    // D.1 flag-histogram: classify combos per paddle_call (not limited to
    // ground-truth spans). Flag-free has the same definition: recover=false + active=null.
    // 0=free, 1=recover, 2=active, 3=both.
    public static int FlagBucket(bool recoverShortBlank, string? activeShortText)
        => (recoverShortBlank ? 1 : 0) | (string.IsNullOrEmpty(activeShortText) ? 0 : 2);

    // E.4b canary dual-trigger: every 50 Paddle images OR 60s without a full OCR.
    public const long CanaryEveryImages = 50;
    public const double CanaryEverySeconds = 60;
    public static bool ShouldTriggerCanary(long paddleSinceAudit, double secondsSinceFullOcr)
        => paddleSinceAudit >= CanaryEveryImages || secondsSinceFullOcr >= CanaryEverySeconds;

    // Hit = carried vs audit differ: Detected differs, or same Detected but different text
    // (characters changed below the near-dup threshold -> carrying forward was wrong). Both blank -> no hit.
    public static bool IsCanaryHit(bool carriedDetected, string carriedText, bool auditDetected, string auditText)
    {
        if (carriedDetected != auditDetected) return true;
        if (!carriedDetected) return false;
        return !string.Equals(carriedText, auditText, StringComparison.Ordinal);
    }
}

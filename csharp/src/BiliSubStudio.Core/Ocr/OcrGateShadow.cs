namespace BiliSubStudio.Core.Ocr;

// E.3 — B/C SHADOW MODE (OCR Gates v1.3).
//
// Nguyen tac sat: shadow CHI GHI CHEP, khong bao gio skip/quyet dinh.
// Moi ham o day la thuan (pure) de contract test truc tiep. Histogram that
// nam trong OcrScanTelemetryCounters (scanner), du lieu hieu chuan offline.
public static class OcrGateShadow
{
    public const int Buckets = 10;

    // Diem 0..1 -> ngan 0..9. Khong finite (NaN/inf, worker da chan nhung thu
    // van khong tin) chim ve 0, khong bao gio throw vi shadow.
    public static int Bucketize(double score)
    {
        if (!double.IsFinite(score) || score <= 0) return 0;
        if (score >= 1) return Buckets - 1;
        return Math.Min(Buckets - 1, (int)(score * Buckets));
    }

    // "t0,t1,...,t9|b0,b1,...,b9": text truoc, blank sau, de doc offline.
    public static string FormatHistogram(long[] text, long[] blank)
    {
        if (text.Length != Buckets || blank.Length != Buckets)
            throw new ArgumentException($"Shadow histogram phai co {Buckets} ngan.", nameof(text));
        return string.Join(",", text) + "|" + string.Join(",", blank);
    }

    // E.4a B-REUSE (enforced): near-dup thi be ket qua truoc sang, KHONG goi Paddle.
    // eps mac dinh 0.02 (offline worker-metric: <=0.05 dung 20/20; lay bien an toan).
    // Hoi dong 2 thanh vien: B-reuse + canary audit (E.4b). C-edge tham gia o E.5.
    // 2026-09-13: canary bat ~15 hit/scan production (ke ca text->text: Paddle doi
    // chu giua 2 frame near-dup kieu 凉/涼) -> TAT mac dinh cho toi khi tune lai.
    // May moc (shadow/canary/chain-cap) giu nguyen de bat lai 1 dong.
    public const bool ReuseEnabledDefault = false;
    public const double ReuseEpsDefault = 0.02;
    public static bool ShouldReuse(double? bandScore, bool hasPrevious, double eps = ReuseEpsDefault)
        => hasPrevious && bandScore.HasValue && double.IsFinite(bandScore.Value) && bandScore.Value <= eps;

    // Council v1 SKIP (E.5 enforce): bỏ gọi Paddle ở mẫu sample KHI VÀ CHỈ KHI cả 3:
    // (1) VAD im (NoSub) + (2) B đứng yên (band<=eps) + (3) chuỗi neo blank-đọc-thật còn
    // hiệu lực (prevBlankAnchored: chỉ thành true bằng full-read blank, skip giữ nguyên,
    // full-read có chữ/audit/resume làm rớt) + (4) chưa quá cap liên tiếp.
    // C (text-presence) HIỆN ABSTAIN — luôn coi như SUSPICIOUS, KHÔNG vote: edge-density
    // tier-1 chồng lấn text/blank ở bucket 0 (shadow_c production: text 17553 vs blank 12004
    // cùng bucket) nên chưa đủ chuẩn vote; tier-2 CC/stroke tính sau, lúc đó thêm param thứ 4.
    // B-reuse KHÔNG DÙNG đường này (ReuseEnabledDefault=false, nghỉ hẳn): council là cơ chế
    // skip duy nhất, neo + cap + canary thay cho carry.
    // 2026-09-16: TẮT council skip fail-closed sau 8 canary-hit qua đêm, toàn bộ
    // tai:NoSub (VAD-báo-im nhưng đọc kiểm ra chữ). Chân VAD sai hệ thống nên mọi
    // skip im-đứng-yên đều nghi ăn chữ thật; canary chỉ soi mẫu抽查, không bắt hết.
    // Tiết kiệm đo được ~0.1% nên tắt không tiếc. Mở lại chỉ khi VAD được sửa +
    // parity lại + test ghim. Hàm ShouldCouncilSkip giữ nguyên để test logic.
    // 2026-09-17: MỞ LẠI có điều kiện — timeline từ TAI NGHE Whisper (phân biệt
    // nói/im thật, không phải năng lượng) chính là "VAD được sửa". Chỉ mở khi
    // timeline có cờ HasSub (OcrScanner truyền); timeline năng lượng/sidecar cũ
    // vẫn TẮT. Đa-cổng bảo thủ + cap + canary audit + kill-switch khi canary-hit
    // giữ nguyên — skip sai là canary bắt ngay trong log.
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

    // D.1 flag-histogram: phan loai combo co moi paddle_call (khong gioi han
    // ground-truth spans). Flag-free dinh nghia cung: recover=false + active=null.
    // 0=free, 1=recover, 2=active, 3=both.
    public static int FlagBucket(bool recoverShortBlank, string? activeShortText)
        => (recoverShortBlank ? 1 : 0) | (string.IsNullOrEmpty(activeShortText) ? 0 : 2);

    // E.4b canary dual-trigger: moi 50 anh Paddle HOAC 60s khong OCR day du.
    public const long CanaryEveryImages = 50;
    public const double CanaryEverySeconds = 60;
    public static bool ShouldTriggerCanary(long paddleSinceAudit, double secondsSinceFullOcr)
        => paddleSinceAudit >= CanaryEveryImages || secondsSinceFullOcr >= CanaryEverySeconds;

    // Hit = carried vs audit lech: Detected khac nhau, hoac cung detected ma text khac
    // (chu doi duoi nguong near-dup -> carry cu la sai). Ca hai blank -> khong hit.
    public static bool IsCanaryHit(bool carriedDetected, string carriedText, bool auditDetected, string auditText)
    {
        if (carriedDetected != auditDetected) return true;
        if (!carriedDetected) return false;
        return !string.Equals(carriedText, auditText, StringComparison.Ordinal);
    }
}

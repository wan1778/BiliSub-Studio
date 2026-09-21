namespace BiliSubStudio.Core.Ocr;

// E.5 — ROI STATIC ZONES (OCR Gates v1.3), DOT NAY CHI DUNG-NHIN (shadow).
//
// Y tuong: watermark/logo hien CUNG chu CUNG cho qua hang chuc sample lien tiep
// (bilibili + ten uploader goc phai, do that B.4). Hoc vung tinh bang cach dem:
// dong text xuat hien o box gan nhu he nhau >= RepeatThreshold sample lien tiep
// thi la vung tinh (SUSPICIOUS) — de xuat khoet. Sub thoai doi lien tuc nen
// khong bao gio dat nguong (an toan kieu false-positive thap).
// Dot nay: ghi vung hoc duoc + dem dong le ra bi loc (would-filter). Chua loc
// that, chua doi hanh vi. Box so sanh trong khong gian OCR crop (giong comparer).
public sealed class OcrRoiStaticZones
{
    public const int RepeatThreshold = 5;
    public const double BoxTolerancePx = 4;

    // Gate học zones (field 15/9: 396 zones trên ROI dải-sub đáy — toàn thoại lặp học thành
    // zone oan): chỉ học khi ROI scan phủ lên nửa TRÊN khung hình (nơi watermark/logo sống).
    // ROI dải-sub đáy (Y>=0.5, vd video hiện tại Y=0.82) thì text lặp nào cũng là thoại lặp
    // nên TẮT học hẳn: zones rỗng, wouldfilter 0, không đổi hành vi đọc. Đo thật bboxes.json:
    // dialogue y<=652/720, watermark x>=980 — rule Y là điều kiện cần để không oan.
    public static bool LearningEnabledFor(OcrRegion scanRegion) => scanRegion.Y < 0.5;

    public sealed record StaticZone(string Text, OcrBox Box, int Repeats);

    private readonly Dictionary<string, (OcrBox Box, int Streak)> _streaks = new(StringComparer.Ordinal);
    private readonly List<StaticZone> _zones = [];
    private readonly HashSet<string> _zoned = new(StringComparer.Ordinal);

    public IReadOnlyList<StaticZone> Zones => _zones;

    private static string Key(string text, OcrBox box)
    {
        var qx = (int)Math.Round(box.Left / BoxTolerancePx);
        var qy = (int)Math.Round(box.Top / BoxTolerancePx);
        var qw = (int)Math.Round((box.Right - box.Left) / BoxTolerancePx);
        var qh = (int)Math.Round((box.Bottom - box.Top) / BoxTolerancePx);
        return text + "|" + qx + "," + qy + "," + qw + "," + qh;
    }

    // Moi sample: nap lines da filter. Tra ve so dong le ra bi loc (would-filter).
    public int Observe(IReadOnlyList<OcrLine> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Box is not { Length: 4 }) continue;
            var box = new OcrBox(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            if (box.Right <= box.Left || box.Bottom <= box.Top) continue;
            seen.Add(ObserveLine(line.Text ?? string.Empty, box));
        }
        // Streak chi noi khi xuat hien sample LIEN TIEP: key vang mat thi reset.
        var gone = new List<string>();
        foreach (var key in _streaks.Keys)
            if (!seen.Contains(key)) gone.Add(key);
        foreach (var key in gone) _streaks.Remove(key);
        // Dem would-filter: dong nam trong vung da hoc.
        var wouldFilter = 0;
        foreach (var line in lines)
        {
            if (line.Box is not { Length: 4 }) continue;
            var box = new OcrBox(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            foreach (var zone in _zones)
            {
                if (BoxInside(box, zone.Box)) { wouldFilter++; break; }
            }
        }
        return wouldFilter;
    }

    private string ObserveLine(string text, OcrBox box)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var key = Key(text, box);
        if (_zoned.Contains(key)) return key;
        if (_streaks.TryGetValue(key, out var entry))
            entry = (entry.Box, entry.Streak + 1);
        else
            entry = (box, 1);
        _streaks[key] = entry;
        if (entry.Streak >= RepeatThreshold)
        {
            _streaks.Remove(key);
            _zoned.Add(key);
            _zones.Add(new StaticZone(text, entry.Box, entry.Streak));
        }
        return key;
    }

    internal static bool BoxInside(OcrBox inner, OcrBox outer)
        => inner.Left >= outer.Left - BoxTolerancePx && inner.Top >= outer.Top - BoxTolerancePx
        && inner.Right <= outer.Right + BoxTolerancePx && inner.Bottom <= outer.Bottom + BoxTolerancePx;
}

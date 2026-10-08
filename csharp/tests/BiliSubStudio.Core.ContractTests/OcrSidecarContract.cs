using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Sidecar trong Data app, cấm cạnh video (28/09, theo lệnh user): probe/VAD/pxv
// tính 1 lần xài nhiều scan. Alongside chỉ đọc di sản, mọi Save mới vào Data.
// Quy tắc thép test ở đây: mismatch/hỏng/version lệch => bỏ qua lặng lẽ (null), scan
// tính lại đường cũ; xóa file không sao; thư mục khóa ghi thì fallback Data.
internal static class OcrSidecarContract
{
    public static async Task RunAsync()
    {
        // 1. Probe round-trip + version/dims guard.
        var probe = new VideoProbe(1920, 1080, 1.0 / 90000, 1.0 / 30, 30.0, 1.4, 1, 90000, 22078.5);
        var restored = OcrSidecar.ToProbe(OcrSidecar.FromProbe(probe));
        Check(restored == probe, "probe section must round-trip exactly");
        Check(OcrSidecar.ToProbe(OcrSidecar.FromProbe(probe) with { V = 999 }) is null, "probe version mismatch must miss");
        Check(OcrSidecar.ToProbe(OcrSidecar.FromProbe(probe) with { Width = 0 }) is null, "probe bad dims must miss");

        // 2. VAD bitpack round-trip cả 3 cờ + hỏng.
        var flags = new[] { VadFlag.NoSub, VadFlag.Suspicious, VadFlag.HasSub, VadFlag.NoSub, VadFlag.Suspicious };
        var timeline = new VadTimeline(0.2, flags);
        var vadBack = OcrSidecar.ToTimeline(OcrSidecar.FromTimeline(timeline));
        Check(vadBack is not null && vadBack.FrameSeconds == 0.2 && vadBack.Flags.SequenceEqual(flags),
            "vad section must round-trip all 3 flags");
        Check(OcrSidecar.ToTimeline(OcrSidecar.FromTimeline(timeline) with { V = 999 }) is null, "vad version mismatch must miss");
        Check(OcrSidecar.ToTimeline(new OcrSidecar.VadSection(1, 0.2, 1, "!!!")) is null, "vad bad base64 must miss");
        var badBits = OcrSidecar.PackVadFlags([VadFlag.NoSub]);
        badBits[0] = 0xFF;
        try { OcrSidecar.UnpackVadFlags(badBits, 1); Check(false, "vad corrupt bits must throw"); }
        catch (InvalidDataException) { }

        // 3. PXV: Same/Different persist, Unavailable bỏ, rect hỏng bỏ, verdict lạ bỏ.
        var crop = new System.Drawing.Rectangle(10, 20, 100, 40);
        Check(OcrSidecar.ToEntry(1, 2, crop, PixelCheckResult.Same)?.Verdict == 0, "Same must persist as 0");
        Check(OcrSidecar.ToEntry(1, 2, crop, PixelCheckResult.Different)?.Verdict == 1, "Different must persist as 1");
        Check(OcrSidecar.ToEntry(1, 2, crop, PixelCheckResult.Unavailable) is null, "Unavailable must not persist");
        Check(OcrSidecar.FromEntry(new OcrSidecar.PixelEntry(1, 2, 10, 20, 110, 60, 0))?.Verdict == PixelCheckResult.Same,
            "pxv entry must round-trip verdict + crop");
        Check(OcrSidecar.FromEntry(new OcrSidecar.PixelEntry(1, 2, 10, 20, 110, 60, 7)) is null, "pxv bad verdict must miss");
        Check(OcrSidecar.FromEntry(new OcrSidecar.PixelEntry(1, 2, 50, 20, 10, 60, 0)) is null, "pxv inverted rect must miss");

        // 4. Identity: khớp size+mtime, path chỉ để hiển thị (đổi tên/chuyển ổ vẫn trúng).
        var id = new OcrSidecar.FileIdentity("E:\\a\\v.mp4", 100, 200);
        Check(id.Matches(new OcrSidecar.FileIdentity("D:\\b\\v.mp4", 100, 200)), "identity must ignore path");
        Check(!id.Matches(new OcrSidecar.FileIdentity("E:\\a\\v.mp4", 101, 200)), "identity must reject size change");
        Check(!id.Matches(new OcrSidecar.FileIdentity("E:\\a\\v.mp4", 100, 201)), "identity must reject mtime change");
        Check(OcrSidecar.FileIdentity.FromFile("Z:\\__no_dir__\\__no_file__.mp4") is null, "missing file must have no identity");

        // 5. Parse hỏng => null, không throw.
        Check(OcrSidecar.Parse("") is null, "empty file must parse as null");
        Check(OcrSidecar.Parse("{oops") is null, "truncated JSON must parse as null");
        Check(OcrSidecar.Parse("{}") is null, "identity-less doc must parse as null");

        // 6. Store qua temp dirs: save→load, mismatch, fallback ổ khóa.
        var root = Path.Combine(Path.GetTempPath(), "sidecar-test-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "data");
        var videos = Path.Combine(root, "videos");
        Directory.CreateDirectory(videos);
        try
        {
            var video = Path.Combine(videos, "v.mp4");
            await File.WriteAllBytesAsync(video, new byte[1024]);
            var identity = OcrSidecar.FileIdentity.FromFile(video)
                ?? throw new InvalidOperationException("temp video must have identity");
            var store = new OcrSidecarStore(appData);
            var doc = new OcrSidecar.SidecarDocument(identity, OcrSidecar.FromProbe(probe), OcrSidecar.FromTimeline(timeline),
                new OcrSidecar.PixelSection(OcrSidecar.PixelVersion, [new OcrSidecar.PixelEntry(1, 2, 10, 20, 110, 60, 0)]));
            await store.SaveAsync(video, doc, CancellationToken.None);
            Check(!File.Exists(store.AlongsidePath(video)), "sidecar must not land next to video");
            Check(File.Exists(store.FallbackPath(identity)), "sidecar must land in Data");
            var loaded = await store.LoadAsync(video, identity, CancellationToken.None);
            Check(loaded is not null && loaded.Identity.Matches(identity), "sidecar must reload with matching identity");
            Check(OcrSidecar.ToTimeline(loaded!.Vad)!.Flags.SequenceEqual(flags), "reloaded vad must match");
            Check(OcrSidecar.ToProbe(loaded.Probe) == probe, "reloaded probe must match");
            var moved = await store.LoadAsync(video, new OcrSidecar.FileIdentity(identity.Path, 1025, identity.ModUnixNano), CancellationToken.None);
            Check(moved is null, "size-changed file must miss sidecar");

            // Ổ khóa ghi (Z: không tồn tại) => fallback Data, lần 2 đọc trúng fallback.
            var lockedVideo = "Z:\\__no_drive__\\v.mp4";
            var lockedId = new OcrSidecar.FileIdentity(lockedVideo, 100, 200);
            var lockedDoc = new OcrSidecar.SidecarDocument(lockedId, null, null, null);
            await store.SaveAsync(lockedVideo, lockedDoc, CancellationToken.None);
            Check(File.Exists(store.FallbackPath(lockedId)), "unwritable dir must fall back to Data");
            var lockedBack = await store.LoadAsync(lockedVideo, lockedId, CancellationToken.None);
            Check(lockedBack is not null, "fallback sidecar must reload");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // 7. Comparer Seed→Snapshot (không FFmpeg): Same/Different vào, Unavailable + trùng bỏ.
        var comparer = new ProductionPixelComparer();
        var seeded = comparer.Seed("v.mp4", [
            (1L, 2L, new System.Drawing.Rectangle(0, 0, 50, 20), PixelCheckResult.Same),
            (3L, 4L, new System.Drawing.Rectangle(0, 0, 50, 20), PixelCheckResult.Different),
            (5L, 6L, new System.Drawing.Rectangle(0, 0, 50, 20), PixelCheckResult.Unavailable),
            (1L, 2L, new System.Drawing.Rectangle(0, 0, 50, 20), PixelCheckResult.Different),
        ]);
        Check(seeded == 2, $"seed must add 2 decisive verdicts (got {seeded})");
        var snap = comparer.Snapshot();
        Check(snap.Count == 2, "snapshot must hold seeded verdicts");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

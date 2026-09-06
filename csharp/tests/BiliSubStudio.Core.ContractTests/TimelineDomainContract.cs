using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G3 domain: int64-microsecond time model, frame-grid snapping, explicit
// source/sequence mappings, asset/clip/track/sequence validation and
// isolated V2 persistence roundtrip. No FFmpeg, no production writes.
internal static class TimelineDomainContract
{
    public static async Task RunAsync()
    {
        TimeModel();
        FrameGridBoundaries();
        TimeMappings();
        PropertyRoundtrips();
        AssetValidation();
        ClipValidation();
        TrackOrder();
        SequenceValidation();
        await StoreRoundtripAsync();
    }

    private static void TimeModel()
    {
        Check(TimelineTime.FromSeconds(0) == 0, "zero seconds must map to zero microseconds");
        Check(TimelineTime.FromSeconds(1.5) == 1_500_000, "fractional seconds lost microsecond precision");
        Check(Math.Abs(TimelineTime.ToSeconds(1_500_000) - 1.5) < 1e-9, "microsecond to second drifted");
        Check(TimelineTime.FormatSeconds(1_500_000) == "1.500", "boundary format must be invariant 0.000");
        Throws<InvalidDataException>(() => TimelineTime.FromSeconds(double.NaN), "NaN seconds accepted");
        Throws<InvalidDataException>(() => TimelineTime.FromSeconds(-1), "negative seconds accepted");
        Throws<InvalidDataException>(() => TimelineTime.ToSeconds(-1), "negative microseconds accepted");
    }

    private static void FrameGridBoundaries()
    {
        // 30 fps frame = 33333.333us. The exact frame before/after a cut must
        // snap deterministically on both sides of the boundary.
        const double fps = 30;
        var frame = (long)Math.Round(1_000_000.0 / fps, MidpointRounding.AwayFromZero);
        Check(TimelineTime.SnapToFrameGrid(frame, fps) == frame, "exact frame boundary moved");
        Check(TimelineTime.SnapToFrameGrid(frame - 1, fps) == frame, "sub-frame epsilon below cut snapped away");
        Check(TimelineTime.SnapToFrameGrid(frame + 1, fps) == frame, "sub-frame epsilon above cut snapped away");
        var half = frame / 2;
        Check(TimelineTime.SnapToFrameGrid(half, fps) == frame
            || TimelineTime.SnapToFrameGrid(half, fps) == 0, "half-frame snap left the grid");
        var snapped = TimelineTime.SnapToFrameGrid(123_456_789, fps);
        Check(snapped % 1 == 0 && Math.Abs(snapped - 123_456_789) <= frame,
            "snap moved more than one frame");
        Throws<InvalidDataException>(() => TimelineTime.SnapToFrameGrid(100, 0), "zero FPS accepted");
        Throws<InvalidDataException>(() => TimelineTime.SnapToFrameGrid(-5, fps), "negative time accepted");
    }

    private static void TimeMappings()
    {
        // Clip: timeline 10s..20s plays source 100s..110s at 1x.
        var sourceIn = TimelineTime.FromSeconds(100);
        var start = TimelineTime.FromSeconds(10);
        Check(TimelineTime.ClipDurationUs(sourceIn, sourceIn + 10_000_000, 1.0) == 10_000_000,
            "clip duration broke the (out-in)/rate identity");
        var atCut = TimelineTime.SequenceToSourceUs(start + 10_000_000, start, sourceIn, 1.0);
        Check(atCut == sourceIn + 10_000_000, "cut-end source mapping drifted");
        var back = TimelineTime.SourceToSequenceUs(atCut, start, sourceIn, 1.0);
        Check(back == start + 10_000_000, "source to sequence roundtrip drifted");
        // Exact frame before and after the cut map to adjacent source frames.
        var before = TimelineTime.SequenceToSourceUs(start + 10_000_000 - 1, start, sourceIn, 1.0);
        var after = TimelineTime.SequenceToSourceUs(start + 10_000_000 + 1, start, sourceIn, 1.0);
        Check(after - before == 2, "cut adjacency lost single-microsecond determinism");
        Throws<InvalidDataException>(
            () => TimelineTime.SequenceToSourceUs(start - 1, start, sourceIn, 1.0),
            "pre-clip sequence time accepted");
        Throws<InvalidDataException>(
            () => TimelineTime.SourceToSequenceUs(sourceIn - 1, start, sourceIn, 1.0),
            "pre-range source time accepted");
    }

    private static void PropertyRoundtrips()
    {
        var random = new Random(20260906);
        for (var index = 0; index < 500; index++)
        {
            var seconds = random.NextDouble() * 6 * 3600;
            var us = TimelineTime.FromSeconds(seconds);
            var recovered = TimelineTime.ToSeconds(us);
            Check(Math.Abs(recovered - seconds) < 1e-6 + 1e-9 * seconds,
                $"second/microsecond roundtrip drifted at {seconds}");
            var fps = new[] { 24.0, 25, 30, 50, 60 }[index % 5];
            var snapped = TimelineTime.SnapToFrameGrid(us, fps);
            var resnapped = TimelineTime.SnapToFrameGrid(snapped, fps);
            Check(snapped == resnapped, "frame snap is not idempotent");
        }
    }

    private static void AssetValidation()
    {
        var asset = TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080,
            TimelineTime.FromSeconds(60), 30, hasAudio: true, size: 100, lastWriteUtcTicks: 7);
        asset.Validate();
        Throws<InvalidDataException>(
            () => TimelineMediaAsset.Create("C:/v/a.mp4", 0, 1080, 60_000_000, 30, true),
            "zero width accepted");
        Throws<InvalidDataException>(
            () => TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080, 0, 30, true),
            "zero duration accepted");
        Throws<InvalidDataException>(
            () => TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080, 60_000_000, 0, true),
            "zero FPS accepted");
        Throws<InvalidDataException>(
            () => TimelineMediaAsset.Create("  ", 1920, 1080, 60_000_000, 30, true),
            "empty asset path accepted");
    }

    private static void ClipValidation()
    {
        var asset = TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080,
            TimelineTime.FromSeconds(60), 30, hasAudio: true);
        var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 1.0);
        clip.ValidateAgainst(asset);
        Check(clip.DurationUs == 10_000_000 && clip.TimelineEndUs == 10_000_000,
            "clip duration/end broke the time identity");
        var over = clip with { SourceOutUs = asset.DurationUs + 1 };
        Throws<InvalidDataException>(() => over.ValidateAgainst(asset), "over-duration clip accepted");
        var other = TimelineMediaAsset.Create("C:/v/b.mp4", 1280, 720,
            TimelineTime.FromSeconds(60), 30, hasAudio: false);
        Throws<InvalidDataException>(() => clip.ValidateAgainst(other), "cross-asset clip accepted");
        Throws<InvalidDataException>(
            () => new TimelineClip(TimelineClip.NewId(), asset.Id, -1, 0, 5, 1.0).ValidateShape(),
            "negative timeline start accepted");
    }

    private static void TrackOrder()
    {
        var expected = new[]
        {
            TimelineTrackType.Img, TimelineTrackType.Sub, TimelineTrackType.Fx,
            TimelineTrackType.Video, TimelineTrackType.SourceAudio, TimelineTrackType.Voice,
            TimelineTrackType.Music,
        };
        Check(TimelineTrack.CanonicalOrder.SequenceEqual(expected), "DEC-007 track order drifted");
        Check(TimelineTrack.IsCanonicalOrder(expected), "canonical order check rejected itself");
        Check(!TimelineTrack.IsCanonicalOrder(expected.Reverse()), "reversed order passed as canonical");
        var defaults = TimelineTrack.CreateDefaultTracks();
        Check(TimelineTrack.IsCanonicalOrder(defaults.Select(track => track.Type)),
            "default tracks broke canonical order");
        foreach (var track in defaults) track.Validate();
    }

    private static void SequenceValidation()
    {
        var a = TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080,
            TimelineTime.FromSeconds(60), 30, hasAudio: true);
        var b = TimelineMediaAsset.Create("C:/v/b.mp4", 1280, 720,
            TimelineTime.FromSeconds(30), 25, hasAudio: false);
        var first = new TimelineClip(TimelineClip.NewId(), a.Id, 0, 0, 10_000_000, 1.0);
        var second = new TimelineClip(TimelineClip.NewId(), b.Id, 10_000_000, 5_000_000, 15_000_000, 1.0);
        var sequence = TimelineSequence.Create([first, second], [a, b], outputFrameRate: 30);
        Check(sequence.OutputWidth == 1920 && sequence.OutputHeight == 1080,
            "output geometry must default to the first clip (PDEC-001)");
        Check(sequence.OutputFrameRate == 30, "explicit sequence FPS lost (PDEC-002)");
        Check(sequence.TotalDurationUs == 20_000_000, "sequence duration broke contiguous join");
        var overlap = second with { TimelineStartUs = 9_999_999 };
        Throws<InvalidDataException>(
            () => TimelineSequence.Create([first, overlap], [a, b], 30),
            "overlapping hard cut accepted");
        var reflowed = TimelineSequence.ReflowContiguous([second, first]);
        Check(reflowed[0].TimelineStartUs == 0 && reflowed[1].TimelineStartUs == 10_000_000,
            "reflow did not join clips end-to-start preserving order");
        Throws<InvalidDataException>(
            () => TimelineSequence.Create([first, second], [a, b], 0),
            "implicit sequence FPS accepted");
        var plan = TimelineCompiler.Compile(
            new TimelineProjectV2("a".PadLeft(32, 'b'), "P", DateTimeOffset.UtcNow, [a, b], sequence));
        Check(plan.Clips.Count == 2 && plan.TotalDurationUs == 20_000_000,
            "compiler did not resolve ordered clip segments");
        Check(plan.Clips[0].SequenceStartUs == 0 && plan.Clips[1].SequenceStartUs == 10_000_000,
            "resolved plan lost hard-cut order");
    }

    private static async Task StoreRoundtripAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-timeline-g3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TimelineProjectStoreV2(root);
            var asset = TimelineMediaAsset.Create("C:/v/a.mp4", 1920, 1080,
                TimelineTime.FromSeconds(60), 30, hasAudio: true, size: 10, lastWriteUtcTicks: 5);
            var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 1.0);
            var sequence = TimelineSequence.Create([clip], [asset], outputFrameRate: 30);
            var session = new TimelineEditorSession(store);
            var created = await session.CreateAsync("G3 kiểm tra", CancellationToken.None);
            var full = created with { Assets = [asset], Sequence = sequence };
            await session.SaveAsync(full, CancellationToken.None);
            var reopened = await store.LoadAsync(full.Id, CancellationToken.None);
            Check(reopened.Assets.Count == 1 && reopened.Assets[0].Id == asset.Id,
                "asset did not survive V2 persistence roundtrip");
            Check(reopened.Sequence is not null && reopened.Sequence.TotalDurationUs == 10_000_000,
                "sequence did not survive V2 persistence roundtrip");
            Check(string.Equals(reopened.Id, full.Id, StringComparison.Ordinal),
                "project ID changed across save/load (DEC-012)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}

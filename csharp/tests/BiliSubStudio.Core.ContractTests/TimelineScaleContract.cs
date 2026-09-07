using System.Diagnostics;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G20 scale contracts (MASTER_PLAN section 13). These are not wall-clock
// benchmarks: they prove the domain, compiler and audio ops stay correct and
// bounded at the documented scale targets, with generous but finite budgets so
// a quadratic regression fails instead of silently passing on fast machines.
internal static class TimelineScaleContract
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static async Task RunAsync()
    {
        HundredClipsCompileAndOps();
        SixHourSequenceIsExact();
        TenThousandSubtitleCuesValidateAndCompile();
        TwoThousandEffectItemsRideOps();
        ViewportCullsAndRoundTrips();
        await LargeProjectRoundTripsThroughStoreAsync();
    }

    private static void HundredClipsCompileAndOps()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/scale-100.mp4", 1920, 1080, 6 * 3_600_000_000L, 30, hasAudio: true);
        var clips = new List<TimelineClip>(100);
        for (var index = 0; index < 100; index++)
            clips.Add(TimelineClip.Create(asset.Id, index * 10_000_000L, index * 10_000_000L, (index + 1) * 10_000_000L));

        var sw = Stopwatch.StartNew();
        var state = TimelineEditState.Create(clips, []);
        var plan = TimelineCompiler.Compile(
            new TimelineProjectV2(
                TimelineProjectV2.NewId(), "G20 100 clips", DateTimeOffset.UtcNow,
                [asset], new TimelineSequence(state.Clips, 1920, 1080, 30),
                state.Items, [], [], TimelineSourceAudioState.Defaults(state.Clips), [], null));
        // One move inside the 100-clip list must stay one bounded transaction.
        var reordered = TimelineSequenceOps.Reorder(state.Clips, state.Clips[50].Id, 0);
        var split = TimelineSequenceOps.Split(state.Clips, state.Clips[10].Id, 100_000_000L + 5_000_000L);
        var trimmed = TimelineSequenceOps.Trim(state.Clips, state.Clips[90].Id, 900_000_000L, 901_000_000L);
        var deleted = TimelineSequenceOps.Delete(state.Clips, state.Clips[99].Id);
        sw.Stop();

        Check(plan.Clips.Count == 100 && plan.TotalDurationUs == 1_000_000_000L,
            "100-clip plan geometry drifted");
        Check(reordered.Count == 100 && reordered[0].Id == state.Clips[50].Id
            && reordered[0].TimelineStartUs == 0,
            "100-clip reorder drifted");
        Check(split.Count == 101, "100-clip split did not split");
        Check(trimmed[90].SourceInUs == 900_000_000L, "100-clip trim drifted");
        Check(deleted.Count == 99, "100-clip delete drifted");
        Check(sw.Elapsed < Budget, $"100-clip ops exceeded budget: {sw.Elapsed}");
    }

    private static void SixHourSequenceIsExact()
    {
        // One 6-hour clip at 30 fps: microsecond grid and frame grid stay exact
        // and no int64 overflow occurs anywhere in the time math.
        const long sixHoursUs = 6L * 3_600 * 1_000_000;
        var asset = TimelineMediaAsset.Create(
            "C:/video/six-hours.mp4", 3840, 2160, sixHoursUs, 30, hasAudio: true);
        var clip = TimelineClip.Create(asset.Id, 0, 0, sixHoursUs);
        var sequence = new TimelineSequence([clip], 3840, 2160, 30);
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(), "G20 six hours", DateTimeOffset.UtcNow,
            [asset], sequence, [], [], [], TimelineSourceAudioState.Defaults([clip]), [], null);

        var sw = Stopwatch.StartNew();
        project.Validate();
        var plan = TimelineCompiler.Compile(project);
        sw.Stop();

        Check(sequence.TotalDurationUs == sixHoursUs && plan.TotalDurationUs == sixHoursUs,
            "six-hour duration drifted");
        Check(TimelineTime.SnapToFrameGrid(sixHoursUs, 30) == sixHoursUs,
            "six-hour frame-grid snap drifted");
        Check(TimelineTime.ClipDurationUs(0, sixHoursUs, 1) == sixHoursUs,
            "six-hour clip duration drifted");
        Check(plan.Audio.MaximumOutputDriftUs == 16_667,
            "six-hour drift bound drifted");
        Check(sw.Elapsed < Budget, $"six-hour validation exceeded budget: {sw.Elapsed}");
    }

    private static void TenThousandSubtitleCuesValidateAndCompile()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/cues.mp4", 1280, 720, 6 * 3_600_000_000L, 30, hasAudio: true);
        var clip = TimelineClip.Create(asset.Id, 0, 0, 6L * 3_600 * 1_000_000);
        var subtitle = TimelineItemOps.AttachSubtitleToClip(clip.Id, 0, clip.DurationUs, "C:/subs/big.srt");
        var cues = new List<TimelineSubtitleCue>(10_000);
        for (var index = 0; index < 10_000; index++)
        {
            var start = index * 2_000_000L;
            cues.Add(TimelineSubtitleCue.Create(subtitle.Id, start, start + 1_800_000L, $"Cue {index}"));
        }
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(), "G20 10k cues", DateTimeOffset.UtcNow,
            [asset], new TimelineSequence([clip], 1280, 720, 30),
            [subtitle], cues, [], TimelineSourceAudioState.Defaults([clip]), [], null);

        var sw = Stopwatch.StartNew();
        project.Validate();
        var plan = TimelineCompiler.Compile(project);
        sw.Stop();

        Check(plan.SubtitleCues.Count == 10_000,
            "10k-cue plan lost cues");
        Check(plan.SubtitleCues[9_999].SequenceStartUs == 9_999 * 2_000_000L,
            "10k-cue plan drifted at the tail cue");
        Check(sw.Elapsed < Budget, $"10k-cue validation exceeded budget: {sw.Elapsed}");
    }

    private static void TwoThousandEffectItemsRideOps()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/effects.mp4", 1280, 720, 3_600_000_000L, 30, hasAudio: true);
        var clip = TimelineClip.Create(asset.Id, 0, 0, 1_000_000_000L);
        var items = new List<TimelineItem>(2_000);
        for (var index = 0; index < 2_000; index++)
        {
            var start = index * 400_000L;
            items.Add(new TimelineItem(
                TimelineItem.NewId(), TimelineItemKind.Effect, clip.Id,
                start, start + 200_000L, 0, 0, "blur:0.1,0.1,0.2,0.2:18"));
        }
        var state = TimelineEditState.Create([clip], items);

        var sw = Stopwatch.StartNew();
        var plan = TimelineCompiler.Compile(new TimelineProjectV2(
            TimelineProjectV2.NewId(), "G20 2k effects", DateTimeOffset.UtcNow,
            [asset], new TimelineSequence([clip], 1280, 720, 30),
            state.Items, [], [], TimelineSourceAudioState.Defaults([clip]), [], null));
        var split = TimelineDependencyOps.Split(state, clip.Id, 500_100_000L);
        var trimmed = TimelineDependencyOps.Trim(
            state, clip.Id, 100_000_000L, 900_000_000L);
        var reordered = TimelineDependencyOps.Reorder(state, clip.Id, 0);
        sw.Stop();

        Check(plan.Items.Count == 2_000, "2k-effect plan lost items");
        Check(split.Items.Count == 2_000 + 1
            && split.Items.Count(item => item.Kind == TimelineItemKind.Effect) == 2_001,
            "2k-effect split lost or duplicated items");
        Check(trimmed.Items.Count == 1_750,
            "2k-effect trim kept an incorrect range");
        Check(reordered.Items.Count == 2_000,
            "2k-effect reorder lost items");
        Check(sw.Elapsed < Budget, $"2k-effect ops exceeded budget: {sw.Elapsed}");
    }

    private static void ViewportCullsAndRoundTrips()
    {
        var assetId = TimelineMediaAsset.NewId();
        var clips = new List<TimelineClip>(100);
        for (var index = 0; index < 100; index++)
            clips.Add(TimelineClip.Create(
                assetId, index * 10_000_000L, index * 10_000_000L, (index + 1) * 10_000_000L));

        var canonical = TimelineEditState.Create(clips.AsEnumerable().Reverse().ToArray(), []);
        Check(canonical.Clips.Select(clip => clip.Id).SequenceEqual(clips.Select(clip => clip.Id)),
            "edit state did not canonicalize persisted clip order");
        var visible = TimelineViewport.VisibleClipRange(
            canonical.Clips, horizontalOffset: 40_000, viewportWidth: 700,
            TimelineViewport.DefaultPixelsPerSecond);
        Check(visible == (49, 51),
            "viewport did not cull the 100-clip sequence to its visible range");

        const long sixHoursUs = 6L * 3_600 * 1_000_000;
        var x = TimelineViewport.SequenceX(
            sixHoursUs, TimelineViewport.DefaultPixelsPerSecond);
        Check(TimelineViewport.SequenceUs(
                x, TimelineViewport.DefaultPixelsPerSecond, sixHoursUs) == sixHoursUs,
            "six-hour viewport coordinate round trip drifted");
        Check(TimelineViewport.SurfaceWidth(
                sixHoursUs, TimelineViewport.MinimumPixelsPerSecond, 700) == 21_620,
            "six-hour minimum-zoom surface width drifted");
    }

    private static async Task LargeProjectRoundTripsThroughStoreAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g20-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var asset = TimelineMediaAsset.Create(
                "C:/video/big-store.mp4", 1920, 1080, 6 * 3_600_000_000L, 30, hasAudio: true);
            var clip = TimelineClip.Create(asset.Id, 0, 0, 6L * 3_600 * 1_000_000);
            var subtitle = TimelineItemOps.AttachSubtitleToClip(clip.Id, 0, clip.DurationUs, "C:/subs/big.srt");
            var cues = new List<TimelineSubtitleCue>(10_000);
            for (var index = 0; index < 10_000; index++)
            {
                var start = index * 2_000_000L;
                cues.Add(TimelineSubtitleCue.Create(subtitle.Id, start, start + 1_800_000L, $"Cue {index}"));
            }
            var project = new TimelineProjectV2(
                TimelineProjectV2.NewId(), "G20 store", DateTimeOffset.UtcNow,
                [asset], new TimelineSequence([clip], 1920, 1080, 30),
                [subtitle], cues, [], TimelineSourceAudioState.Defaults([clip]), [], null);
            var store = new TimelineProjectStoreV2(root);

            var sw = Stopwatch.StartNew();
            await store.SaveAsync(project, CancellationToken.None);
            var loaded = await store.LoadAsync(project.Id, CancellationToken.None);
            sw.Stop();

            Check(loaded.SubtitleCues.Count == 10_000
                && loaded.SubtitleCues.SequenceEqual(project.SubtitleCues)
                && loaded.Sequence is not null
                && loaded.Sequence.VideoClips.SequenceEqual(project.Sequence!.VideoClips),
                "large project round trip lost state");
            Check(sw.Elapsed < Budget, $"large store round trip exceeded budget: {sw.Elapsed}");
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
}

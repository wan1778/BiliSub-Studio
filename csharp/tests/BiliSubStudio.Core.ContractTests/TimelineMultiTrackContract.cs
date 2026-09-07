using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G21 multi-track compiler contract (user-approved Concat parity, 2026-09-08).
// Locks: track validation (visual overlap rejected, audio overlap allowed),
// cross-track references (text track must exist), z-index ordered resolution
// into the plan, transform passthrough, schema-4 upgrade to empty multi-track
// state, and plan roundtrip through persistence.
public static class TimelineMultiTrackContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-timeline-g21-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new TimelineProjectStoreV2(root);
            var project = await store.CreateAsync("G21 multitrack", CancellationToken.None);

            var asset = new TimelineMediaAsset(
                TimelineMediaAsset.NewId(),
                "C:/video/g21.mp4",
                1920, 1080,
                DurationUs: 10_000_000,
                FrameRate: 30,
                HasAudio: true,
                Size: 123,
                LastWriteUtcTicks: 1);
            var primaryClip = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
            var sequence = TimelineSequence.Create([primaryClip], [asset], outputFrameRate: 30);

            var overlayTrack = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.VideoOverlay, "Overlay 1",
                ZIndex: 10, IsVisible: true, IsMuted: false, IsLocked: false, GainDb: 0, Clips: []);
            var textTrack = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.Text, "Text 1",
                ZIndex: 20, IsVisible: true, IsMuted: false, IsLocked: false, GainDb: 0, Clips: []);
            var musicTrack = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.Music, "Music 2",
                ZIndex: 0, IsVisible: true, IsMuted: false, IsLocked: false, GainDb: -6, Clips: []);

            var overlayClip = TimelineTrackClip.Create(
                asset.Id, overlayTrack.Id, 2_000_000, 1_000_000, 5_000_000,
                playbackRate: 1.0,
                transform: new TimelineClipTransform(0.25, 0.25, 0.5, 0.5, 0, 0.9));
            var text = TimelineTextItem.Create(
                textTrack.Id, 1_000_000, 4_000_000, "Xin chào G21",
                style: new TimelineTextStyle(
                    "Arial", 0.08, true, false, "#FFFFFF", "#000000", 0.004, "#000000", 0.5),
                animation: TimelineTextAnimation.FadeIn, animationDurationUs: 400_000);

            var withTracks = project with
            {
                Assets = [asset],
                Sequence = sequence,
                SourceAudio = TimelineSourceAudioState.Defaults([primaryClip]),
                Tracks = [overlayTrack with { Clips = [overlayClip] }, textTrack, musicTrack],
                TextItems = [text],
            };
            await store.SaveAsync(withTracks, CancellationToken.None);

            var reloaded = await store.LoadAsync(withTracks.Id, CancellationToken.None);
            var plan = TimelineCompiler.Compile(reloaded);

            Check(TimelineProjectV2.Schema == 5, "project schema constant must be 5");
            Check(reloaded.Tracks.Count == 3, "track count must survive persistence");
            Check(plan.OverlayClips.Count == 1, "plan must contain exactly one overlay clip");
            var resolved = plan.OverlayClips.Single();
            Check(string.Equals(resolved.TrackId, overlayTrack.Id, StringComparison.Ordinal),
                "overlay clip must keep its track id");
            Check(resolved.ZIndex == 10 && resolved.TrackKind == TimelineTrackKind.VideoOverlay,
                "overlay clip must keep track kind and z-index");
            Check(resolved.Transform.PositionX == 0.25 && resolved.Transform.Opacity == 0.9,
                "overlay clip transform must roundtrip exactly");
            Check(resolved.SourceInUs == 1_000_000 && resolved.SourceOutUs == 5_000_000,
                "overlay clip source range must roundtrip");
            Check(plan.TextItems.Count == 1
                && plan.TextItems[0].Content == "Xin chào G21"
                && plan.TextItems[0].Animation == TimelineTextAnimation.FadeIn,
                "text item must survive compile with animation");
            Check(plan.Clips.Count == 1 && plan.Clips[0].ClipId == primaryClip.Id,
                "primary plan clips must stay untouched by overlay resolution");
            // Schema-4 payload upgrade: strip schema-5 fields, write schema 4.
            var path = Path.Combine(root, withTracks.Id + ".json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["schema"] = 4;
            json.Remove("tracks");
            json.Remove("transitions");
            json.Remove("text_items");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var upgraded = await store.LoadAsync(withTracks.Id, CancellationToken.None);
            Check(upgraded.Tracks.Count == 0 && upgraded.Transitions.Count == 0
                && upgraded.TextItems.Count == 0,
                "schema-4 payload must upgrade to empty multi-track state");
            var upgradedPlan = TimelineCompiler.Compile(upgraded);
            Check(upgradedPlan.OverlayClips.Count == 0 && upgradedPlan.TextItems.Count == 0,
                "upgraded schema-4 plan must have no overlays or text");

            // Negative: overlapping clips on one visual track must be rejected.
            var overlapping = TimelineTrackClip.Create(
                asset.Id, overlayTrack.Id, 1_000_000, 0, 3_000_000);
            var overlappingProject = upgraded with
            {
                Tracks = [overlayTrack with { Clips = [overlayClip, overlapping] }],
                TextItems = [],
            };
            Throws<InvalidDataException>(
                () => overlappingProject.Validate(),
                "same-track visual overlap must be rejected");

            // Negative: text on a missing text track.
            var orphanText = text with { TrackId = TimelineTrackV2.NewId() };
            var orphanProject = upgraded with { TextItems = [orphanText] };
            Throws<InvalidDataException>(
                () => orphanProject.Validate(),
                "text item on missing text track must be rejected");

            // Negative: transform out of range.
            Throws<InvalidDataException>(
                () => new TimelineClipTransform(1.5, 0.5, 1, 1, 0, 1).Validate(),
                "PositionX above 1 must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineClipTransform(0.5, 0.5, 1, 1, 0, 2).Validate(),
                "Opacity above 1 must be rejected");

            // Transitions: valid + duplicate pair rejection.
            var secondClip = TimelineClip.Create(asset.Id, 10_000_000, 0, 6_000_000);
            var twoClips = upgraded with
            {
                Sequence = upgraded.Sequence! with { VideoClips = [primaryClip, secondClip] },
                SourceAudio = TimelineSourceAudioState.Defaults([primaryClip, secondClip]),
            };
            var transition = TimelineTransition.Create(
                primaryClip.Id, secondClip.Id, TimelineTransitionKind.Cross, 600_000);
            var transitionProject = twoClips with { Transitions = [transition] };
            transitionProject.Validate();
            var transitionPlan = TimelineCompiler.Compile(transitionProject);
            Check(transitionPlan.Transitions.Count == 1
                && transitionPlan.Transitions[0].XfadeName == "dissolve",
                "transition must resolve to its xfade name");

            var duplicate = transition with { Id = TimelineTransition.NewId() };
            Throws<InvalidDataException>(
                () => (twoClips with { Transitions = [transition, duplicate] }).Validate(),
                "duplicate transition pair must be rejected");
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
        catch (Exception error)
        {
            throw new InvalidOperationException($"{message} (wrong exception: {error.GetType().Name})");
        }
        throw new InvalidOperationException(message + " (no exception)");
    }
}

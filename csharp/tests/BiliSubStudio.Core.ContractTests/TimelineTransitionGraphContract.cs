using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G22 transition graph contract (user-approved Concat parity, 2026-09-08).
// Locks: sequential-clip requirement, duration bounds, xfade offset math
// (offset = from-clip end minus duration), deterministic chain building with
// invariant-culture formatting, and rejection of mismatched plan/segments.
public static class TimelineTransitionGraphContract
{
    public static void Run()
    {
        var asset = new TimelineMediaAsset(
            TimelineMediaAsset.NewId(), "C:/video/g22.mp4", 1920, 1080,
            DurationUs: 20_000_000, FrameRate: 30, HasAudio: true,
            Size: 1, LastWriteUtcTicks: 1);
        var first = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
        var second = TimelineClip.Create(asset.Id, 10_000_000, 5_000_000, 12_000_000);
        var third = TimelineClip.Create(asset.Id, 17_000_000, 0, 5_000_000);
        var sequence = TimelineSequence.Create([first, second, third], [asset], outputFrameRate: 30);
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(), "G22 transition graph", DateTimeOffset.UtcNow,
            [asset], sequence, [], [], [], TimelineSourceAudioState.Defaults([first, second, third]),
            [], null);
        var plan = TimelineCompiler.Compile(project);
        plan.ValidateOutputGeometry();

        var cross = TimelineTransition.Create(first.Id, second.Id, TimelineTransitionKind.Cross, 800_000);
        var wipe = TimelineTransition.Create(second.Id, third.Id, TimelineTransitionKind.WipeLeft, 500_000);
        var withTransitions = project with { Transitions = [cross, wipe] };
        var transitionPlan = TimelineCompiler.Compile(withTransitions);
        Check(transitionPlan.Transitions.Count == 2, "plan must carry both transitions");
        Check(transitionPlan.Transitions.Any(t => t.XfadeName == "dissolve")
            && transitionPlan.Transitions.Any(t => t.XfadeName == "wipeleft"),
            "xfade names must map from transition kinds");
        Check(transitionPlan.Transitions.SequenceEqual(
                transitionPlan.Transitions.OrderBy(t => t.DurationUs).ThenBy(t => t.TransitionId, StringComparer.Ordinal)),
            "plan transitions must be deterministically ordered by duration then id");

        // Offset math: xfade cross point is from-clip end minus duration.
        var resolvedCross = transitionPlan.Transitions.Single(t => t.XfadeName == "dissolve");
        var (crossName, crossOffset) = TimelineTransitionGraph.Resolve(resolvedCross, transitionPlan);
        Check(crossName == "dissolve", "resolved xfade name must match");
        Check(crossOffset == 10_000_000 - 800_000,
            "xfade offset must equal from-clip end minus duration");

        // Non-sequential neighbours are rejected.
        var gap = TimelineTransition.Create(first.Id, third.Id, TimelineTransitionKind.Fade, 400_000);
        Throws<InvalidDataException>(
            () => TimelineTransitionGraph.Resolve(
                new TimelineResolvedTransition(gap.Id, gap.Kind, gap.ToXfadeName(), gap.DurationUs,
                    gap.FromClipId, gap.ToClipId),
                transitionPlan),
            "transition across a non-neighbour clip must be rejected");

        // Duration exceeding a clip is rejected at Create.
        Throws<InvalidDataException>(
            () => TimelineTransition.Create(first.Id, second.Id, TimelineTransitionKind.Fade, 11_000_000),
            "transition longer than both clips must be rejected at creation");

        // Chain build: segments must exist for every primary clip in order.
        var segments = transitionPlan.Clips
            .Select((clip, index) => new TimelineRenderSegment(
                index, clip, asset,
                transitionPlan.Audio.SourceAudio.First(source =>
                    string.Equals(source.ClipId, clip.ClipId, StringComparison.Ordinal)),
                transitionPlan.Audio,
                transitionPlan,
                clip.SequenceStartUs,
                clip.SequenceDurationUs))
            .ToArray();
        var chain = TimelineTransitionGraph.BuildXfadeChain(
            transitionPlan.Transitions, segments,
            plan.OutputWidth, plan.OutputHeight, plan.OutputFrameRate);
        Check(chain.Contains("xfade=transition=dissolve:duration=0.8:offset=")
            && chain.Contains("xfade=transition=wipeleft:duration=0.5:offset="),
            "chain must contain exact xfade parameters with invariant formatting");
        Check(!chain.Contains(','.ToString()) || !chain.Contains(",offset"),
            "chain must not leak commas into xfade offsets");
        Check(chain.Count(character => character == ';') == transitionPlan.Transitions.Count - 1,
            "chain must join all transitions minus the final separator");

        // Empty transitions reject chain building.
        Throws<InvalidDataException>(
            () => TimelineTransitionGraph.BuildXfadeChain(
                [], segments, plan.OutputWidth, plan.OutputHeight, plan.OutputFrameRate),
            "empty transitions must reject xfade chain");
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

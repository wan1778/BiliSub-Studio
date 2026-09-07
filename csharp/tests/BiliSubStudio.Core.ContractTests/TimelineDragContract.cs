using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineDragContract
{
    public static async Task RunAsync()
    {
        ReorderTargetsBothDirections();
        TrimSnapsAtOneAndTwoTimes();
        TrimClampsToOneFrame();
        FractionalRatesKeepValidBounds();
        await CommitProposalCreatesOneHistoryEntryAsync();
    }

    private static void ReorderTargetsBothDirections()
    {
        var assetId = TimelineMediaAsset.NewId();
        var first = TimelineClip.Create(assetId, 0, 0, 4_000_000);
        var second = TimelineClip.Create(assetId, 4_000_000, 4_000_000, 8_000_000);
        var third = TimelineClip.Create(assetId, 8_000_000, 8_000_000, 12_000_000);
        var clips = new[] { first, second, third };

        Check(TimelineDragMath.ReorderPosition(clips, first.Id, 11_000_000) == 2,
            "body drag did not target the last position");
        Check(TimelineDragMath.ReorderPosition(clips, third.Id, 1_000_000) == 0,
            "body drag did not target the first position");
        Check(TimelineDragMath.ReorderPosition(clips, second.Id, 6_000_000) == 1,
            "stationary body drag changed position");
    }

    private static void TrimSnapsAtOneAndTwoTimes()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/drag.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var normal = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
        var normalTrim = TimelineDragMath.TrimProposal(
            normal, asset, 30, TimelineDragKind.TrimOut, 3_200_000);
        Check(normalTrim == new TimelineTrimProposal(0, 3_200_000),
            "1x trim did not map the sequence frame to source time");

        var fast = TimelineClip.Create(asset.Id, 0, 10_000_000, 30_000_000, 2);
        var inTrim = TimelineDragMath.TrimProposal(
            fast, asset, 30, TimelineDragKind.TrimIn, 2_500_000);
        var outTrim = TimelineDragMath.TrimProposal(
            fast, asset, 30, TimelineDragKind.TrimOut, 7_500_000);
        Check(inTrim == new TimelineTrimProposal(15_000_000, 30_000_000)
            && outTrim == new TimelineTrimProposal(10_000_000, 25_000_000),
            "2x trim did not preserve source/sequence mapping");
    }

    private static void TrimClampsToOneFrame()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/clamp.mp4", 1280, 720, 10_000_000, 30, hasAudio: false);
        var clip = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
        var left = TimelineDragMath.TrimProposal(
            clip, asset, 30, TimelineDragKind.TrimOut, 0);
        var right = TimelineDragMath.TrimProposal(
            clip, asset, 30, TimelineDragKind.TrimIn, clip.TimelineEndUs);
        var outwardLeft = TimelineDragMath.TrimProposal(
            clip, asset, 30, TimelineDragKind.TrimIn, 0);
        var outwardRight = TimelineDragMath.TrimProposal(
            clip, asset, 30, TimelineDragKind.TrimOut, clip.TimelineEndUs);
        Check(left.SourceOutUs == 33_333 && right.SourceInUs == 9_966_667
            && outwardLeft == new TimelineTrimProposal(0, 10_000_000)
            && outwardRight == new TimelineTrimProposal(0, 10_000_000),
            "trim handles did not preserve a one-frame clip minimum");
    }

    private static async Task CommitProposalCreatesOneHistoryEntryAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);
            var selected = controller.Clips[0];
            var target = TimelineDragMath.ReorderPosition(
                controller.Clips, selected.Id, controller.Clips[^1].TimelineEndUs);

            controller.MoveSelected(target);
            Check(controller.UndoCount == 1 && controller.RedoCount == 0
                && controller.Revision == 1 && controller.IsDirty
                && controller.Clips[^1].Id == selected.Id,
                "one drag proposal did not create exactly one reorder transaction");
            Check(controller.Undo() && controller.Clips[0].Id == selected.Id
                && controller.Project.Items.Single().ClipId == selected.Id,
                "drag undo did not restore clip order and dependency ownership");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void FractionalRatesKeepValidBounds()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/fractional.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var slow = TimelineClip.Create(asset.Id, 0, 0, 10_000_000, .25);
        var narrowed = TimelineDragMath.TrimProposal(
            slow, asset, 30, TimelineDragKind.TrimOut, 0);
        Check(TimelineTime.ClipDurationUs(
                narrowed.SourceInUs, narrowed.SourceOutUs, slow.PlaybackRate) >= 33_333,
            "slow-rate trim produced less than one sequence frame");

        var fractional = TimelineClip.Create(asset.Id, 0, 0, 15_000_001, 1.5);
        var outward = TimelineDragMath.TrimProposal(
            fractional, asset, 30, TimelineDragKind.TrimOut, fractional.TimelineEndUs);
        Check(outward == new TimelineTrimProposal(0, 15_000_001),
            "fractional-rate outward drag changed the source edge by rounding");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}

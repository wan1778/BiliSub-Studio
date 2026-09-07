using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelinePreviewPlaybackContract
{
    public static async Task RunAsync()
    {
        await RendererDelegatesWholeSequenceAsync();
        await RendererDelegatesFinalMixAsync();
        await UnsavedControllerSnapshotDrivesPlanAsync();
    }

    private static async Task RendererDelegatesFinalMixAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var plan = TimelineCompiler.Compile(project);
            var mixer = new RecordingMixer();
            var export = new TimelineExportService(
                AppPaths.FromRoot(root),
                new RecordingSegmentRenderer(),
                new TimelineExportConcatList(AppPaths.FromRoot(root)),
                new RecordingConcatenator(),
                mixer);
            var renderer = new TimelineFfmpegProcessedPreviewRenderer(project, export);
            var output = Path.Combine(root, "mixed-preview.rendering.mp4");
            await using var processes = new OwnedProcessGroup();

            await renderer.RenderAsync(
                new TimelineProcessedPreviewRequest(3, plan, 0, plan.TotalDurationUs),
                output,
                processes,
                CancellationToken.None);
            Check(mixer.Count == 1 && File.Exists(output),
                "processed preview skipped the shared final voice/music mix");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task RendererDelegatesWholeSequenceAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = AppPaths.FromRoot(root);
            var project = Project();
            var plan = TimelineCompiler.Compile(project);
            var segmentRenderer = new RecordingSegmentRenderer();
            var concatenator = new RecordingConcatenator();
            var export = new TimelineExportService(
                paths,
                segmentRenderer,
                new TimelineExportConcatList(paths),
                concatenator,
                new RejectingMixer());
            var renderer = new TimelineFfmpegProcessedPreviewRenderer(project, export);
            var output = Path.Combine(root, "whole-preview.rendering.mp4");
            await using var processes = new OwnedProcessGroup();

            await renderer.RenderAsync(
                new TimelineProcessedPreviewRequest(1, plan, 0, plan.TotalDurationUs),
                output,
                processes,
                CancellationToken.None);
            Check(File.Exists(output) && new FileInfo(output).Length > 0
                && segmentRenderer.Count == plan.Clips.Count
                && concatenator.Count == 1 && processes.ActiveCount == 0,
                "processed preview did not delegate the complete plan to the export graph");

            await ThrowsAsync<InvalidDataException>(() => renderer.RenderAsync(
                new TimelineProcessedPreviewRequest(2, plan, 1, plan.TotalDurationUs - 1),
                Path.Combine(root, "partial.mp4"),
                processes,
                CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task UnsavedControllerSnapshotDrivesPlanAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Project();
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);
            var opened = controller.Clips[0];
            controller.TrimSelected(opened.SourceInUs, opened.SourceOutUs - 2_000_000);

            var previewPlan = TimelineCompiler.Compile(controller.Project);
            var persistedPlan = TimelineCompiler.Compile(
                await store.LoadAsync(project.Id, CancellationToken.None));
            var segmentRenderer = new RecordingSegmentRenderer();
            var paths = AppPaths.FromRoot(root);
            var export = new TimelineExportService(
                paths,
                segmentRenderer,
                new TimelineExportConcatList(paths),
                new RecordingConcatenator(),
                new RejectingMixer());
            var renderer = new TimelineFfmpegProcessedPreviewRenderer(controller.Project, export);
            await using var processes = new OwnedProcessGroup();
            var output = Path.Combine(root, "unsaved-preview.rendering.mp4");
            await renderer.RenderAsync(
                new TimelineProcessedPreviewRequest(4, previewPlan, 0, previewPlan.TotalDurationUs),
                output,
                processes,
                CancellationToken.None);
            Check(controller.IsDirty && controller.Revision == 1
                && previewPlan.TotalDurationUs == persistedPlan.TotalDurationUs - 2_000_000
                && segmentRenderer.Durations.SequenceEqual([previewPlan.TotalDurationUs])
                && File.Exists(output),
                "unsaved controller snapshot did not flow through the processed preview graph");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static TimelineProjectV2 Project()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/preview.mp4", 1280, 720, 20_000_000, 30, hasAudio: true);
        var clip = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
        var sequence = new TimelineSequence([clip], 1280, 720, 30);
        return new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            "G18 preview",
            DateTimeOffset.UtcNow,
            [asset],
            sequence,
            [],
            [],
            [],
            TimelineSourceAudioState.Defaults([clip]),
            [],
            null);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class RecordingSegmentRenderer : ITimelineExportSegmentRenderer
    {
        public int Count { get; private set; }
        public List<long> Durations { get; } = [];

        public async Task RenderSegmentAsync(
            TimelineRenderSegment segment,
            string renderingPath,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            Count++;
            Durations.Add(segment.DurationUs);
            await File.WriteAllTextAsync(renderingPath, "segment", cancellationToken);
        }
    }

    private sealed class RecordingConcatenator : ITimelineExportConcatenator
    {
        public int Count { get; private set; }

        public async Task ConcatAsync(
            TimelineConcatListArtifact list,
            string renderingPath,
            TimelineMuxExpectation expectation,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            Count++;
            await File.WriteAllTextAsync(renderingPath, "processed-preview", cancellationToken);
        }
    }

    private sealed class RejectingMixer : ITimelineFinalMixer
    {
        public Task MixFinalAsync(
            string concatPath,
            TimelineRenderPlan plan,
            string renderingPath,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("voice/music mixer was not expected");
    }

    private sealed class RecordingMixer : ITimelineFinalMixer
    {
        public int Count { get; private set; }

        public async Task MixFinalAsync(
            string concatPath,
            TimelineRenderPlan plan,
            string renderingPath,
            OwnedProcessGroup processes,
            CancellationToken cancellationToken)
        {
            Count++;
            await File.WriteAllTextAsync(renderingPath, "mixed-preview", cancellationToken);
        }
    }
}

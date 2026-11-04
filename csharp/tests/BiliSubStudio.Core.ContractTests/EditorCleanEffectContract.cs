using System.Reflection;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The "clean" effect (object removal, delogo underneath - from the basic
// BCC Wire Remover family): strength is inert like cover
// (EditorCoverEffect.NormalizeStored keeps 0); the export graph uses delogo
// directly on the frame with absolute coordinates + edge clamping >=1px
// (verified with real ffmpeg: x=0 or crop-then-crop-over explodes with
// "Logo area is outside of the frame"); the timeline enable works directly.
// This does not touch the blur/mosaic/cover branch or the current
// handler/geometry.
internal static class EditorCleanEffectContract
{
    public static Task RunAsync()
    {
        PersistenceNormalizesStrengthToZero();
        ExportGraphUsesDirectDelogoWithEdgeClamp();
        WholeVideoHasNoEnableGuard();
        PreviewSliceMatchesExportGeometry();
        TinyRegionRejectedByCommonLaw();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<EditRegion> Normalize(params object[] args)
    {
        var normalizeMethod = typeof(EditorProjectStore).GetMethod(
            "NormalizeRegions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Clean persistence normalization policy");
        return (IReadOnlyList<EditRegion>)normalizeMethod.Invoke(null, args)!;
    }

    // clean shares cover's "strength is not used" table: every input maps to 0.
    private static void PersistenceNormalizesStrengthToZero()
    {
        var normalized = Normalize(
            [new[] { new EditRegion(.1, .2, .3, .25, "clean", 37, true, 0, 10, "clean-persisted") }, 10d, 10d, false])!;
        Equal(EditorCoverEffect.StoredStrength, normalized[0].Strength);
        Equal("clean", normalized[0].Effect);

        // A time-scoped region keeps its window after normalization.
        var scoped = Normalize(
            [new[] { new EditRegion(.1, .2, .3, .25, "clean", 99, false, 2, 8, "clean-scoped") }, 10d, 10d, false])!;
        Equal(EditorCoverEffect.StoredStrength, scoped[0].Strength);
        Equal(2d, scoped[0].Start);
        Equal(8d, scoped[0].End);
    }

    // Export graph: delogo directly on the frame, absolute coordinates, edge clamp.
    // Region (.1,.2,.3,.25) @3840x2160 -> x=384 y=432 w=1152 h=540 (inside the
    // frame, not touching an edge so it stays unchanged).
    private static void ExportGraphUsesDirectDelogoWithEdgeClamp()
    {
        var region = new EditRegion(.1, .2, .3, .25, "clean", EditorCoverEffect.StoredStrength, true, 0, 10, "clean");
        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 3840, 2160, 10, [region]);
        var graph = VideoEditorService.BuildFilter(request);
        True(graph.Contains("delogo=x=384:y=432:w=1152:h=540:show=0", StringComparison.Ordinal),
            "Export Clean lost the direct delogo shape or normalized source geometry");
        True(graph.Contains("show=0", StringComparison.Ordinal), "delogo must keep the show area hidden");
        True(!graph.Contains("drawbox", StringComparison.Ordinal), "clean must not use cover's drawbox");
        True(!graph.Contains("boxblur", StringComparison.Ordinal), "clean must not use blur's boxblur");

        // An edge-touching region (x=0) must be clamped >=1px inside instead of
        // exploding with "Logo area is outside of the frame" (verified with real ffmpeg).
        var edge = new EditRegion(0, 0, .3, .25, "clean", EditorCoverEffect.StoredStrength, true, 0, 10, "edge");
        var edgeGraph = VideoEditorService.BuildFilter(request with { Regions = [edge] });
        True(edgeGraph.Contains("delogo=x=1:y=1:w=", StringComparison.Ordinal),
            "edge-touching clean region must be clamped strictly inside the frame");
    }

    private static void WholeVideoHasNoEnableGuard()
    {
        var whole = new EditRegion(.1, .2, .3, .25, "clean", EditorCoverEffect.StoredStrength, true, 37, 42, "whole");
        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 3840, 2160, 120, [whole]);
        var graph = VideoEditorService.BuildFilter(request);
        True(graph.Contains("delogo=x=", StringComparison.Ordinal), "whole-video clean lost its delogo");
        True(!graph.Contains("enable='between", StringComparison.Ordinal),
            "whole-video clean unexpectedly retained a timed enable guard");

        // A time-scoped region keeps its enable (delogo supports the timeline - verified with real ffmpeg).
        var timed = new EditRegion(.1, .2, .3, .25, "clean", EditorCoverEffect.StoredStrength, false, 2, 8, "timed");
        var timedGraph = VideoEditorService.BuildFilter(request with { Regions = [timed] });
        True(timedGraph.Contains("enable='between(t,2.000,8.000)'", StringComparison.Ordinal),
            "timed clean lost its enable window");
    }

    // The Preview slice must share Export's normalized geometry (or they drift like
    // Cover once did): region (.1,.2,.3,.25) @1280x720 -> x=128 y=144 w=384 h=180.
    private static void PreviewSliceMatchesExportGeometry()
    {
        var region = new EditRegion(.1, .2, .3, .25, "clean", EditorCoverEffect.StoredStrength, true, 0, 10, "clean");
        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 3840, 2160, 10, [region]);
        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Clean processed-preview slice policy");
        var preview = (VideoEditRequest)sliceMethod.Invoke(null, [request, 0d, 10d, 1280, 720])!;
        var previewGraph = VideoEditorService.BuildFilter(preview);
        True(previewGraph.Contains("delogo=x=128:y=144:w=384:h=180:show=0", StringComparison.Ordinal),
            "processed Preview changed Clean geometry from Export");
    }

    // Common rule: regions <2px are still rejected like with every other effect.
    private static void TinyRegionRejectedByCommonLaw()
    {
        var tiny = new EditRegion(.5, .5, .0005, .0005, "clean", EditorCoverEffect.StoredStrength, true, 0, 10, "tiny");
        try
        {
            _ = VideoEditorService.BuildFilter(new VideoEditRequest("input.mp4", ".", "output.mp4", 100, 100, 10, [tiny]));
            throw new InvalidOperationException("tiny clean region was accepted");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}; got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Reflection;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// Effect "clean" (xóa vật thể, ruột delogo — họ BCC Wire Remover cơ bản):
// strength bất động như cover (EditorCoverEffect.NormalizeStored giữ 0);
// graph delogo trực tiếp trên khung bằng tọa độ tuyệt đối + kẹp mép >=1px
// (đã kiểm thật ffmpeg: x=0 hay crop-đè-nguyên-crop nổ "Logo area is outside
// of the frame"); enable timeline chạy trực tiếp. Không đụng nhánh
// blur/mosaic/cover hay handler/geometry hiện tại.
internal static class EditorCleanEffectContract
{
    public static Task RunAsync()
    {
        PersistenceNormalizesStrengthToZero();
        ExportGraphUsesDirectDelogoWithEdgeClamp();
        WholeVideoHasNoEnableGuard();
        PreviewSliceMatchesExportGeometry();
        TinyRegionRejectedByCommonLaw();
        TimelineAcceptsCleanAndLegacyPayloads();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<EditRegion> Normalize(params object[] args)
    {
        var normalizeMethod = typeof(EditorProjectStore).GetMethod(
            "NormalizeRegions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Clean persistence normalization policy");
        return (IReadOnlyList<EditRegion>)normalizeMethod.Invoke(null, args)!;
    }

    // clean đi cùng bảng "không dùng cường độ" với cover: mọi input đều về 0.
    private static void PersistenceNormalizesStrengthToZero()
    {
        var normalized = Normalize(
            [new[] { new EditRegion(.1, .2, .3, .25, "clean", 37, true, 0, 10, "clean-persisted") }, 10d, false])!;
        Equal(EditorCoverEffect.StoredStrength, normalized[0].Strength);
        Equal("clean", normalized[0].Effect);

        // Time-scoped region giữ nguyên cửa sổ sau normalize.
        var scoped = Normalize(
            [new[] { new EditRegion(.1, .2, .3, .25, "clean", 99, false, 2, 8, "clean-scoped") }, 10d, false])!;
        Equal(EditorCoverEffect.StoredStrength, scoped[0].Strength);
        Equal(2d, scoped[0].Start);
        Equal(8d, scoped[0].End);
    }

    // Graph export: delogo trực tiếp trên khung, tọa độ tuyệt đối, kẹp mép.
    // Region (.1,.2,.3,.25) @3840x2160 -> x=384 y=432 w=1152 h=540 (trong khung,
    // không đụng mép nên giữ nguyên).
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

        // Vùng chạm mép (x=0) phải được kẹp vào trong >=1px thay vì nổ
        // "Logo area is outside of the frame" (đã kiểm thật ffmpeg).
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

        // Time-scoped region giữ enable (delogo hỗ trợ timeline — đã kiểm thật).
        var timed = new EditRegion(.1, .2, .3, .25, "clean", EditorCoverEffect.StoredStrength, false, 2, 8, "timed");
        var timedGraph = VideoEditorService.BuildFilter(request with { Regions = [timed] });
        True(timedGraph.Contains("enable='between(t,2.000,8.000)'", StringComparison.Ordinal),
            "timed clean lost its enable window");
    }

    // Preview slice phải cùng hình học normalized với Export (kẻo lệch như
    // Cover từng bị): region (.1,.2,.3,.25) @1280x720 -> x=128 y=144 w=384 h=180.
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

    // Luật chung: vùng <2px vẫn bị từ chối như mọi effect khác.
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

    // Timeline nhận payload clean; id lạ vẫn throw; payload blur/cover cũ vẫn parse.
    private static void TimelineAcceptsCleanAndLegacyPayloads()
    {
        var clean = TimelineEffectSpec.Parse("clean:0.1,0.2,0.3,0.25:0", "item-clean");
        Equal("clean", clean.Effect);
        Equal(.1, clean.X);
        Equal(.25, clean.Height);
        Equal(0, clean.Strength);
        var blur = TimelineEffectSpec.Parse("blur:0.1,0.2,0.3,0.25:12", "item-blur");
        Equal("blur", blur.Effect);
        Equal(12, blur.Strength);
        var cover = TimelineEffectSpec.Parse("cover:0.1,0.2,0.3,0.25:0", "item-cover");
        Equal("cover", cover.Effect);
        try
        {
            _ = TimelineEffectSpec.Parse("wipe:0.1,0.2,0.3,0.25:0", "item-wipe");
            throw new InvalidOperationException("unknown timeline effect id was accepted");
        }
        catch (InvalidOperationException)
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

using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G5 one-clip parity: the V2 one-clip plan maps onto exactly the same
// VideoEditRequest the classic editor builds, so Preview/Export reuse the
// proven VideoEditorService path with no second FFmpeg graph.
internal static class TimelineParityContract
{
    public static Task RunAsync()
    {
        MappingAndRange();
        NonOneClipRejected();
        ServiceTypeShape();
        return Task.CompletedTask;
    }

    private static void MappingAndRange()
    {
        // Asset is 300s; the clip plays sequence 10s..20s from source 100s..110s.
        var asset = TimelineMediaAsset.Create(
            "C:/v/a.mp4", 1920, 1080, TimelineTime.FromSeconds(300), 30, hasAudio: true);
        var plan = TimelineRenderPlan.Empty(asset.Id is { Length: 32 } ? asset.Id : throw new InvalidOperationException("asset id"))
            with
        {
            Clips =
            [
                new TimelineResolvedClip("c".PadLeft(32, 'd'), asset.Id,
                    TimelineTime.FromSeconds(10), TimelineTime.FromSeconds(20),
                    TimelineTime.FromSeconds(100), TimelineTime.FromSeconds(110)),
            ],
        };
        Check(TimelineOneClipParity.IsOneClip(plan), "one-clip plan not recognized");
        var request = TimelineOneClipParity.ToVideoEditRequest(
            plan, asset, outputDirectory: "C:/out", fileName: "a.mp4");

        // Exact classic-shape parity.
        Check(string.Equals(request.InputPath, asset.Path, StringComparison.Ordinal), "parity lost input path");
        Check(request.SourceWidth == 1920 && request.SourceHeight == 1080, "parity lost dimensions");
        Check(Math.Abs(request.Duration - 300) < 1e-9, "parity lost asset duration seconds");
        Check(request.Trim is not null && Math.Abs(request.Trim.Start - 100) < 1e-9
            && Math.Abs(request.Trim.End - 110) < 1e-9, "parity lost source trim");
        Check(request.Regions.Count == 0 && request.Subtitle is null && request.VoiceTrack is null,
            "one-clip parity must not invent edits");
        Check(EditorProjectStore.NormalizeAudio(request.Audio).SourceMode == "keep",
            "parity default audio must be keep");

        // Trim is a real edit: HasTrim must be true and the export range equals
        // the classic trim window.
        Check(EditorProjectStore.HasTrim(request.Trim, request.Duration),
            "parity trim must qualify as an edit for the classic pipeline");
        var (start, duration) = TimelineOneClipParity.ExportRange(plan);
        Check(Math.Abs(start - 100) < 1e-9 && Math.Abs(duration - 10) < 1e-9,
            "export range broke the classic trim identity");

        // Full-clip trim (0..duration) is not an edit, exactly like classic.
        var fullPlan = plan with
        {
            Clips =
            [
                new TimelineResolvedClip("c".PadLeft(32, 'd'), asset.Id,
                    0, TimelineTime.FromSeconds(300), 0, TimelineTime.FromSeconds(300)),
            ],
        };
        var fullRequest = TimelineOneClipParity.ToVideoEditRequest(
            fullPlan, asset, outputDirectory: "C:/out", fileName: "a.mp4");
        Check(!EditorProjectStore.HasTrim(fullRequest.Trim, fullRequest.Duration),
            "full-clip plan must behave like an untrimmed classic request");

        // Preview mapping reuses the same request shape the classic preview
        // path consumes (CreatePreviewSegmentAsync input).
        Check(request.Export is null, "preview request must not smuggle export settings");
    }

    private static void NonOneClipRejected()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/v/a.mp4", 1920, 1080, TimelineTime.FromSeconds(60), 30, hasAudio: true);
        var two = TimelineRenderPlan.Empty(asset.Id) with
        {
            Clips =
            [
                new TimelineResolvedClip("a".PadLeft(32, 'b'), asset.Id, 0, 1, 0, 1),
                new TimelineResolvedClip("c".PadLeft(32, 'd'), asset.Id, 1, 2, 1, 2),
            ],
        };
        Check(!TimelineOneClipParity.IsOneClip(two), "multi-clip plan accepted by parity");
        Throws<InvalidOperationException>(() => TimelineOneClipParity.ExportRange(two),
            "multi-clip export range accepted");
        Throws<InvalidOperationException>(
            () => TimelineOneClipParity.ToVideoEditRequest(two, asset, "C:/out", "a.mp4"),
            "multi-clip parity mapping accepted");
    }

    private static void ServiceTypeShape()
    {
        // V2 keeps its own request/result types and its own preview root; the
        // classic service remains the only FFmpeg graph owner.
        var serviceType = typeof(TimelinePreviewExportService);
        Check(serviceType.Namespace == "BiliSubStudio.Core.Timeline", "V2 service left the V2 namespace");
        var previewMethod = serviceType.GetMethod("CreatePreviewSegmentAsync");
        Check(previewMethod is not null
            && previewMethod.GetParameters().Any(parameter => parameter.ParameterType == typeof(TimelineRenderPlan)),
            "V2 preview must consume the immutable plan");
        var exportMethod = serviceType.GetMethod("ExportAsync");
        Check(exportMethod is not null
            && exportMethod.GetParameters().Any(parameter => parameter.ParameterType == typeof(AppJob)),
            "V2 export must run under the app job model");
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

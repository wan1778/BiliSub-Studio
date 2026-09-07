using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Timeline;

public sealed record TimelineExportRequestV2(
    TimelineRenderPlan Plan,
    string OutputDirectory,
    string FileName,
    EditorExportSettings Export);

// G5 one-clip parity (MASTER_PLAN gate G5). A one-clip V2 plan maps exactly
// onto the classic single-video pipeline: the clip becomes the same
// VideoEditRequest that EditorPage would have built, so Preview and Export
// run the proven VideoEditorService path instead of a second FFmpeg graph.
// Nothing here writes to production state; output paths are caller-supplied.
public static class TimelineOneClipParity
{
    public static bool IsOneClip(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Clips.Count != 1 || plan.Items.Count != 0 || plan.SubtitleCues.Count != 0
            || plan.Audio.VoiceCues.Count != 0 || plan.Audio.Music is not null
            || plan.Audio.SourceAudio.Count != 1)
            return false;
        var clip = plan.Clips[0];
        var source = plan.Audio.SourceAudio[0];
        if (clip.SequenceStartUs != 0 || clip.SequenceEndUs <= clip.SequenceStartUs
            || clip.SourceInUs < 0 || clip.SourceOutUs <= clip.SourceInUs
            || !double.IsFinite(clip.PlaybackRate) || clip.PlaybackRate <= 0)
            return false;
        return clip.SequenceDurationUs
                == TimelineTime.ClipDurationUs(clip.SourceInUs, clip.SourceOutUs, clip.PlaybackRate)
            && Math.Abs(clip.PlaybackRate - 1) <= double.Epsilon
            && source.ClipId == clip.ClipId
            && source.AssetId == clip.AssetId
            && source.SequenceStartUs == clip.SequenceStartUs
            && source.SequenceEndUs == clip.SequenceEndUs
            && source.SourceInUs == clip.SourceInUs
            && source.SourceOutUs == clip.SourceOutUs
            && Math.Abs(source.PlaybackRate - clip.PlaybackRate) <= double.Epsilon
            && source.Gain == 1;
    }

    // Parity mapping. requestedStart/previewWidth/previewHeight mirror
    // VideoEditorService.CreatePreviewSegmentAsync inputs for preview; for
    // export the full clip range is used with source dimensions.
    public static VideoEditRequest ToVideoEditRequest(
        TimelineRenderPlan plan,
        TimelineMediaAsset asset,
        string outputDirectory,
        string fileName,
        EditorExportSettings? export = null,
        double requestedPreviewStart = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsOneClip(plan)) throw new InvalidOperationException("G5 parity chỉ áp dụng cho một clip.");
        var clip = plan.Clips[0];
        if (!string.Equals(clip.AssetId, asset.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Plan clip không khớp asset.");
        if (plan.Audio.SourceAudio[0].HasSourceStream != asset.HasAudio)
            throw new InvalidOperationException("Plan source audio không khớp asset.");

        var trim = new EditorTrimRange(
            TimelineTime.ToSeconds(clip.SourceInUs),
            TimelineTime.ToSeconds(clip.SourceOutUs));

        return new VideoEditRequest(
            InputPath: asset.Path,
            OutputDirectory: outputDirectory,
            FileName: fileName,
            SourceWidth: asset.Width,
            SourceHeight: asset.Height,
            Duration: asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond,
            Regions: [],
            Subtitle: null,
            Audio: EditorAudioSettings.Default,
            VoiceTrack: null,
            MosaicScaleX: 1,
            MosaicScaleY: 1,
            Trim: trim,
            Export: export);
    }

    // Export resolves the exact same segment the classic editor would render
    // for this trim: full clip, full source dimensions, one hard cut.
    public static (double Start, double Duration) ExportRange(TimelineRenderPlan plan)
    {
        if (!IsOneClip(plan)) throw new InvalidOperationException("G5 parity chỉ áp dụng cho một clip.");
        var clip = plan.Clips[0];
        return (TimelineTime.ToSeconds(clip.SourceInUs),
            TimelineTime.ToSeconds(clip.SequenceDurationUs));
    }
}

using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private readonly Dictionary<string, MediaPreviewInfo> _timelineClipMedia = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Danh sách clip của dải ghép hiện tại.</summary>
    internal IReadOnlyList<EditorTimelineClip> TimelineClips => _timelineClips;

    /// <summary>Clip chứa thời gian hiện tại, kèm thời gian tương ứng trong file nguồn của clip.</summary>
    internal (EditorTimelineClip Clip, double SourceTime, double TimelineStart) LocateTimelineClip(double timelineTime)
        => EditorTimelineComposition.Locate(_timelineClips, timelineTime);

    /// <summary>Clip này dùng chung file nguồn với video gốc của dự án (nên mới burn phụ đề được).</summary>
    internal bool IsPrimaryTimelineClip(EditorTimelineClip clip)
        => clip.Id == EditorTimelineComposition.PrimaryClipId
            || (!string.IsNullOrEmpty(_path)
                && string.Equals(clip.SourcePath, _path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Thông tin media của clip; cache theo đường dẫn để không probe lại nhiều lần.</summary>
    internal async Task<MediaPreviewInfo> TimelineClipMediaAsync(
        EditorTimelineClip clip,
        CancellationToken cancellationToken = default)
    {
        if (_timelineClipMedia.TryGetValue(clip.SourcePath, out var cached)) return cached;
        var media = await _application.Media.ProbeAsync(clip.SourcePath, cancellationToken);
        _timelineClipMedia[clip.SourcePath] = media;
        return media;
    }

    /// <summary>
    /// Yêu cầu render cho một clip trên dải ghép. Phụ đề chỉ burn cho clip gốc của
    /// dự án vì timecode phụ đề thuộc về file nguồn đó.
    /// </summary>
    internal async Task<VideoEditRequest> TimelineClipEditRequestAsync(
        EditorTimelineClip clip,
        EditorSubtitleBurn? subtitle,
        CancellationToken cancellationToken = default)
    {
        var media = await TimelineClipMediaAsync(clip, cancellationToken);
        EnsureCurrentSourceFingerprint();
        var burn = IsPrimaryTimelineClip(clip) ? subtitle : null;
        if (burn is not null) EnsureCurrentSubtitleFingerprint();
        return new VideoEditRequest(
            clip.SourcePath,
            _application.Config.OutputDirectory,
            _project?.FileName ?? "video_edited.mp4",
            media.Width,
            media.Height,
            media.Duration,
            _document.Regions.ToArray(),
            burn,
            _audioSettings,
            _voiceTrack,
            Trim: new EditorTrimRange(clip.Start, clip.End));
    }
}

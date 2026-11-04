using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private readonly Dictionary<string, MediaPreviewInfo> _timelineClipMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapImage> _timelineClipThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _timelineClipThumbnailPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current composed timeline clip list.</summary>
    internal IReadOnlyList<EditorTimelineClip> TimelineClips => _timelineClips;

    /// <summary>Clip containing the current time, together with the matching time within that clip's source file.</summary>
    internal (EditorTimelineClip Clip, double SourceTime, double TimelineStart) LocateTimelineClip(double timelineTime)
        => EditorTimelineComposition.Locate(_timelineClips, timelineTime);

    /// <summary>This clip shares a source file with the project's original video (which is why subtitles can be burned).</summary>
    internal bool IsPrimaryTimelineClip(EditorTimelineClip clip)
        => clip.Id == EditorTimelineComposition.PrimaryClipId
            || (!string.IsNullOrEmpty(_path)
                && string.Equals(clip.SourcePath, _path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// This clip points exactly at the project's source file: the project's
    /// already-probed media is the media of that clip, so there is no need to
    /// probe it again.
    /// </summary>
    internal bool IsProjectSourceFile(EditorTimelineClip clip)
        => !string.IsNullOrEmpty(_path)
            && string.Equals(clip.SourcePath, _path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Media of a clip: the probed media of the clip, or the project's
    /// already-probed media when that clip is the project's source file (same
    /// file means same fps, so the timeline frame grid stays consistent).
    /// </summary>
    internal MediaPreviewInfo? TimelineClipMediaOrProject(EditorTimelineClip clip)
    {
        var cached = TimelineClipMediaCached(clip);
        if (cached is not null) return cached;
        return IsProjectSourceFile(clip) ? _media : null;
    }

    /// <summary>
    /// Media of the clip containing a position on the timeline; loads
    /// asynchronously when it is not yet in the cache. Returns null when that
    /// clip's media cannot be read.
    /// </summary>
    /// <param name="timelineTime">Time on the composed timeline (seconds).</param>
    /// <param name="cancellationToken">Cancels the probe operation.</param>
    internal async Task<MediaPreviewInfo?> TimelineClipMediaAtAsync(
        double timelineTime,
        CancellationToken cancellationToken = default)
    {
        if (_timelineClips.Count == 0) return null;
        var (clip, _, _) = LocateTimelineClip(timelineTime);
        var known = TimelineClipMediaOrProject(clip);
        if (known is not null) return known;
        try { return await TimelineClipMediaAsync(clip, cancellationToken); }
        catch (Exception) { return null; }
    }

    /// <summary>Media info of a clip; cached by path so it is not probed repeatedly.</summary>
    internal async Task<MediaPreviewInfo> TimelineClipMediaAsync(
        EditorTimelineClip clip,
        CancellationToken cancellationToken = default)
    {
        if (_timelineClipMedia.TryGetValue(clip.SourcePath, out var cached)) return cached;
        var media = await _application.Media.ProbeAsync(clip.SourcePath, cancellationToken);
        _timelineClipMedia[clip.SourcePath] = media;
        return media;
    }

    /// <summary>Media info of a clip if already probed; null when it is not in the cache yet.</summary>
    internal MediaPreviewInfo? TimelineClipMediaCached(EditorTimelineClip clip)
        => _timelineClipMedia.TryGetValue(clip.SourcePath, out var cached) ? cached : null;

    /// <summary>Pre-reads a clip's media info so edge dragging immediately has the fps and the source file duration.</summary>
    internal void PrefetchTimelineClipMedia(EditorTimelineClip clip)
    {
        if (_timelineClipMedia.ContainsKey(clip.SourcePath)) return;
        _ = PrefetchTimelineClipMediaAsync(clip);
    }

    private async Task PrefetchTimelineClipMediaAsync(EditorTimelineClip clip)
    {
        try { await TimelineClipMediaAsync(clip, CancellationToken.None); }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("timeline-clip-prefetch: " + error);
        }
    }

    /// <summary>Pre-reads a clip's first frame so the video lane shows a filmstrip instead of a flat bar.</summary>
    internal void PrefetchTimelineClipThumbnail(EditorTimelineClip clip)
    {
        var key = TimelineClipThumbnailKey(clip);
        if (_timelineClipThumbnails.ContainsKey(key) || !_timelineClipThumbnailPending.Add(key)) return;
        _ = LoadTimelineClipThumbnailAsync(clip, key);
    }

    /// <summary>Cached filmstrip thumbnail of a clip (null until its first frame has been extracted).</summary>
    private BitmapImage? TimelineClipThumbnail(EditorTimelineClip clip)
        => _timelineClipThumbnails.TryGetValue(TimelineClipThumbnailKey(clip), out var bitmap) ? bitmap : null;

    /// <summary>
    /// Extraction key of a clip's thumbnail: source file identity (size and
    /// write time), frame second and trim in point - a replaced file or a
    /// re-trimmed clip never reuses a stale frame.
    /// </summary>
    private static string TimelineClipThumbnailKey(EditorTimelineClip clip)
    {
        var info = new FileInfo(clip.SourcePath);
        var size = info.Exists ? info.Length : 0;
        var ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        var sourceIn = clip.SourceIn.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return $"{clip.SourcePath}|{size}|{ticks}|{sourceIn}";
    }

    private string TimelineClipThumbnailCachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_application.Paths.Cache, "Editor", "ClipThumbnails", hash + ".jpg");
    }

    private async Task LoadTimelineClipThumbnailAsync(EditorTimelineClip clip, string key)
    {
        try
        {
            var cachePath = TimelineClipThumbnailCachePath(key);
            byte[] bytes;
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                bytes = await File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                if (!_application.Tools.Status.FfmpegReady) return;
                bytes = await _application.Media.GetFrameJpegAsync(
                    clip.SourcePath, Math.Max(0, clip.SourceIn), CancellationToken.None);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, bytes);
            }
            var bitmap = await DecodeTimelineClipThumbnailAsync(bytes);
            _timelineClipThumbnails[key] = bitmap;
            DispatcherQueue.TryEnqueue(QueueTimelineRender);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("timeline-clip-thumb: " + error);
        }
        finally
        {
            _timelineClipThumbnailPending.Remove(key);
        }
    }

    private static async Task<BitmapImage> DecodeTimelineClipThumbnailAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>
    /// Time in the primary clip's source file at the current timeline position.
    /// All project figures (trim, effect regions) live in source-file
    /// coordinates, so the timeline coordinate must be mapped; returns null
    /// when the cursor is on another clip because there is no valid source
    /// coordinate then.
    /// </summary>
    internal double? CurrentPrimarySourceTime()
    {
        if (_media is null) return null;
        var (clip, sourceTime, _) = LocateTimelineClip(Timeline.Value);
        if (!IsPrimaryTimelineClip(clip)) return null;
        return Math.Clamp(sourceTime, 0, _media.Duration);
    }

    /// <summary>
    /// First timeline clip that carries the project's original video. Effect
    /// region times are anchored to this clip's source file.
    /// </summary>
    private (EditorTimelineClip Clip, double TimelineStart)? PrimaryTimelineAnchor()
    {
        if (_timelineClips.Count == 0) return null;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            if (IsPrimaryTimelineClip(_timelineClips[index]))
                return (_timelineClips[index], starts[index]);
        }
        return null;
    }

    /// <summary>
    /// Effect-region time (in the primary clip's source-file coordinates) at a
    /// composed-timeline position. Values past the primary clip's end continue
    /// into the following clips, which lets one region cover segment 2.
    /// </summary>
    private double? RegionTimeForTimeline(double timelineTime)
    {
        if (PrimaryTimelineAnchor() is not { } anchor) return null;
        return anchor.Clip.SourceIn + (timelineTime - anchor.TimelineStart);
    }

    /// <summary>Largest effect-region time the project timeline can represent.</summary>
    private double RegionTimeLimit()
    {
        var duration = Math.Max(0, _media?.Duration ?? 0);
        if (PrimaryTimelineAnchor() is not { } anchor) return duration;
        return Math.Max(duration,
            anchor.Clip.SourceIn + Math.Max(0, TimelineDuration - anchor.TimelineStart));
    }

    /// <summary>Timeline start of one clip; 0 when the clip is not on the strip.</summary>
    private double TimelineStartOfClip(EditorTimelineClip clip)
    {
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            if (ReferenceEquals(_timelineClips[index], clip) || _timelineClips[index].Id == clip.Id)
                return starts[index];
        }
        return 0;
    }

    /// <summary>
    /// Maps the project's effect regions (primary-source time) onto one clip.
    /// A timed region that crosses a clip boundary is clipped to that clip's
    /// source range; a whole-video region stays whole for every clip.
    /// </summary>
    private IReadOnlyList<EditRegion> MapRegionsToTimelineClip(
        EditorTimelineClip clip,
        double timelineStart,
        IEnumerable<EditRegion> regions)
    {
        var mapped = new List<EditRegion>();
        foreach (var region in regions)
        {
            if (MapRegionToTimelineClip(region, clip, timelineStart) is { } one) mapped.Add(one);
        }
        return mapped;
    }

    private EditRegion? MapRegionToTimelineClip(
        EditRegion region,
        EditorTimelineClip clip,
        double timelineStart)
    {
        if (PrimaryTimelineAnchor() is null || clip.Duration <= 0) return null;
        if (region.WholeVideo) return region;
        var clipEnd = timelineStart + clip.Duration;
        var start = Math.Max(timelineStart, TimelineTimeForSource(region.Start));
        var end = Math.Min(clipEnd, TimelineTimeForSource(region.End));
        if (end - start <= .001) return null;
        var sourceStart = Math.Clamp(clip.SourceIn + (start - timelineStart), clip.SourceIn, clip.End);
        var sourceEnd = Math.Clamp(clip.SourceIn + (end - timelineStart), clip.SourceIn, clip.End);
        return sourceEnd - sourceStart <= .001
            ? null
            : region with { WholeVideo = false, Start = sourceStart, End = sourceEnd };
    }

    /// <summary>Regions that apply to one clip, ready for preview/export of that clip's source file.</summary>
    internal IReadOnlyList<EditRegion> RegionsForTimelineClip(EditorTimelineClip clip, double timelineStart)
        => MapRegionsToTimelineClip(clip, timelineStart, _document.Regions);

    /// <summary>
    /// Render request for one clip on the composed timeline. Subtitles and the
    /// voice track only apply to the project's primary clip; effect regions are
    /// mapped to every clip they cover so a region can continue into segment 2.
    /// </summary>
    internal async Task<VideoEditRequest> TimelineClipEditRequestAsync(
        EditorTimelineClip clip,
        EditorSubtitleBurn? subtitle,
        CancellationToken cancellationToken = default)
    {
        var media = await TimelineClipMediaAsync(clip, cancellationToken);
        EnsureCurrentSourceFingerprint();
        var primary = IsPrimaryTimelineClip(clip);
        var burn = primary ? subtitle : null;
        if (burn is not null) EnsureCurrentSubtitleFingerprint();
        var request = new VideoEditRequest(
            clip.SourcePath,
            _application.Config.EditorOutputDirectory,
            _project?.FileName ?? "video_edited.mp4",
            media.Width,
            media.Height,
            media.Duration,
            RegionsForTimelineClip(clip, TimelineStartOfClip(clip)),
            burn,
            _audioSettings,
            primary ? _voiceTrack : null,
            Trim: new EditorTrimRange(clip.SourceIn, clip.End));
        if (!primary)
        {
            if (HasClipSubtitle(clip.Id))
            {
                EnsureClipSubtitleFingerprint(clip);
                request = request with { Subtitle = ClipPreviewSubtitleBurn(clip.Id) };
            }
            var clipVoice = ClipVoiceTrack(clip.Id);
            if (clipVoice is not null) request = request with { VoiceTrack = clipVoice };
        }
        return request;
    }
}

using System.Globalization;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.Timeline;

// G13 full-fidelity export graphs (DEC-023). Effect regions and subtitle
// burns reuse the proven classic builders (VideoEditorService.BuildFilter /
// BuildAss) over a per-segment classic request view expressed in source
// media coordinates; voice cues and sequence-global music are mixed in one
// final pass after the concat. Clip logos and legacy unverified music stay
// rejected: the classic pipeline never rendered logos inside FFmpeg either.
public sealed record TimelineEffectSpec(
    string Effect,
    double X,
    double Y,
    double Width,
    double Height,
    int Strength)
{
    // Documented encoding for V2 effect payloads:
    //   {blur|mosaic|cover|clean}:{x},{y},{w},{h}:{strength}
    // Fractions follow the classic EditRegion convention. Opaque legacy
    // payloads (bare "blur", file paths, ...) are rejected explicitly.
    public static TimelineEffectSpec Parse(string payload, string itemId)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(itemId))
            throw new InvalidOperationException(
                "Timeline effect thiếu payload hoặc ID.");
        payload = payload.Trim();
        var parts = payload.Trim().Split(':');
        if (parts.Length != 3)
            throw new InvalidOperationException(
                $"Timeline effect {itemId} phải có dạng effect:x,y,w,h:strength.");
        var effect = parts[0].Trim().ToLowerInvariant();
        if (effect is not ("blur" or "mosaic" or "cover" or "clean"))
            throw new InvalidOperationException(
                $"Timeline effect {itemId} không hỗ trợ '{parts[0].Trim()}'.");
        var geometry = parts[1].Split(',');
        if (geometry.Length != 4
            || !double.TryParse(geometry[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(geometry[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(geometry[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(geometry[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || !double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || x < 0 || y < 0 || x >= 1 || y >= 1 || width <= 0 || height <= 0
            || x + width > 1.0001 || y + height > 1.0001)
            throw new InvalidOperationException(
                $"Timeline effect {itemId} có geometry không hợp lệ.");
        if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var strength)
            || strength < 0)
            throw new InvalidOperationException(
                $"Timeline effect {itemId} có strength không hợp lệ.");
        return new TimelineEffectSpec(effect, x, y, width, height, strength);
    }
}

public sealed record TimelineLogoSpec(
    string Path,
    double X,
    double Y,
    double Width,
    double Height,
    double Opacity)
{
    // Documented encoding for V2 logo payloads ('|' is forbidden in Windows
    // file names, so drive-letter paths survive splitting):
    //   logo|{path}|{x},{y},{w},{h}|{opacity}
    // Fractions and the 5%-100% opacity range mirror the classic composer.
    public static TimelineLogoSpec Parse(string payload, string itemId)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(itemId))
            throw new InvalidOperationException(
                "Timeline logo thiếu payload hoặc ID.");
        var parts = payload.Trim().Split('|');
        if (parts.Length != 4
            || !string.Equals(parts[0].Trim(), "logo", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Timeline logo {itemId} phải có dạng logo|path|x,y,w,h|opacity.");
        var path = parts[1].Trim();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(path)
            || extension is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidOperationException(
                $"Timeline logo {itemId} chỉ hỗ trợ PNG, JPG hoặc JPEG.");
        var geometry = parts[2].Split(',');
        if (geometry.Length != 4
            || !double.TryParse(geometry[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(geometry[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(geometry[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(geometry[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || !double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || x < 0 || y < 0 || width <= 0 || height <= 0
            || x + width > 1.0001 || y + height > 1.0001)
            throw new InvalidOperationException(
                $"Timeline logo {itemId} có geometry không hợp lệ.");
        if (!double.TryParse(parts[3].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var opacity)
            || !double.IsFinite(opacity) || opacity is < 0.05 or > 1)
            throw new InvalidOperationException(
                $"Timeline logo {itemId} có opacity ngoài 5%-100%.");
        return new TimelineLogoSpec(path, x, y, width, height, opacity);
    }
}

public sealed record TimelineLogoOverlay(
    string Path,
    int X,
    int Y,
    int Width,
    int Height,
    double Opacity,
    double StartSeconds,
    double EndSeconds,
    bool WholeVideo,
    string ItemId);

public static class TimelineRenderCapability
{
    // G13 mux boundary: video, clip effects with documented payloads,
    // subtitle cues, voice cues and at most one typed music track render.
    // G16 adds clip and sequence-global logos with documented payloads.
    // G26 adds clip color grades (deterministic eq/colorbalance chain).
    // G27 adds speed envelopes (piecewise setpts + per-span atempo audio).
    // G28 wires rotation/opacity keyframes; Position/Scale keyframes need
    // the G31 overlay compositing stage and stay rejected. Legacy unverified
    // music items stay rejected.
    public static void EnsureMuxSupported(TimelineRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var envelope in plan.SpeedEnvelopes)
            _ = TimelineSpeedMapping.BuildSpans(envelope);
        foreach (var set in plan.KeyframeSets)
        {
            var wired = set.Tracks.All(track => track.Channel
                is TimelineKeyframeChannel.RotationDegrees
                or TimelineKeyframeChannel.Opacity);
            if (!wired)
                throw new InvalidOperationException(
                    "Timeline FFmpeg mux chỉ hỗ trợ keyframes rotation/opacity (G28); Position/Scale chờ G31.");
            foreach (var track in set.Tracks)
                _ = TimelineKeyframeGraph.BuildExpression(track);
        }
        foreach (var item in plan.Items)
        {
            if (item.Kind == TimelineItemKind.Logo)
                _ = TimelineLogoSpec.Parse(item.Payload, item.ItemId);
            else if (item.Kind == TimelineItemKind.Music)
                throw new InvalidOperationException(
                    "Timeline FFmpeg mux chưa hỗ trợ legacy music item.");
            else if (item.Kind == TimelineItemKind.Effect)
                _ = TimelineEffectSpec.Parse(item.Payload, item.ItemId);
        }
    }
}

public static class TimelineEffectGraph
{
    public const int MaxRegionsPerSegment = 32;

    // Maps clip-local effect items to classic regions in SOURCE media
    // seconds. The composed graph applies effects before trim, so enable
    // windows stay in the source timeline and WholeVideo covers the rendered
    // source window of this segment.
    public static IReadOnlyList<EditRegion> MapEffectsForSegment(TimelineRenderSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var clip = segment.Clip;
        var asset = segment.Asset;
        if (!double.IsFinite(clip.PlaybackRate) || clip.PlaybackRate <= 0)
            throw new InvalidDataException("Timeline segment PlaybackRate không hợp lệ.");
        var assetDurationUs = asset.DurationUs;
        var segSourceInUs = clip.SourceInUs;
        var segSourceOutUs = clip.SourceOutUs;
        var regions = new List<EditRegion>();
        foreach (var item in segment.Plan.Items)
        {
            if (item.Kind != TimelineItemKind.Effect
                || !string.Equals(item.ClipId, clip.ClipId, StringComparison.Ordinal))
                continue;
            var spec = TimelineEffectSpec.Parse(item.Payload, item.ItemId);
            // Resolved items already carry sequence time: convert through the
            // owning clip into source media time (piecewise under an envelope,
            // clamped into the clip window before mapping).
            var sourceStartUs = checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                clip.SpeedSpans, clip.PlaybackRate, Math.Clamp(
                    item.SequenceStartUs - clip.SequenceStartUs, 0, clip.SequenceDurationUs)));
            var sourceEndUs = checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                clip.SpeedSpans, clip.PlaybackRate, Math.Clamp(
                    item.SequenceEndUs - clip.SequenceStartUs, 0, clip.SequenceDurationUs)));
            var startUs = Math.Max(sourceStartUs, Math.Max(0, segSourceInUs));
            var endUs = Math.Min(sourceEndUs, Math.Min(assetDurationUs, segSourceOutUs));
            if (endUs <= startUs)
                continue;
            var pixelWidth = (Math.Min(1, spec.X + spec.Width) * asset.Width) - (spec.X * asset.Width);
            var pixelHeight = (Math.Min(1, spec.Y + spec.Height) * asset.Height) - (spec.Y * asset.Height);
            if (pixelWidth < 2 || pixelHeight < 2)
                throw new InvalidOperationException(
                    $"Timeline effect {item.ItemId} quá nhỏ để render.");
            regions.Add(new EditRegion(
                spec.X, spec.Y, spec.Width, spec.Height,
                spec.Effect, spec.Strength,
                startUs <= segSourceInUs && endUs >= segSourceOutUs,
                ToSeconds(startUs), ToSeconds(endUs), item.ItemId));
        }
        if (regions.Count > MaxRegionsPerSegment)
            throw new InvalidOperationException(
                "Timeline segment vượt quá 32 vùng hiệu ứng.");
        return regions;
    }

    // Maps resolved subtitle cues overlapping this clip to a classic burn in
    // SOURCE media seconds. Clip-local cues convert through the owning clip;
    // sequence-global cues anchor to this segment window. Times stay full
    // (unclipped): the trim stage selects the rendered frames.
    public static EditorSubtitleBurn? MapBurnForSegment(TimelineRenderSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var clip = segment.Clip;
        if (!double.IsFinite(clip.PlaybackRate) || clip.PlaybackRate <= 0)
            throw new InvalidDataException("Timeline segment PlaybackRate không hợp lệ.");
        var cues = new List<EditorSubtitleCue>();
        foreach (var cue in segment.Plan.SubtitleCues
            .OrderBy(cue => cue.SequenceStartUs)
            .ThenBy(cue => cue.CueId, StringComparer.Ordinal))
        {
            var owned = string.Equals(cue.ClipId, clip.ClipId, StringComparison.Ordinal);
            var global = cue.ClipId is null;
            if (!owned && !global)
                continue;
            if (cue.SequenceEndUs <= clip.SequenceStartUs
                || cue.SequenceStartUs >= clip.SequenceEndUs)
                continue;
            var sourceStartUs = checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                clip.SpeedSpans, clip.PlaybackRate, Math.Clamp(
                    cue.SequenceStartUs - clip.SequenceStartUs, 0, clip.SequenceDurationUs)));
            var sourceEndUs = checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                clip.SpeedSpans, clip.PlaybackRate, Math.Clamp(
                    cue.SequenceEndUs - clip.SequenceStartUs, 0, clip.SequenceDurationUs)));
            if (sourceEndUs <= sourceStartUs)
                continue;
            cues.Add(new EditorSubtitleCue(
                cue.CueId,
                (cues.Count + 1).ToString(CultureInfo.InvariantCulture),
                $"{ToSeconds(sourceStartUs):0.000} --> {ToSeconds(sourceEndUs):0.000}",
                ToSeconds(sourceStartUs),
                ToSeconds(sourceEndUs),
                cue.SpokenText,
                cue.SpokenText));
        }
        if (cues.Count == 0)
            return null;
        return new EditorSubtitleBurn(
            cues, EditorSubtitlePlacement.Default, null, false, null);
    }

    public static string BuildAssText(EditorSubtitleBurn burn, int width, int height) =>
        VideoEditorService.BuildAss(
            burn ?? throw new ArgumentNullException(nameof(burn)), width, height);

    // Maps clip-local and sequence-global logo items overlapping this clip to
    // overlays. The overlay stage runs after normalize, so geometry resolves
    // against the OUTPUT frames (presentation space, like the classic export
    // composer); enable windows stay in source seconds so the downstream trim
    // selects the rendered frames.
    public static IReadOnlyList<TimelineLogoOverlay> MapLogosForSegment(TimelineRenderSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var clip = segment.Clip;
        var asset = segment.Asset;
        if (!double.IsFinite(clip.PlaybackRate) || clip.PlaybackRate <= 0)
            throw new InvalidDataException("Timeline segment PlaybackRate không hợp lệ.");
        var outputWidth = segment.Plan.OutputWidth;
        var outputHeight = segment.Plan.OutputHeight;
        segment.Plan.ValidateOutputGeometry();
        if (asset.Width <= 0 || asset.Height <= 0)
            throw new InvalidDataException("Timeline logo cần kích thước asset hợp lệ.");
        var segSourceInUs = clip.SourceInUs;
        var segSourceOutUs = clip.SourceOutUs;
        var overlays = new List<TimelineLogoOverlay>();
        foreach (var item in segment.Plan.Items
            .OrderBy(item => item.SequenceStartUs)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal))
        {
            if (item.Kind != TimelineItemKind.Logo)
                continue;
            var owned = string.Equals(item.ClipId, clip.ClipId, StringComparison.Ordinal);
            var global = item.ClipId is null;
            if (!owned && !global)
                continue;
            if (item.SequenceEndUs <= clip.SequenceStartUs
                || item.SequenceStartUs >= clip.SequenceEndUs)
                continue;
            var spec = TimelineLogoSpec.Parse(item.Payload, item.ItemId);
            if (!File.Exists(spec.Path))
                throw new InvalidDataException($"Thiếu file logo {item.ItemId} cho final render.");
            var startUs = Math.Max(
                checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                    clip.SpeedSpans, clip.PlaybackRate,
                    Math.Max(0, item.SequenceStartUs - clip.SequenceStartUs))),
                Math.Max(0, segSourceInUs));
            var endUs = Math.Min(
                checked(clip.SourceInUs + TimelineSpeedMapping.SequenceToSourceUs(
                    clip.SpeedSpans, clip.PlaybackRate,
                    Math.Min(item.SequenceEndUs - clip.SequenceStartUs, clip.SequenceDurationUs))),
                Math.Min(asset.DurationUs, segSourceOutUs));
            if (endUs <= startUs)
                continue;
            var width = Math.Max(2, (int)Math.Round(spec.Width * outputWidth));
            var height = Math.Max(2, (int)Math.Round(spec.Height * outputHeight));
            if (Math.Round(spec.Width * outputWidth) < 2
                || Math.Round(spec.Height * outputHeight) < 2)
                throw new InvalidOperationException(
                    $"Timeline logo {item.ItemId} quá nhỏ để render.");
            overlays.Add(new TimelineLogoOverlay(
                System.IO.Path.GetFullPath(spec.Path),
                Math.Clamp((int)Math.Round(spec.X * outputWidth), 0, Math.Max(0, outputWidth - width)),
                Math.Clamp((int)Math.Round(spec.Y * outputHeight), 0, Math.Max(0, outputHeight - height)),
                width,
                height,
                Math.Clamp(spec.Opacity, .05, 1),
                ToSeconds(startUs),
                ToSeconds(endUs),
                startUs <= segSourceInUs && endUs >= segSourceOutUs,
                item.ItemId));
        }
        return overlays;
    }

    // Composes the classic effect/subtitle graph ahead of trim/normalize, or
    // returns null when the segment carries no visual edits (G12 fast path).
    // Effects run at source resolution with source-timeline enable windows;
    // trim, rate, scale and ASS timestamps stay consistent downstream.
    public static string? BuildComposedVideoFilter(TimelineRenderSegment segment, string? assPath)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var regions = MapEffectsForSegment(segment);
        var burn = MapBurnForSegment(segment);
        if (regions.Count == 0 && burn is null)
            return null;
        if (burn is not null && string.IsNullOrWhiteSpace(assPath))
            throw new InvalidOperationException(
                "Timeline subtitle burn cần file ASS sidecar.");
        var view = new VideoEditRequest(
            segment.Asset.Path,
            string.Empty,
            string.Empty,
            segment.Asset.Width,
            segment.Asset.Height,
            ToSeconds(segment.Asset.DurationUs),
            regions,
            burn,
            null,
            null,
            1,
            1,
            null,
            null);
        var body = VideoEditorService.BuildFilter(view, assPath);
        const string terminal = "[vout]";
        if (!body.EndsWith(terminal, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Classic filter shape đã đổi; không nối được Timeline graph.");
        return body[..^terminal.Length];
    }

    // Concat-timeline position of a sequence timestamp. Clip gaps collapse:
    // the muxed timeline joins clips end-to-start.
    public static long ConcatTimeUs(long sequenceUs, IReadOnlyList<TimelineResolvedClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var cursor = 0L;
        foreach (var clip in clips.OrderBy(clip => clip.SequenceStartUs))
        {
            if (sequenceUs < clip.SequenceStartUs)
                return cursor;
            if (sequenceUs <= clip.SequenceEndUs)
                return checked(cursor + (sequenceUs - clip.SequenceStartUs));
            cursor = checked(cursor + clip.SequenceDurationUs);
        }
        return cursor;
    }

    private static double ToSeconds(long microseconds) =>
        microseconds / (double)TimelineTime.MicrosecondsPerSecond;
}

public interface ITimelineFinalMixer
{
    Task MixFinalAsync(
        string concatPath,
        TimelineRenderPlan plan,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken);
}

public sealed class TimelineFfmpegFinalMixer : ITimelineFinalMixer
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly ProcessRunner _processes = new();

    public TimelineFfmpegFinalMixer(string ffmpegPath, string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        _ffmpeg = ffmpegPath.Trim();
        _ffprobe = ffprobePath.Trim();
    }

    public async Task MixFinalAsync(
        string concatPath,
        TimelineRenderPlan plan,
        string renderingPath,
        OwnedProcessGroup processes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concatPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        ArgumentNullException.ThrowIfNull(processes);
        plan.ValidateOutputGeometry();
        if (plan.Audio.VoiceCues.Count == 0 && plan.Audio.Music is null)
            throw new InvalidOperationException(
                "Timeline final mix cần ít nhất một voice cue hoặc music track.");
        if (!File.Exists(concatPath))
            throw new InvalidDataException("Timeline concat output cho final mix không tồn tại.");
        var arguments = BuildFinalMixArguments(concatPath, plan, renderingPath);
        var result = await _processes.RunAsync(
            _ffmpeg, arguments, cancellationToken, owner: processes).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg final mix thất bại: {result.StandardError.Trim()} " +
                $"[argv: {string.Join(" ", arguments)}]");
        var probe = await _processes.RunAsync(
            _ffprobe,
            TimelineFfmpegArguments.BuildProbe(renderingPath),
            cancellationToken,
            owner: processes).ConfigureAwait(false);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFprobe final mix thất bại: {probe.StandardError.Trim()}");
        TimelineMuxValidation.ValidateProbe(
            probe.StandardOutput,
            TimelineMuxValidation.BuildExpectation(plan) with { ExpectAudio = true });
    }

    public static IReadOnlyList<string> BuildFinalMixArguments(
        string concatPath,
        TimelineRenderPlan plan,
        string renderingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concatPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingPath);
        plan.ValidateOutputGeometry();
        var orderedClips = plan.Clips.OrderBy(clip => clip.SequenceStartUs).ToArray();
        var totalSeconds = (orderedClips.Sum(clip => clip.SequenceDurationUs)
            / (double)TimelineTime.MicrosecondsPerSecond)
            .ToString("0.000000", CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", concatPath,
        };
        var voiceGroups = plan.Audio.VoiceCues
            .GroupBy(voice => voice.AudioAssetId, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in voiceGroups)
        {
            var master = group.First();
            if (!File.Exists(master.Path))
                throw new InvalidDataException("Thiếu voice master cho final mix.");
            arguments.AddRange(["-i", master.Path]);
        }
        var music = plan.Audio.Music;
        if (music is not null)
        {
            if (!File.Exists(music.Path))
                throw new InvalidDataException("Thiếu music asset cho final mix.");
            arguments.AddRange(["-i", music.Path]);
        }

        var chains = new List<string>();
        var mixed = new List<string>();
        var concatHasAudio = plan.Audio.SourceAudio
            .Any(source => source.HasSourceStream && source.Gain > 0);
        if (concatHasAudio)
        {
            chains.Add("[0:a]aresample=48000,asetpts=PTS-STARTPTS[base]");
            mixed.Add("[base]");
        }
        else
        {
            arguments.AddRange(["-f", "lavfi", "-i",
                $"anullsrc=r=48000:cl=stereo:d={totalSeconds}"]);
        }
        var inputIndex = 1;
        foreach (var group in voiceGroups)
        {
            var master = group.First();
            var ordered = group.OrderBy(voice => voice.SequenceStartUs).ToArray();
            var branches = new List<string>();
            if (ordered.Length == 1)
            {
                branches.Add($"[{inputIndex}:a]");
            }
            else
            {
                var split = string.Concat(
                    Enumerable.Range(0, ordered.Length).Select(index => $"[vm{inputIndex}_{index}]"));
                chains.Add($"[{inputIndex}:a]asplit={ordered.Length}{split}");
                branches.AddRange(Enumerable.Range(0, ordered.Length)
                    .Select(index => $"[vm{inputIndex}_{index}]"));
            }
            for (var index = 0; index < ordered.Length; index++)
            {
                var voice = ordered[index];
                var startSeconds = (voice.SourceStartFrame / (double)voice.SampleRate)
                    .ToString("0.000000", CultureInfo.InvariantCulture);
                var durationSeconds = (voice.FrameCount / (double)voice.SampleRate)
                    .ToString("0.000000", CultureInfo.InvariantCulture);
                var delayMs = (int)Math.Round(
                    TimelineEffectGraph.ConcatTimeUs(voice.SequenceStartUs, orderedClips) / 1000d,
                    MidpointRounding.AwayFromZero);
                var label = $"[vc{inputIndex}_{index}]";
                // G29: per-cue voice filter composes right after the trim
                // stage; neutral/absent filters keep the chain byte-identical.
                var voiceFilter = TimelineVoiceFilterGraph.BuildChain(
                    voice.Filter, voice.SampleRate);
                var filterPrefix = voiceFilter is null ? string.Empty : voiceFilter + ",";
                chains.Add($"{branches[index]}atrim=start={startSeconds}:duration={durationSeconds}," +
                    $"asetpts=PTS-STARTPTS,{filterPrefix}adelay={delayMs}:all=1," +
                    $"volume={voice.Gain.ToString("0.###", CultureInfo.InvariantCulture)}{label}");
                mixed.Add(label);
            }
            inputIndex++;
        }
        if (music is not null)
        {
            var rate = music.SampleRate;
            if (rate <= 0)
                throw new InvalidDataException("Timeline music sample rate không hợp lệ.");
            var startSeconds = (music.SourceStartFrame / (double)rate)
                .ToString("0.000000", CultureInfo.InvariantCulture);
            var durationSeconds = (music.FrameCount / (double)rate)
                .ToString("0.000000", CultureInfo.InvariantCulture);
            var delayMs = (int)Math.Round(
                TimelineEffectGraph.ConcatTimeUs(music.SequenceStartUs, orderedClips) / 1000d,
                MidpointRounding.AwayFromZero);
            chains.Add($"[{inputIndex}:a]atrim=start={startSeconds}:duration={durationSeconds}," +
                $"asetpts=PTS-STARTPTS,adelay={delayMs}:all=1," +
                $"volume={music.Gain.ToString("0.###", CultureInfo.InvariantCulture)}[mc]");
            mixed.Add("[mc]");
            inputIndex++;
        }
        if (!concatHasAudio)
            mixed.Insert(0, $"[{inputIndex}:a]");
        if (mixed.Count < 2)
            throw new InvalidOperationException("Timeline final mix cần ít nhất hai nguồn audio.");
        chains.Add($"{string.Concat(mixed)}amix=inputs={mixed.Count}:" +
            "duration=longest:dropout_transition=0:normalize=0[aout]");
        arguments.AddRange(["-filter_complex", string.Join(";", chains),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
            "-ar", "48000", "-ac", "2", "-movflags", "+faststart", renderingPath]);
        return arguments;
    }
}

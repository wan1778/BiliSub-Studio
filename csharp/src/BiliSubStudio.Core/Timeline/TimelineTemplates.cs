namespace BiliSubStudio.Core.Timeline;

// G30 timeline templates (user-approved Concat parity, 2026-09-08). Pure
// project factories: each preset returns a validated empty project carrying
// the preset's track layout (names, kinds, z-order, gains) and sequence
// defaults so new projects start consistent with the Concat-style editor.
// Factories perform no IO and take explicit caller inputs; media stays empty
// until the user imports clips.
public enum TimelineTemplateKind
{
    Default = 0,
    PiPOverlay = 1,
    TextOverVideo = 2,
    Reaction = 3,
}

public static class TimelineTemplates
{
    public const int MaxTrackNameLength = 80;

    // Track layout is ordered bottom-to-top in the editor; ZIndex mirrors it
    // (higher = closer to the top). Video presets keep the primary track free
    // of clips: TimelineSequence.Create requires at least one clip, so video
    // presets are completed by the caller's first imported clip.
    public static TimelineProjectV2 Create(
        TimelineTemplateKind kind,
        string name,
        int outputWidth,
        int outputHeight,
        double outputFrameRate)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new InvalidDataException("Tên project Timeline phải có từ 1 đến 120 ký tự.");
        if (outputWidth <= 0 || outputHeight <= 0)
            throw new InvalidDataException("Timeline template cần output resolution hợp lệ.");
        if (!double.IsFinite(outputFrameRate) || outputFrameRate <= 0)
            throw new InvalidDataException("Timeline template cần FPS dương hữu hạn.");
        var tracks = kind switch
        {
            TimelineTemplateKind.PiPOverlay => Tracks(
                ("Music", TimelineTrackKind.Music, 0, -6),
                ("Overlay", TimelineTrackKind.VideoOverlay, 10, 0),
                ("Text", TimelineTrackKind.Text, 20, 0)),
            TimelineTemplateKind.TextOverVideo => Tracks(
                ("Music", TimelineTrackKind.Music, 0, -6),
                ("Text", TimelineTrackKind.Text, 20, 0)),
            TimelineTemplateKind.Reaction => Tracks(
                ("Audio", TimelineTrackKind.Audio, 0, 0),
                ("Reaction Cam", TimelineTrackKind.VideoOverlay, 10, 0),
                ("Text", TimelineTrackKind.Text, 20, 0)),
            TimelineTemplateKind.Default => Tracks(
                ("Music", TimelineTrackKind.Music, 0, -6),
                ("Text", TimelineTrackKind.Text, 20, 0)),
            _ => throw new InvalidDataException("Timeline template kind không hợp lệ."),
        };
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            name.Trim(),
            DateTimeOffset.UtcNow,
            [],
            null,
            [],
            [],
            [],
            [],
            [],
            null,
            tracks,
            [],
            [],
            [],
            [],
            []);
        project.Validate();
        return project;
    }

    public static IReadOnlyList<TimelineTrackV2> Tracks(
        params (string Name, TimelineTrackKind Kind, int ZIndex, double GainDb)[] layout)
    {
        if (layout.Length == 0)
            throw new InvalidDataException("Timeline template cần ít nhất một track.");
        if (layout.Length > TimelineTrackV2KindCount)
            throw new InvalidDataException("Timeline template vượt số track cho phép.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return layout.Select(slot =>
        {
            var track = new TimelineTrackV2(
                TimelineTrackV2.NewId(), slot.Kind, ValidateTrackName(slot.Name),
                slot.ZIndex, IsVisible: true, IsMuted: false, IsLocked: false,
                slot.GainDb, Clips: []);
            if (!ids.Add(track.Id))
                throw new InvalidDataException("Timeline template sinh track ID trùng.");
            return track;
        }).ToArray();
    }

    private const int TimelineTrackV2KindCount = 64;

    private static string ValidateTrackName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxTrackNameLength)
            throw new InvalidDataException("Timeline track name phải 1..80 ký tự.");
        return normalized;
    }
}

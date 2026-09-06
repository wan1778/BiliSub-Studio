using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Timeline;

public enum TimelineMigrationWarning
{
    RegionsFlattened,
    SubtitleDropped,
    VoiceDropped,
    TrimRoundedToMicroseconds,
}

public sealed record TimelineMigrationReport(
    TimelineProjectV2 Project,
    IReadOnlyList<TimelineMigrationWarning> Warnings,
    EditorSourceFingerprint SourceFingerprint);

// G4 migration copy (MASTER_PLAN gate G4, DEC-004/005). Reads a schema-7
// EditorProject record and writes a separate one-clip V2 copy. The source
// project, its schema-7 file and the media file are never written: this
// class performs no IO on them and accepts only already-loaded state.
public static class TimelineMigrator
{
    public static TimelineMigrationReport MigrateSchema7(
        EditorProject source,
        string videoPath,
        int width,
        int height,
        double durationSeconds,
        long size,
        long lastWriteUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Schema != 7)
            throw new InvalidDataException("Timeline migration chỉ nhận project schema 7.");
        if (source.Source is null)
            throw new InvalidDataException("Timeline migration cần source fingerprint.");
        if (width <= 0 || height <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new InvalidDataException("Timeline migration cần media probe hợp lệ.");
        if (size < 0 || lastWriteUtcTicks < 0)
            throw new InvalidDataException("Timeline migration fingerprint không hợp lệ.");

        var warnings = new List<TimelineMigrationWarning>();
        if (source.Regions.Count > 0)
            warnings.Add(TimelineMigrationWarning.RegionsFlattened);

        // The migrated V2 copy is a pure one-clip view of the source video.
        // Schema-7 trim and audio settings are intentionally NOT copied yet:
        // they need explicit product decisions (G5 parity + PDEC-001/002) and
        // must never silently reshape the migrated source range.
        var durationUs = TimelineTime.FromSeconds(durationSeconds);
        var asset = new TimelineMediaAsset(
            TimelineMediaAsset.NewId(),
            videoPath,
            width,
            height,
            durationUs,
            FrameRate: 1,
            HasAudio: false,
            size,
            lastWriteUtcTicks);

        var clip = new TimelineClip(
            TimelineClip.NewId(),
            asset.Id,
            TimelineStartUs: 0,
            SourceInUs: 0,
            SourceOutUs: durationUs,
            PlaybackRate: 1.0);
        var sequence = TimelineSequence.Create([clip], [asset], outputFrameRate: 30);

        if (source.Subtitle is not null)
            warnings.Add(TimelineMigrationWarning.SubtitleDropped);
        if (source.Tts is not null)
            warnings.Add(TimelineMigrationWarning.VoiceDropped);

        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            string.IsNullOrWhiteSpace(source.Name) ? "Timeline copy" : source.Name,
            DateTimeOffset.UtcNow,
            [asset],
            sequence,
            []);
        project.Validate();

        return new TimelineMigrationReport(project, warnings, source.Source);
    }
}

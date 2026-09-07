using System.Globalization;

namespace BiliSubStudio.Core.Timeline;

// G24 speed envelope (user-approved Concat parity, 2026-09-08). One clip owns
// at most one envelope that replaces its constant-rate mapping: the clip's
// stored sequence window stays fixed (layout, overlap and reflow math are
// untouched) while segments redistribute playback rate inside it. Source
// coverage must reproduce the clip's source range exactly so no frame is
// dropped or repeated at segment boundaries. The clip's nominal PlaybackRate
// remains layout bookkeeping; all mapping math flows through the envelope.
public sealed record TimelineSpeedSegment(long SequenceDurationUs, double Rate)
{
    public const double MinRate = 0.1;
    public const double MaxRate = 100;

    public long SourceDurationUs
    {
        get
        {
            ValidateRate();
            var source = Math.Round(SequenceDurationUs * Rate, MidpointRounding.AwayFromZero);
            if (source <= 0 || source > long.MaxValue)
                throw new InvalidDataException("Timeline speed segment source duration vượt phạm vi int64 microseconds.");
            return (long)source;
        }
    }

    public void Validate()
    {
        if (SequenceDurationUs <= 0)
            throw new InvalidDataException("Timeline speed segment duration phải dương.");
        ValidateRate();
        _ = SourceDurationUs;
    }

    private void ValidateRate()
    {
        if (!double.IsFinite(Rate) || Rate < MinRate || Rate > MaxRate)
            throw new InvalidDataException(
                $"Timeline speed segment rate phải trong {MinRate}..{MaxRate}.");
    }
}

public sealed record TimelineSpeedEnvelope(
    string ClipId,
    IReadOnlyList<TimelineSpeedSegment> Segments)
{
    public const int MaxSegments = 64;

    public static TimelineSpeedEnvelope Create(
        string clipId,
        IReadOnlyList<TimelineSpeedSegment> segments,
        long clipSequenceDurationUs,
        long clipSourceDurationUs)
    {
        var envelope = new TimelineSpeedEnvelope(clipId, segments);
        envelope.Validate(clipSequenceDurationUs, clipSourceDurationUs);
        return envelope;
    }

    public long SequenceDurationUs => Segments.Sum(segment => segment.SequenceDurationUs);

    public long SourceCoverageUs => Segments.Sum(segment => segment.SourceDurationUs);

    // Full validation needs the owning clip: the envelope must tile the clip's
    // stored sequence window exactly and consume its source range exactly.
    public void Validate(long clipSequenceDurationUs, long clipSourceDurationUs)
    {
        if (ClipId is not { Length: 32 } || !ClipId.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline speed envelope ClipId không hợp lệ.");
        ArgumentNullException.ThrowIfNull(Segments);
        if (Segments.Count == 0)
            throw new InvalidDataException("Timeline speed envelope cần ít nhất một segment.");
        if (Segments.Count > MaxSegments)
            throw new InvalidDataException("Timeline speed envelope vượt 64 segment.");
        foreach (var segment in Segments)
            segment.Validate();
        if (SequenceDurationUs != clipSequenceDurationUs)
            throw new InvalidDataException(
                "Timeline speed envelope không phủ đúng độ dài sequence của clip.");
        if (SourceCoverageUs != clipSourceDurationUs)
            throw new InvalidDataException(
                "Timeline speed envelope không phủ đúng source range của clip.");
    }

    // Clip-local piecewise mapping: source offsets are relative to the clip's
    // SourceInUs, sequence offsets relative to TimelineStartUs. Segment
    // boundaries resolve by equality so ends land on exact cursors.
    public long SourceToSequenceUs(long sourceOffsetUs)
    {
        if (sourceOffsetUs < 0)
            throw new InvalidDataException("Timeline speed envelope source offset không được âm.");
        var sourceCursor = 0L;
        var sequenceCursor = 0L;
        foreach (var segment in Segments)
        {
            var sourceLength = segment.SourceDurationUs;
            if (sourceOffsetUs <= checked(sourceCursor + sourceLength))
            {
                var offset = Math.Round(
                    checked(sourceOffsetUs - sourceCursor) / segment.Rate,
                    MidpointRounding.AwayFromZero);
                return checked(sequenceCursor + (long)offset);
            }
            sourceCursor = checked(sourceCursor + sourceLength);
            sequenceCursor += segment.SequenceDurationUs;
        }
        throw new InvalidDataException("Timeline speed envelope source offset vượt source range.");
    }

    public long SequenceToSourceUs(long sequenceOffsetUs)
    {
        if (sequenceOffsetUs < 0)
            throw new InvalidDataException("Timeline speed envelope sequence offset không được âm.");
        var sourceCursor = 0L;
        var sequenceCursor = 0L;
        foreach (var segment in Segments)
        {
            if (sequenceOffsetUs <= checked(sequenceCursor + segment.SequenceDurationUs))
            {
                var offset = Math.Round(
                    checked(sequenceOffsetUs - sequenceCursor) * segment.Rate,
                    MidpointRounding.AwayFromZero);
                return checked(sourceCursor + (long)offset);
            }
            sourceCursor = checked(sourceCursor + segment.SourceDurationUs);
            sequenceCursor += segment.SequenceDurationUs;
        }
        throw new InvalidDataException("Timeline speed envelope sequence offset vượt sequence range.");
    }

    // Shared atempo decomposition (single filter only spans 0.5..100). Must
    // stay byte-identical with the mux renderer's historical output.
    public static string BuildAtempoChain(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0)
            throw new InvalidDataException("PlaybackRate phải là số dương hữu hạn.");
        var parts = new List<string>();
        var remaining = rate;
        while (remaining > 100)
        {
            parts.Add("atempo=100");
            remaining /= 100;
        }
        while (remaining is > 0 and < 0.5)
        {
            parts.Add("atempo=0.5");
            remaining *= 2;
        }
        parts.Add("atempo=" + remaining.ToString("0.###", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }
}

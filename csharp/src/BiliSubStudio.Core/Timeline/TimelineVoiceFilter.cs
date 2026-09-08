namespace BiliSubStudio.Core.Timeline;

// G29 voice filters (user-approved Concat parity, 2026-09-08). One optional
// filter per voice cue, mapped deterministically onto FFmpeg asetrate/atempo
// pitch-tempo pairs plus a formant-ish EQ shelf. PitchShift in semitones
// (-12..12) resamples the stream (asetrate) and compensates duration with a
// matching atempo; TempoScale (0.5..2) is applied additionally without pitch
// change (atempo only); Equalize maps to bass/treble EQ gains in dB. The
// neutral filter renders no filters so unfiltered cues stay byte-identical.
public sealed record TimelineVoiceFilter(
    double PitchShiftSemitones,
    double TempoScale,
    double BassGainDb,
    double TrebleGainDb)
{
    public const double MaxPitchSemitones = 12;
    public const double MinTempoScale = 0.5;
    public const double MaxTempoScale = 2;
    public const double MaxEqGainDb = 12;

    public static TimelineVoiceFilter Neutral { get; } = new(0, 1, 0, 0);

    public void Validate()
    {
        if (!double.IsFinite(PitchShiftSemitones)
            || Math.Abs(PitchShiftSemitones) > MaxPitchSemitones)
            throw new InvalidDataException(
                $"Timeline voice filter pitch phải trong -{MaxPitchSemitones}..{MaxPitchSemitones} semitone.");
        if (!double.IsFinite(TempoScale) || TempoScale is < MinTempoScale or > MaxTempoScale)
            throw new InvalidDataException(
                $"Timeline voice filter tempo phải trong {MinTempoScale}..{MaxTempoScale}.");
        if (!double.IsFinite(BassGainDb) || Math.Abs(BassGainDb) > MaxEqGainDb)
            throw new InvalidDataException(
                $"Timeline voice filter bass phải trong -{MaxEqGainDb}..{MaxEqGainDb} dB.");
        if (!double.IsFinite(TrebleGainDb) || Math.Abs(TrebleGainDb) > MaxEqGainDb)
            throw new InvalidDataException(
                $"Timeline voice filter treble phải trong -{MaxEqGainDb}..{MaxEqGainDb} dB.");
    }

    public bool IsNeutral =>
        PitchShiftSemitones == 0 && TempoScale == 1 && BassGainDb == 0 && TrebleGainDb == 0;
}

public static class TimelineVoiceFilterGraph
{
    // Deterministic FFmpeg audio filter chain for one cue's filter. The pitch
    // stage resamples (asetrate = rate * semitone factor) and restores the
    // duration with the inverse atempo so cue timing stays exact; the tempo
    // stage adds a plain atempo; the EQ stage maps gains to bass/treble
    // filters. Returns null for neutral/absent filters.
    public static string? BuildChain(TimelineVoiceFilter? filter, int sampleRate)
    {
        if (filter is null || filter.IsNeutral)
            return null;
        filter.Validate();
        if (sampleRate <= 0)
            throw new InvalidDataException("Timeline voice filter sample rate không hợp lệ.");
        var parts = new List<string>();
        var combinedTempo = filter.TempoScale;
        if (filter.PitchShiftSemitones != 0)
        {
            var factor = Math.Pow(2, filter.PitchShiftSemitones / 12.0);
            var shiftedRate = Fmt(sampleRate * factor);
            parts.Add($"asetrate={shiftedRate}");
            parts.Add("aresample=48000");
            // asetrate shifts duration by 1/factor; the inverse atempo restores it.
            combinedTempo /= factor;
            if (combinedTempo < TimelineVoiceFilter.MinTempoScale
                || combinedTempo > TimelineVoiceFilter.MaxTempoScale)
                throw new InvalidDataException(
                    "Timeline voice filter pitch+tempo vượt phạm vi atempo 0.5..2.");
            parts.Add($"atempo={Fmt(combinedTempo)}");
        }
        else if (filter.TempoScale != 1)
        {
            parts.Add($"atempo={Fmt(filter.TempoScale)}");
        }
        if (filter.BassGainDb != 0)
            parts.Add($"bass=g={Fmt(filter.BassGainDb)}:f=110:w=0.6");
        if (filter.TrebleGainDb != 0)
            parts.Add($"treble=g={Fmt(filter.TrebleGainDb)}:f=3800:w=0.6");
        return string.Join(",", parts);
    }

    private static string Fmt(double value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}

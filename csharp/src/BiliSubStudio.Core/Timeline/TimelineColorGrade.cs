namespace BiliSubStudio.Core.Timeline;

// G26 color grade (user-approved Concat parity, 2026-09-08). One clip owns at
// most one grade. All parameters are bounded, finite doubles in documented
// FFmpeg units (eq filters): exposure/brightness in -1..1, contrast/saturation
// as multipliers 0..3, temperature/tint map to the colorbalance channels in
// -1..1. A neutral grade (all defaults) is valid but renders as no-op.
public sealed record TimelineColorGrade(
    double Exposure,
    double Contrast,
    double Saturation,
    double Temperature,
    double Tint)
{
    public static TimelineColorGrade Neutral { get; } = new(0, 1, 1, 0, 0);

    public void Validate()
    {
        if (!double.IsFinite(Exposure) || Exposure < -1 || Exposure > 1)
            throw new InvalidDataException("Timeline color grade Exposure phải trong -1..1.");
        if (!double.IsFinite(Contrast) || Contrast < 0 || Contrast > 3)
            throw new InvalidDataException("Timeline color grade Contrast phải trong 0..3.");
        if (!double.IsFinite(Saturation) || Saturation < 0 || Saturation > 3)
            throw new InvalidDataException("Timeline color grade Saturation phải trong 0..3.");
        if (!double.IsFinite(Temperature) || Temperature < -1 || Temperature > 1)
            throw new InvalidDataException("Timeline color grade Temperature phải trong -1..1.");
        if (!double.IsFinite(Tint) || Tint < -1 || Tint > 1)
            throw new InvalidDataException("Timeline color grade Tint phải trong -1..1.");
    }

    public bool IsNeutral =>
        Exposure == 0 && Contrast == 1 && Saturation == 1 && Temperature == 0 && Tint == 0;
}

public static class TimelineColorGradeGraph
{
    // Deterministic eq/colorbalance chain in source coordinates (the composed
    // graph runs before trim, matching G16 effects and subtitle burn stages).
    // Neutral grades return null so segments without visual edits keep the
    // G12 fast path byte-identical. Invariant culture everywhere.
    public static string? BuildFilter(TimelineColorGrade? grade)
    {
        if (grade is null || grade.IsNeutral)
            return null;
        grade.Validate();
        var parts = new List<string>();
        if (grade.Exposure != 0)
            parts.Add($"eq=brightness={Fmt(grade.Exposure)}");
        if (grade.Contrast != 1)
            parts.Add($"eq=contrast={Fmt(grade.Contrast)}");
        if (grade.Saturation != 1)
            parts.Add($"eq=saturation={Fmt(grade.Saturation)}");
        if (grade.Temperature != 0 || grade.Tint != 0)
        {
            // Temperature moves blue (+) / red (-); tint moves magenta (+) /
            // green (-). Each maps onto both relevant colorbalance channels.
            var blue = Fmt(grade.Temperature);
            var magenta = Fmt(grade.Tint);
            parts.Add(
                $"colorbalance=rs={Fmt(-grade.Temperature / 2)}:bs={blue}" +
                $":rm={Fmt(-grade.Tint / 2)}:bm={magenta}");
        }
        return string.Join(",", parts);
    }

    private static string Fmt(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

// Clip-scoped grade reference persisted in the project's ColorGrades array.
public sealed record TimelineColorGradeSpec(string ClipId, TimelineColorGrade Grade);

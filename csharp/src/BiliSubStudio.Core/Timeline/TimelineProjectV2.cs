namespace BiliSubStudio.Core.Timeline;

// G2 shell model. G3 owns assets/clips/tracks/time mapping; this shell only
// proves a separate identity and persistence path from schema-7.
public sealed record TimelineProjectV2(
    string Id,
    string Name,
    DateTimeOffset UpdatedUtc)
{
    public const int Schema = 1;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Length != 32
            || !id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline V2 project ID không hợp lệ.");
    }

    public static string ValidateName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 120)
            throw new InvalidDataException("Tên project Timeline phải có từ 1 đến 120 ký tự.");
        return normalized;
    }
}

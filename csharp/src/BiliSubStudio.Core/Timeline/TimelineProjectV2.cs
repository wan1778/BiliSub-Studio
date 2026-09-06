namespace BiliSubStudio.Core.Timeline;

// G3 project (MASTER_PLAN section 7), extended with persisted items in G7.
// Schema 2 upgrades safely with an empty item set; schema 1 remains rejected
// because output geometry/FPS cannot be inferred. Project ID stays stable
// across asset relinks (DEC-012; relink itself arrives later).
public sealed record TimelineProjectV2(
    string Id,
    string Name,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<TimelineMediaAsset> Assets,
    TimelineSequence? Sequence,
    IReadOnlyList<TimelineItem> Items)
{
    public const int Schema = 3;

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

    public void Validate()
    {
        ValidateId(Id);
        ValidateName(Name);
        ArgumentNullException.ThrowIfNull(Assets);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets)
        {
            asset.Validate();
            if (!assetIds.Add(asset.Id))
                throw new InvalidDataException("Timeline project chứa asset trùng ID.");
        }
        Sequence?.Validate(Assets);
        ArgumentNullException.ThrowIfNull(Items);
        if (Sequence is null)
        {
            if (Items.Count != 0)
                throw new InvalidDataException("Timeline project không có sequence nhưng chứa item.");
        }
        else
        {
            TimelineItemOps.ValidateAll(Items, Sequence.VideoClips);
        }
    }
}

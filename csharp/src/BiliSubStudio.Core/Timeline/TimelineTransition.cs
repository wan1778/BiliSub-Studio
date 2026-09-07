namespace BiliSubStudio.Core.Timeline;

// G22 transitions (user-approved Concat parity, 2026-09-08). A transition
// bridges two clips on the same visual track and consumes overlap time; the
// render graph maps it to an FFmpeg xfade variant.
public enum TimelineTransitionKind
{
    Fade = 0,
    Cross = 1,
    WipeLeft = 2,
    WipeRight = 3,
    SlideLeft = 4,
    SlideRight = 5,
}

public sealed record TimelineTransition(
    string Id,
    string FromClipId,
    string ToClipId,
    TimelineTransitionKind Kind,
    long DurationUs,
    string? ParamsJson)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public const long MinDurationUs = 80_000;   // 80 ms
    public const long MaxDurationUs = 3_000_000; // 3 s

    public static TimelineTransition Create(
        string fromClipId,
        string toClipId,
        TimelineTransitionKind kind,
        long durationUs,
        string? paramsJson = null)
    {
        var transition = new TimelineTransition(
            NewId(), fromClipId, toClipId, kind, durationUs, paramsJson);
        transition.Validate();
        return transition;
    }

    public void Validate()
    {
        if (Id is not { Length: 32 } || !Id.All(character => char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Timeline transition ID không hợp lệ.");
        if (string.IsNullOrWhiteSpace(FromClipId) || string.IsNullOrWhiteSpace(ToClipId))
            throw new InvalidDataException("Timeline transition thiếu clip đầu/cuối.");
        if (string.Equals(FromClipId, ToClipId, StringComparison.Ordinal))
            throw new InvalidDataException("Timeline transition không được nối clip với chính nó.");
        if (!Enum.IsDefined(typeof(TimelineTransitionKind), Kind))
            throw new InvalidDataException("Timeline transition kind không hợp lệ.");
        if (DurationUs < MinDurationUs || DurationUs > MaxDurationUs)
            throw new InvalidDataException(
                $"Timeline transition duration phải trong {MinDurationUs / 1000}..{MaxDurationUs / 1000} ms.");
        if (ParamsJson is { Length: > 512 })
            throw new InvalidDataException("Timeline transition ParamsJson vượt 512 ký tự.");
    }

    public string ToXfadeName() => Kind switch
    {
        TimelineTransitionKind.Fade => "fade",
        TimelineTransitionKind.Cross => "dissolve",
        TimelineTransitionKind.WipeLeft => "wipeleft",
        TimelineTransitionKind.WipeRight => "wiperight",
        TimelineTransitionKind.SlideLeft => "slideleft",
        TimelineTransitionKind.SlideRight => "slideright",
        _ => throw new InvalidOperationException(
            $"Timeline transition kind {Kind} không có xfade tương ứng."),
    };
}

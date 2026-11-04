namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Policy for saving the playhead position of the timeline (the playhead belongs to the
/// project, like FilmCraft): when the user scrubs, the position is written after a
/// debounce delay so that a single mouse drag hits the disk exactly once; while
/// playing, the disk is never written continuously, only once when playback stops.
///
/// This class is pure: it never touches a clock and never touches the disk. UI only
/// supplies the timestamp and the playback state; the decision to write or not
/// belongs to Core.
/// </summary>
public sealed class EditorPlayheadSavePolicy
{
    /// <summary>Default delay before writing the playhead position (milliseconds).</summary>
    public const double DefaultDebounceMilliseconds = 800d;

    private readonly double _debounceMilliseconds;
    private double _pendingSinceMilliseconds;
    private bool _pending;

    /// <summary>
    /// Creates a playhead save policy with a debounce delay.
    /// </summary>
    /// <param name="debounceMilliseconds">Delay in milliseconds; an invalid value falls back to the default.</param>
    public EditorPlayheadSavePolicy(double debounceMilliseconds = DefaultDebounceMilliseconds)
    {
        _debounceMilliseconds = double.IsFinite(debounceMilliseconds) && debounceMilliseconds > 0
            ? debounceMilliseconds
            : DefaultDebounceMilliseconds;
    }

    /// <summary>Debounce delay (milliseconds) between two writes.</summary>
    public double DebounceMilliseconds => _debounceMilliseconds;

    /// <summary>Whether a playhead position is waiting to be written.</summary>
    public bool IsPending => _pending;

    /// <summary>
    /// Records one user scrub: the deadline is pushed forward on every scrub so
    /// that a single mouse drag hits the disk exactly once.
    /// </summary>
    /// <param name="nowMilliseconds">Current timestamp (milliseconds).</param>
    public void Schedule(double nowMilliseconds)
    {
        _pendingSinceMilliseconds = nowMilliseconds;
        _pending = true;
    }

    /// <summary>
    /// Whether the debounce delay has elapsed so the write can happen: while playing it
    /// never is (no continuous disk writes), and the debounce window must elapse
    /// since the last scrub.
    /// </summary>
    /// <param name="nowMilliseconds">Current timestamp (milliseconds).</param>
    /// <param name="isPlaying">Whether the app is playing the preview.</param>
    public bool IsDue(double nowMilliseconds, bool isPlaying) =>
        _pending
        && !isPlaying
        && nowMilliseconds - _pendingSinceMilliseconds >= _debounceMilliseconds;

    /// <summary>
    /// Takes the write permission once the delay has elapsed: returns false and keeps the
    /// queue intact while playing, so the position is written when playback stops.
    /// </summary>
    /// <param name="nowMilliseconds">Current timestamp (milliseconds).</param>
    /// <param name="isPlaying">Whether the app is playing the preview.</param>
    public bool TryConsume(double nowMilliseconds, bool isPlaying)
    {
        if (!IsDue(nowMilliseconds, isPlaying)) return false;
        _pending = false;
        return true;
    }

    /// <summary>Drops the pending playhead save (switching project or closing the editor).</summary>
    public void Clear()
    {
        _pending = false;
        _pendingSinceMilliseconds = 0;
    }
}

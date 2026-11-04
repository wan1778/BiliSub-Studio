using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Per-project playhead save (following FilmCraft): when the user scrubs the position is written
// after a debounce wait, so a single mouse drag writes to disk only once; while playing it must never
// write to disk continuously, it writes once when playback stops.
internal static class EditorPlayheadSaveContract
{
    public static Task RunAsync()
    {
        var policy = new EditorPlayheadSavePolicy();
        Equal(EditorPlayheadSavePolicy.DefaultDebounceMilliseconds, policy.DebounceMilliseconds);
        True(policy.DebounceMilliseconds >= 500d && policy.DebounceMilliseconds <= 2000d,
            "the playhead debounce window must stay between 500ms and 2s");
        True(!policy.IsPending, "a fresh policy has nothing to write");

        // Without scrubbing nothing is written.
        True(!policy.TryConsume(60_000d, isPlaying: false),
            "an editor that was never seeked must not write the playhead");

        // One scrub: not enough wait yet, nothing written.
        policy.Schedule(1_000d);
        True(policy.IsPending, "a seek must queue a playhead write");
        True(!policy.IsDue(1_000d + policy.DebounceMilliseconds - 1d, isPlaying: false),
            "the playhead write must wait for the whole debounce window");
        True(policy.IsDue(1_000d + policy.DebounceMilliseconds, isPlaying: false),
            "the playhead write must be due once the debounce window elapsed");
        True(!policy.TryConsume(1_000d + policy.DebounceMilliseconds - 1d, isPlaying: false),
            "the playhead write must wait for the whole debounce window");
        True(policy.IsPending, "a playhead write that is not due yet must stay queued");

        // Enough wait: written exactly once and then dequeued.
        True(policy.TryConsume(1_000d + policy.DebounceMilliseconds, isPlaying: false),
            "a seek must be written once the debounce window elapsed");
        True(!policy.IsPending, "a written playhead must leave the queue");
        True(!policy.TryConsume(1_000d + policy.DebounceMilliseconds, isPlaying: false),
            "one seek must write the playhead only once");

        // Dragging the mouse (consecutive scrubs) writes only once.
        policy.Schedule(5_000d);
        policy.Schedule(5_100d);
        policy.Schedule(5_200d);
        True(!policy.TryConsume(5_200d + policy.DebounceMilliseconds - 1d, isPlaying: false),
            "a slider drag must not write the playhead before the drag settles");
        True(policy.TryConsume(5_200d + policy.DebounceMilliseconds, isPlaying: false),
            "a slider drag must write the playhead once the drag settles");
        True(!policy.IsPending, "a slider drag must leave the queue empty");

        // While playing: the playhead advances every frame so nothing may be written to disk, however
        // long we have waited.
        policy.Schedule(10_000d);
        True(!policy.IsDue(10_000d + 60_000d, isPlaying: true),
            "playback must never make the playhead write due");
        True(!policy.TryConsume(10_000d + 60_000d, isPlaying: true),
            "playback must never write the playhead");
        True(policy.IsPending, "the playhead position must stay queued while playing");

        // Playback stopped: written exactly once, not repeatedly.
        policy.Schedule(70_000d);
        True(policy.TryConsume(70_000d + policy.DebounceMilliseconds, isPlaying: false),
            "stopping playback must write the queued playhead once");
        True(!policy.TryConsume(70_000d + policy.DebounceMilliseconds + 5_000d, isPlaying: false),
            "stopping playback must not write the playhead again");

        // Switching project: drop the queue, do not write the old project's position.
        policy.Schedule(80_000d);
        policy.Clear();
        True(!policy.IsPending, "clearing the policy must drop the queued playhead");
        True(!policy.TryConsume(90_000d, isPlaying: false),
            "a cleared policy must not write the playhead");

        // An invalid wait uses the default; a valid wait uses exactly that value.
        var fallback = new EditorPlayheadSavePolicy(double.NaN);
        Equal(EditorPlayheadSavePolicy.DefaultDebounceMilliseconds, fallback.DebounceMilliseconds);
        var negative = new EditorPlayheadSavePolicy(-1d);
        Equal(EditorPlayheadSavePolicy.DefaultDebounceMilliseconds, negative.DebounceMilliseconds);
        var custom = new EditorPlayheadSavePolicy(1_200d);
        Equal(1_200d, custom.DebounceMilliseconds);

        return Task.CompletedTask;
    }

    private static void Equal(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-9) throw new InvalidOperationException($"expected {expected} but got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

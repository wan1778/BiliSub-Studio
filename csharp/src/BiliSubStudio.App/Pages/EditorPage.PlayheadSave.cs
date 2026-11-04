using System.Diagnostics;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// Saves the playhead position per project (following FilmCraft): when the
/// user scrubs, the position to save is marked, and the disk write is
/// debounced so one mouse drag writes only once. While playing, the playhead
/// advances every frame, so successive disk writes must never happen: the
/// pending position is written one time when playback stops.
/// </summary>
public sealed partial class EditorPage
{
    private readonly EditorPlayheadSavePolicy _playheadSavePolicy = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _playheadSaveTimer;
    private string? _playheadSaveProjectId;

    /// <summary>
    /// The user scrubbed (dragged the slider, clicked the ruler/lane, selected
    /// a cue, pressed arrow keys): mark the playhead position that must be
    /// saved per project. The timer owns the debounce, so one scrub writes to
    /// disk exactly once.
    /// </summary>
    private void SchedulePlayheadSave()
    {
        if (_project is null) return;
        _playheadSaveProjectId = _project.Id;
        _playheadSavePolicy.Schedule(PlayheadSaveNowMilliseconds());
        // While playing, do not write to disk: the playhead advances every
        // frame, so wait until playback stops to write once.
        if (_playback.IsPlaying) return;
        RestartPlayheadSaveTimer();
    }

    /// <summary>
    /// The preview just stopped: write out the pending playhead position (one
    /// time) because playing does not permit successive disk writes.
    /// </summary>
    internal void NotifyEditorPlaybackStopped()
    {
        if (_project is null) return;
        _playheadSaveProjectId = _project.Id;
        _playheadSavePolicy.Schedule(PlayheadSaveNowMilliseconds());
        RestartPlayheadSaveTimer();
    }

    private void RestartPlayheadSaveTimer()
    {
        var timer = EnsurePlayheadSaveTimer();
        timer.Stop();
        timer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer EnsurePlayheadSaveTimer()
    {
        if (_playheadSaveTimer is not null) return _playheadSaveTimer;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(EditorPlayheadSavePolicy.DefaultDebounceMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += PlayheadSaveTimer_Tick;
        _playheadSaveTimer = timer;
        return timer;
    }

    private async void PlayheadSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_playheadSavePolicy.IsPending) return;
        // While playing: do not write to disk, wait until playback stops (NotifyEditorPlaybackStopped).
        if (_playback.IsPlaying) return;
        if (!_playheadSavePolicy.TryConsume(PlayheadSaveNowMilliseconds(), isPlaying: false))
        {
            // The timer fired earlier than the wait interval: retry later instead of losing the position.
            RestartPlayheadSaveTimer();
            return;
        }
        try { await FlushPlayheadSaveAsync(); }
        catch (Exception error) { ReportEditorCommandFailure("lưu vị trí playhead", error); }
    }

    /// <summary>
    /// Writes the playhead position into the project once the wait interval has
    /// elapsed and playback is not running. The save runs in the background
    /// through the same project save gate, so it does not overwrite other
    /// changes and does not freeze the UI.
    /// </summary>
    private async Task FlushPlayheadSaveAsync()
    {
        if (_project is null) return;
        // The project changed while waiting: the old project's position must not
        // overwrite the new project.
        if (!string.Equals(_project.Id, _playheadSaveProjectId, StringComparison.Ordinal))
        {
            ClearPlayheadSave();
            return;
        }
        await SaveProjectNowAsync();
    }

    /// <summary>Drops the pending playhead save (opening another project or closing the editor).</summary>
    private void ClearPlayheadSave()
    {
        _playheadSavePolicy.Clear();
        _playheadSaveProjectId = null;
        _playheadSaveTimer?.Stop();
    }

    private static double PlayheadSaveNowMilliseconds() =>
        Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
}

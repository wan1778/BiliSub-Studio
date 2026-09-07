namespace BiliSubStudio.Core.Timeline;

// G15 editing controller. Owns one open V2 project, the selected clip, an
// atomic undo/redo history and the dirty flag. Every edit runs the pure
// TimelineAudioDependencyOps first and commits history only on success, so a
// rejected edit (stale voice, crossing subtitle, ...) leaves state, history,
// selection and dirty flag untouched. Persistence goes only through the
// injected V2 store; the controller performs no IO itself.
public sealed class TimelineEditingController
{
    private readonly TimelineProjectStoreV2 _store;
    private readonly TimelineAudioEditHistory _history = new();
    private TimelineProjectV2? _project;
    private TimelineAudioEditState? _state;
    private string? _selectedClipId;
    private bool _dirty;

    public TimelineEditingController(TimelineProjectStoreV2 store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool HasOpenProject => _project is not null && _state is not null;

    public TimelineProjectV2 Project => _project
        ?? throw new InvalidOperationException("Chưa mở project Timeline.");

    public IReadOnlyList<TimelineClip> Clips =>
        _state?.Timeline.Clips ?? throw new InvalidOperationException("Chưa mở project Timeline.");

    public string? SelectedClipId => _selectedClipId;

    public bool IsDirty => _dirty;

    public int UndoCount => _history.UndoCount;

    public int RedoCount => _history.RedoCount;

    public async Task OpenAsync(string id, CancellationToken cancellationToken)
    {
        TimelineProjectV2.ValidateId(id);
        var project = await _store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (project.Sequence is null)
            throw new InvalidOperationException(
                "Project Timeline chưa có sequence để edit (copy từ project classic trước).");
        _project = project;
        _state = ToAudioState(project);
        _selectedClipId = _state.Timeline.Clips.FirstOrDefault()?.Id;
        _dirty = false;
    }

    public void SelectClip(string? clipId)
    {
        var state = CurrentState();
        if (clipId is null)
        {
            _selectedClipId = null;
            return;
        }
        if (!state.Timeline.Clips.Any(clip =>
                string.Equals(clip.Id, clipId, StringComparison.Ordinal)))
            throw new InvalidDataException("Timeline clip không tồn tại để chọn.");
        _selectedClipId = clipId;
    }

    public void TrimSelected(
        long newSourceInUs,
        long newSourceOutUs,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject) =>
        Apply(state => TimelineAudioDependencyOps.Trim(
            state, SelectedClipOrThrow(), newSourceInUs, newSourceOutUs, voicePolicy));

    public void SplitSelected(
        long cutSequenceUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject) =>
        Apply(state => TimelineAudioDependencyOps.Split(
            state, SelectedClipOrThrow(), cutSequenceUs, subtitlePolicy, voicePolicy));

    public void DeleteSelected() =>
        Apply(state => TimelineAudioDependencyOps.Delete(state, SelectedClipOrThrow()));

    public void MoveSelected(int newPosition) =>
        Apply(state => TimelineAudioDependencyOps.Reorder(state, SelectedClipOrThrow(), newPosition));

    public bool Undo()
    {
        var state = CurrentState();
        if (_history.UndoCount == 0)
            return false;
        _state = _history.Undo(state);
        _project = ApplyToProject(_project!, _state);
        FixSelection();
        _dirty = true;
        return true;
    }

    public bool Redo()
    {
        var state = CurrentState();
        if (_history.RedoCount == 0)
            return false;
        _state = _history.Redo(state);
        _project = ApplyToProject(_project!, _state);
        FixSelection();
        _dirty = true;
        return true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var project = Project;
        project = project with { UpdatedUtc = DateTimeOffset.UtcNow };
        await _store.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        _project = project;
        _dirty = false;
    }

    private void Apply(Func<TimelineAudioEditState, TimelineAudioEditState> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var before = CurrentState();
        var after = operation(before);
        _history.Commit(before, after);
        _state = after;
        _project = ApplyToProject(_project!, after);
        FixSelection();
        _dirty = true;
    }

    private string SelectedClipOrThrow() =>
        _selectedClipId
        ?? throw new InvalidOperationException("Chưa chọn clip Timeline để edit.");

    private TimelineAudioEditState CurrentState() =>
        _state ?? throw new InvalidOperationException("Chưa mở project Timeline.");

    private void FixSelection()
    {
        var clips = _state!.Timeline.Clips;
        if (_selectedClipId is not null && clips.Any(clip =>
                string.Equals(clip.Id, _selectedClipId, StringComparison.Ordinal)))
            return;
        _selectedClipId = clips.FirstOrDefault()?.Id;
    }

    private static TimelineAudioEditState ToAudioState(TimelineProjectV2 project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sequence = project.Sequence
            ?? throw new InvalidOperationException("Project Timeline chưa có sequence để edit.");
        return TimelineAudioEditState.Create(
            TimelineEditState.Create(sequence.VideoClips, project.Items),
            project.SubtitleCues,
            project.AudioAssets,
            project.SourceAudio,
            project.VoiceCues,
            project.Music);
    }

    private static TimelineProjectV2 ApplyToProject(
        TimelineProjectV2 project, TimelineAudioEditState state)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(state);
        var sequence = project.Sequence
            ?? throw new InvalidOperationException("Project Timeline chưa có sequence để edit.");
        var rebuilt = new TimelineSequence(
            state.Timeline.Clips,
            sequence.OutputWidth,
            sequence.OutputHeight,
            sequence.OutputFrameRate);
        rebuilt.Validate(project.Assets);
        var updated = project with
        {
            Sequence = rebuilt,
            Items = state.Timeline.Items,
            SubtitleCues = state.SubtitleCues,
            AudioAssets = state.AudioAssets,
            SourceAudio = state.SourceAudio,
            VoiceCues = state.VoiceCues,
            Music = state.Music,
        };
        updated.Validate();
        return updated;
    }
}

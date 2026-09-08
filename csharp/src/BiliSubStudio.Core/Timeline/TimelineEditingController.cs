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
    private TimelineAudioEditHistory _history = new();
    private TimelineProjectV2? _project;
    private TimelineAudioEditState? _state;
    private TimelineAudioEditState? _savedState;
    private string? _selectedClipId;
    private bool _dirty;
    private long _revision;

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

    public long Revision => _revision;

    public async Task OpenAsync(
        string id,
        CancellationToken cancellationToken,
        bool discardUnsavedChanges = false)
    {
        TimelineProjectV2.ValidateId(id);
        if (_dirty && !discardUnsavedChanges)
            throw new InvalidOperationException(
                "Project Timeline hiện tại có thay đổi chưa lưu; cần lưu hoặc bỏ thay đổi trước khi mở project khác.");
        var openProjectId = _project?.Id;
        var revision = _revision;
        var project = await _store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (_revision != revision
            || !string.Equals(_project?.Id, openProjectId, StringComparison.Ordinal)
            || (_dirty && !discardUnsavedChanges))
            throw new InvalidOperationException(
                "Project Timeline đã thay đổi trong lúc mở project khác; thao tác mở đã bị hủy.");
        if (project.Sequence is null)
            throw new InvalidOperationException(
                "Project Timeline chưa có sequence để edit (copy từ project classic trước).");
        var state = ToAudioState(project);
        _project = project;
        _state = state;
        _savedState = state;
        _selectedClipId = state.Timeline.Clips.FirstOrDefault()?.Id;
        _history = new TimelineAudioEditHistory();
        _dirty = false;
        _revision = 0;
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
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject)
    {
        if (!Enum.IsDefined(voicePolicy))
            throw new InvalidDataException("Timeline voice boundary policy không hợp lệ.");
        var clipId = SelectedClipOrThrow();
        var clip = CurrentState().Timeline.Clips.Single(item =>
            string.Equals(item.Id, clipId, StringComparison.Ordinal));
        if (clip.SourceInUs == newSourceInUs && clip.SourceOutUs == newSourceOutUs)
            return;
        Apply(state => TimelineAudioDependencyOps.Trim(
            state, clipId, newSourceInUs, newSourceOutUs, voicePolicy));
    }

    public void SplitSelected(
        long cutSequenceUs,
        TimelineSubtitleSplitPolicy subtitlePolicy = TimelineSubtitleSplitPolicy.Reject,
        TimelineVoiceBoundaryPolicy voicePolicy = TimelineVoiceBoundaryPolicy.Reject) =>
        Apply(state => TimelineAudioDependencyOps.Split(
            state, SelectedClipOrThrow(), cutSequenceUs, subtitlePolicy, voicePolicy));

    public void DeleteSelected() =>
        Apply(state => TimelineAudioDependencyOps.Delete(state, SelectedClipOrThrow()));

    // G31: track visibility/mute toggles ride the same atomic undo
    // history; the audio edit state carries the multi-track arrays.
    public void SetTrackState(string trackId, bool? isVisible, bool? isMuted)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            throw new InvalidDataException("Timeline track ID không hợp lệ.");
        Apply(state =>
        {
            var tracks = state.Tracks ?? [];
            var index = tracks.ToList().FindIndex(track =>
                string.Equals(track.Id, trackId, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidDataException("Timeline track không tồn tại để đổi trạng thái.");
            var updated = tracks[index] with
            {
                IsVisible = isVisible ?? tracks[index].IsVisible,
                IsMuted = isMuted ?? tracks[index].IsMuted,
            };
            var rebuilt = tracks.ToArray();
            rebuilt[index] = updated;
            return state with { Tracks = rebuilt };
        });
    }

    // G31: add or replace a text item on an existing Text track with atomic
    // undo; text must reference a real Text track (project validation pins it).
    public void UpsertTextItem(TimelineTextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        Apply(state =>
        {
            var tracks = state.Tracks ?? [];
            if (!tracks.Any(track => track.Kind == TimelineTrackKind.Text
                && string.Equals(track.Id, item.TrackId, StringComparison.Ordinal)))
                throw new InvalidDataException("Timeline text item trỏ track text không tồn tại.");
            var items = state.TextItems ?? [];
            var existing = items.ToList().FindIndex(candidate =>
                string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
            var rebuilt = items.ToArray();
            if (existing >= 0)
                rebuilt[existing] = item;
            else
                rebuilt = [.. items, item];
            return state with { TextItems = rebuilt };
        });
    }

    public void DeleteTextItem(string textItemId)
    {
        if (string.IsNullOrWhiteSpace(textItemId))
            throw new InvalidDataException("Timeline text item ID không hợp lệ.");
        Apply(state =>
        {
            var items = state.TextItems ?? [];
            if (!items.Any(candidate =>
                    string.Equals(candidate.Id, textItemId, StringComparison.Ordinal)))
                throw new InvalidDataException("Timeline text item không tồn tại để xóa.");
            return state with { TextItems = items
                .Where(candidate => !string.Equals(candidate.Id, textItemId, StringComparison.Ordinal))
                .ToArray() };
        });
    }

    public void MoveSelected(int newPosition)
    {
        var clipId = SelectedClipOrThrow();
        var clips = CurrentState().Timeline.Clips;
        if (newPosition < 0 || newPosition >= clips.Count)
            throw new InvalidDataException("Vị trí đổi thứ tự clip không hợp lệ.");
        var currentPosition = clips.ToList().FindIndex(clip =>
            string.Equals(clip.Id, clipId, StringComparison.Ordinal));
        if (currentPosition == newPosition)
            return;
        Apply(state => TimelineAudioDependencyOps.Reorder(state, clipId, newPosition));
    }

    public bool Undo()
    {
        var state = CurrentState();
        if (_history.UndoCount == 0)
            return false;
        _state = _history.Undo(state);
        _project = ApplyToProject(_project!, _state);
        FixSelection();
        _revision++;
        UpdateDirty();
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
        _revision++;
        UpdateDirty();
        return true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var project = Project;
        var state = CurrentState();
        var revision = _revision;
        project = project with { UpdatedUtc = DateTimeOffset.UtcNow };
        await _store.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        if (_project is null || !string.Equals(_project.Id, project.Id, StringComparison.Ordinal))
            return;
        _savedState = state;
        if (_revision == revision)
            _project = project;
        UpdateDirty();
    }

    // G19 resilience. Relink is rejected while dirty so the atomic save
    // baseline always matches the persisted file; the caller saves or discards
    // first. Project ID and every clip ID survive (DEC-012); only the asset
    // path/fingerprint changes.
    public async Task RelinkMediaAssetAsync(
        string assetId,
        string newPath,
        int width,
        int height,
        double durationSeconds,
        bool hasAudio,
        long size,
        long lastWriteUtcTicks,
        CancellationToken cancellationToken)
    {
        if (_dirty)
            throw new InvalidOperationException(
                "Project Timeline có thay đổi chưa lưu; hãy lưu hoặc bỏ thay đổi trước khi nối lại nguồn.");
        var project = Project;
        var revision = _revision;
        var relinked = TimelineMediaRelink.RelinkMediaAsset(
            project, assetId, newPath, width, height, durationSeconds, hasAudio, size, lastWriteUtcTicks);
        await _store.SaveAsync(relinked, cancellationToken).ConfigureAwait(false);
        if (_revision != revision
            || !string.Equals(_project?.Id, project.Id, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Project Timeline đã thay đổi trong lúc nối lại nguồn; thao tác đã bị hủy.");
        var state = ToAudioState(relinked);
        _project = relinked;
        _state = state;
        _savedState = TimelineAudioEditState.Create(
            state.Timeline,
            state.SubtitleCues,
            state.AudioAssets,
            state.SourceAudio,
            state.VoiceCues,
            state.Music,
            state.Tracks,
            state.TextItems);
        _dirty = false;
    }

    public IReadOnlyList<TimelineMediaAsset> FindMissingMediaAssets()
    {
        var project = Project;
        return TimelineMediaRelink.FindMissingMediaAssets(project);
    }

    private void Apply(Func<TimelineAudioEditState, TimelineAudioEditState> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var before = CurrentState();
        var after = operation(before);
        var project = ApplyToProject(_project!, after);
        _history.Commit(before, after);
        _state = after;
        _project = project;
        FixSelection();
        _revision++;
        UpdateDirty();
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

    private void UpdateDirty()
    {
        _dirty = _savedState is null || !StatesEqual(CurrentState(), _savedState);
    }

    private static bool StatesEqual(TimelineAudioEditState left, TimelineAudioEditState right) =>
        left.Timeline.Clips.SequenceEqual(right.Timeline.Clips)
        && left.Timeline.Items.SequenceEqual(right.Timeline.Items)
        && left.SubtitleCues.SequenceEqual(right.SubtitleCues)
        && left.AudioAssets.SequenceEqual(right.AudioAssets)
        && left.SourceAudio.SequenceEqual(right.SourceAudio)
        && left.VoiceCues.SequenceEqual(right.VoiceCues)
        && left.Music == right.Music
        && (left.Tracks ?? []).SequenceEqual(right.Tracks ?? [])
        && (left.TextItems ?? []).SequenceEqual(right.TextItems ?? []);

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
            project.Music,
            project.Tracks,
            project.TextItems);
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
            Tracks = state.Tracks ?? project.Tracks,
            TextItems = state.TextItems ?? project.TextItems,
        };
        updated.Validate();
        return updated;
    }
}

namespace BiliSubStudio.Core.Timeline;

// G2 shell session. Owns only V2 state; it never touches EditorPage mutable
// state, EditorProjectStore schema 7, or production Data/Projects.
public sealed class TimelineEditorSession
{
    public TimelineEditorSession(TimelineProjectStoreV2 store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public TimelineProjectStoreV2 Store { get; }

    public TimelineProjectV2? Current { get; private set; }

    public async Task<TimelineProjectV2> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var project = await Store.CreateAsync(name, cancellationToken).ConfigureAwait(false);
        Current = project;
        return project;
    }

    public async Task<TimelineProjectV2> OpenAsync(string id, CancellationToken cancellationToken)
    {
        var project = await Store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        Current = project;
        return project;
    }

    public async Task<TimelineProjectV2> SaveAsync(TimelineProjectV2 project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        await Store.SaveAsync(project, cancellationToken).ConfigureAwait(false);
        Current = project;
        return project;
    }
}

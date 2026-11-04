namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    internal bool HasOpenEditorProject => _project is not null;

    // EDIT-CLOSE: the user deliberately drops the currently open project
    // (usually because the source video was deleted/renamed/moved after the
    // work was finished) in order to exit the app. Only in-memory state is
    // cleared and autosave is stopped; the old project file on disk is left
    // untouched.
    internal void DiscardOpenProjectForForcedClose()
    {
        CleanupProjectAutosave();
        _pendingProjectSave = null;
        _project = null;
    }

    internal async Task FlushForAppCloseAsync(CancellationToken cancellationToken)
    {
        await _editorTabLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            StopProjectSaveTimer();
            if (_project is null) return;

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ProjectSnapshot();
            await FlushProjectSaveAsync(snapshot);
            await SaveImageSidecarAsync();
        }
        finally
        {
            _editorTabLifecycleGate.Release();
        }
    }
}

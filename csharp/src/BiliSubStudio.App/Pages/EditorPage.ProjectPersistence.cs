namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
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

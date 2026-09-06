namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    internal bool HasOpenEditorProject => _project is not null;

    // EDIT-CLOSE: user chủ động bỏ project đang mở (thường vì video nguồn đã
    // bị xóa/đổi tên/di chuyển sau khi làm xong) để thoát app. Chỉ xóa state
    // trong bộ nhớ và dừng autosave; file project cũ trên đĩa được giữ nguyên.
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

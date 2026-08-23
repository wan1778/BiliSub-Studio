namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorCoreInitialized;

    private void InitializeEditorCore()
    {
        if (_editorCoreInitialized) return;
        _editorCoreInitialized = true;

        EnsureEditorParityInitialized();
        EnsureImageFeatureInitialized();
        PreviewPlayer.AreTransportControlsEnabled = false;
        SetInspectorMode(_inspectorMode);
        RefreshEditorActions();
    }
}

using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorCoreInitialized;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_editorCoreInitialized) return;
        _editorCoreInitialized = true;

        EnsureEditorParityInitialized();
        EnsureImageFeatureInitialized();
        PreviewPlayer.AreTransportControlsEnabled = false;
        SetInspectorMode(_inspectorMode);
        RefreshEditorActions();
    }
}

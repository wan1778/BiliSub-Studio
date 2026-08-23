using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorCoreInitialized;

    private void EditorPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_editorCoreInitialized)
        {
            BindStaticUiShell();
            _editorCoreInitialized = true;
        }

        RefreshEditorActions();
        RefreshImageControls();
        RefreshEditorParityControls();
    }
}

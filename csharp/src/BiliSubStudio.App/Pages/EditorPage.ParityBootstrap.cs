using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        EditorParity_Loaded(this, new RoutedEventArgs());
        EnsureImageFeatureInitialized();
        EnsureInteractionRepairInitialized();
        EnsureToolTransitionRepairInitialized();
        AssertEditorInteractionContract();
    }
}

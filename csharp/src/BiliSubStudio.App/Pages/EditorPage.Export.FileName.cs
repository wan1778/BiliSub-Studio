using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _normalizingEditorOutputFileName;

    private void EditorFileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_normalizingEditorOutputFileName || sender is not TextBox box) return;
        var current = box.Text;
        if (string.IsNullOrWhiteSpace(current))
        {
            ExportDialogValidationText.Text = "Tên file đầu ra không được để trống.";
            RefreshEditorActions();
            return;
        }

        var normalized = FileNamePolicy.NormalizeVideoOutputName(current);
        if (string.Equals(current, normalized, StringComparison.Ordinal)) return;

        _normalizingEditorOutputFileName = true;
        try { box.Text = normalized; }
        finally { _normalizingEditorOutputFileName = false; }
        ExportDialogValidationText.Text = "Tên file đầu ra đã được chuẩn hóa an toàn: " + normalized;
    }
}

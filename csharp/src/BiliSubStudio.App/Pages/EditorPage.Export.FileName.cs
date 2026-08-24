using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _normalizingEditorOutputFileName;

    private void EditorFileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_normalizingEditorOutputFileName) return;
        var current = FileNameBox.Text;
        if (string.IsNullOrWhiteSpace(current))
        {
            StatusText.Text = "Tên file đầu ra không được để trống.";
            RefreshEditorActions();
            return;
        }

        var normalized = FileNamePolicy.NormalizeVideoOutputName(current);
        if (string.Equals(current, normalized, StringComparison.Ordinal)) return;

        _normalizingEditorOutputFileName = true;
        try { FileNameBox.Text = normalized; }
        finally { _normalizingEditorOutputFileName = false; }
        StatusText.Text = "Tên file đầu ra đã được chuẩn hóa an toàn: " + normalized;
    }
}

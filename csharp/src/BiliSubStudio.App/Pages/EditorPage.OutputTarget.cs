using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.App.Pages;

internal sealed record EditorOutputTarget(string Directory, string FileName, EditorExportSettings Settings);

public sealed partial class EditorPage
{
    private EditorOutputTarget CaptureEditorOutputTarget(EditorExportSettings? settings = null)
    {
        var configuredDirectory = _application.Config.OutputDirectory?.Trim() ?? string.Empty;
        if (configuredDirectory.Length == 0)
            throw new InvalidOperationException("Chưa cấu hình thư mục xuất video.");
        var directory = Path.GetFullPath(configuredDirectory);
        var fileName = FileNameBox.Text?.Trim() ?? string.Empty;
        if (fileName.Length == 0)
            throw new InvalidOperationException("Tên file đầu ra không được để trống.");
        return new EditorOutputTarget(directory, fileName, EditorExportPolicy.Normalize(settings));
    }

    private void EnsureEditorExportSourceIdentity(string projectId, string sourcePath)
    {
        if (_project is null
            || !string.Equals(_project.Id, projectId, StringComparison.Ordinal)
            || !EditorSourceSelection.IsSameSource(_path, sourcePath))
            throw new InvalidOperationException("Nguồn Editor đã thay đổi trong lúc chuẩn bị xuất; hãy xuất lại từ trạng thái hiện tại.");
        EnsureCurrentSourceFingerprint();
    }

    private static string ValidateFinalEditorOutput(EditorOutputTarget target, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidDataException("Pipeline Editor trả về đường dẫn output rỗng.");

        var output = Path.GetFullPath(outputPath.Trim());
        var info = new FileInfo(output);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException("Pipeline Editor báo hoàn tất nhưng file output không tồn tại hoặc rỗng.");
        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension is not (".mp4" or ".mkv"))
            throw new InvalidDataException("Pipeline Editor trả về định dạng output ngoài MP4/MKV.");

        var actualDirectory = Path.GetFullPath(info.DirectoryName
            ?? throw new InvalidDataException("Không xác định được thư mục output vừa xuất."))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedDirectory = Path.GetFullPath(target.Directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(actualDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Output hoàn tất nằm sai thư mục. Đích đã khóa: {expectedDirectory}; thực tế: {actualDirectory}.");
        return output;
    }
}

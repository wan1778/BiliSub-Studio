#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CSHARP = ROOT / "csharp"
PAGES = CSHARP / "src/BiliSubStudio.App/Pages"
APP_SERVICES = CSHARP / "src/BiliSubStudio.App/Services"
CORE_EDITOR = CSHARP / "src/BiliSubStudio.Core/Editor"
TESTS = CSHARP / "tests/BiliSubStudio.Core.ContractTests"
VALIDATOR = CSHARP / "scripts/validate_csharp_migration.py"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="\n")


def replace_exact(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"missing exact marker: {label}")
    return text.replace(old, new, 1)


def replace_between(text: str, start: str, end: str, replacement: str, label: str) -> str:
    left = text.find(start)
    if left < 0:
        raise RuntimeError(f"missing start marker: {label}")
    right = text.find(end, left + len(start))
    if right < 0:
        raise RuntimeError(f"missing end marker: {label}")
    return text[:left] + replacement.rstrip() + "\n\n" + text[right:]


def main() -> int:
    xaml_path = PAGES / "EditorPage.xaml"
    editor_path = PAGES / "EditorPage.xaml.cs"
    picker_path = APP_SERVICES / "FilePickerService.cs"
    program_path = TESTS / "Program.cs"

    xaml = read(xaml_path)
    xaml = replace_exact(xaml, 'Click="Pick_Click" Content="Mở video"', 'Click="OpenVideo_Click" Content="Mở video"', "SOURCE-01 XAML handler")
    write(xaml_path, xaml)

    editor = read(editor_path)
    editor = replace_between(
        editor,
        "    private async void Pick_Click(object sender, RoutedEventArgs e)\n",
        "    private async void Refresh_Click(object sender, RoutedEventArgs e)",
        '''    private async void OpenVideo_Click(object sender, RoutedEventArgs e)\n    {\n        try { await OpenVideoAsync(); }\n        catch (OperationCanceledException) { }\n        catch (Exception error)\n        {\n            StatusText.Text = "Không mở được video: " + error.Message;\n            RefreshEditorActions();\n        }\n    }\n\n    private async Task OpenVideoAsync()\n    {\n        // SOURCE-02: cancel is a no-op. Do not touch the current source/project before a real path exists.\n        var pickedPath = await _picker.PickVideoAsync();\n        if (string.IsNullOrWhiteSpace(pickedPath)) return;\n\n        var candidatePath = EditorSourceSelection.NormalizeCandidatePath(pickedPath);\n        if (EditorSourceSelection.IsSameSource(_path, candidatePath))\n        {\n            StatusText.Text = "Video này đang được mở; giữ nguyên project và preview hiện tại.";\n            return;\n        }\n\n        // SOURCE-05: probe and load the candidate before mutating any current Editor state.\n        MediaPreviewInfo candidateMedia;\n        try\n        {\n            candidateMedia = await _application.Media.ProbeAsync(candidatePath, CancellationToken.None);\n        }\n        catch (Exception error) when (error is not OperationCanceledException)\n        {\n            throw new InvalidDataException(\n                "File video không hợp lệ, đã hỏng hoặc codec không đọc được. Project hiện tại vẫn được giữ nguyên. " + error.Message,\n                error);\n        }\n\n        EditorProject candidateProject;\n        try\n        {\n            candidateProject = await _application.LoadEditorProjectAsync(candidatePath, candidateMedia, CancellationToken.None);\n        }\n        catch (Exception error) when (error is not OperationCanceledException)\n        {\n            throw new InvalidDataException(\n                "Không mở được project của video đã chọn. Project hiện tại vẫn được giữ nguyên. " + error.Message,\n                error);\n        }\n\n        var pendingSubtitle = _project is null ? _subtitleSource : null;\n        var pendingPlacement = _subtitlePlacement;\n\n        // SOURCE-04: one explicit old-state save, then one explicit preview disposal.\n        await SaveCurrentSourceStateForSwitchAsync();\n        await DisposePreviewForSourceChangeAsync();\n\n        _path = candidatePath;\n        _media = candidateMedia;\n        _project = candidateProject;\n        _document.Reset(_project.Regions);\n        _audioSettings = EditorProjectStore.NormalizeAudio(_project.Audio);\n        ApplyAudioSettingsToUi();\n        if (pendingSubtitle is not null)\n        {\n            _subtitleSource = pendingSubtitle;\n            _subtitlePlacement = pendingPlacement;\n            AttachSubtitleToProject(string.Empty);\n            SrtPathText.Text = pendingSubtitle.Path;\n            UpdateSubtitleSummary();\n            TranslationStatusText.Text = "Đã gắn SRT đã chọn vào video; có thể đặt khung và Vietsub.";\n        }\n        else await RestoreSubtitleAsync(_project.Subtitle);\n        await SyncSubtitleCueEditorAsync();\n        await RestoreSpeechAndVoiceAsync();\n        await EnsureImageProjectLoadedAsync();\n        _draftRegion = null;\n        Timeline.Maximum = Math.Max(0.1, _media.Duration);\n        Timeline.Value = 0;\n        PathText.Text = candidatePath;\n        MediaText.Text = $"{_media.Width}×{_media.Height} · {_media.Duration:0.0}s · {_media.Codec} · preview xử lý dùng cùng pipeline FFmpeg với export";\n        _syncingInputs = true;\n        try\n        {\n            FileNameBox.Text = _project.FileName;\n            EndBox.Value = _media.Duration;\n        }\n        finally { _syncingInputs = false; }\n        await PreparePlayerAsync();\n        if (_document.Selected is not null) LoadSelectedIntoInputs();\n        else SetCoordinateBoxes(0, 0, 0, 0);\n        RenderDocument();\n        RenderImageList();\n        RenderImageOverlays();\n        await UpdateFrameAsync();\n        StatusText.Text = _document.Regions.Count > 0\n            ? $"Đã mở lại project với {_document.Regions.Count} vùng."\n            : _subtitleSource is not null\n                ? $"Đã mở lại SRT {_subtitleSource.Cues.Count} câu; khung phụ đề có thể kéo/resize trực tiếp."\n                : "Chọn SRT tiếng Trung để bắt đầu Vietsub, hoặc kéo frame để tạo vùng hiệu ứng.";\n        RefreshEditorActions();\n        QueueProjectSave();\n    }\n\n    private async Task SaveCurrentSourceStateForSwitchAsync()\n    {\n        if (_project is null) return;\n        var pendingSave = _saveCancellation;\n        _saveCancellation = null;\n        if (pendingSave is not null)\n        {\n            pendingSave.Cancel();\n            pendingSave.Dispose();\n        }\n        await SaveImageSidecarAsync();\n        await _application.SaveEditorProjectAsync(ProjectSnapshot(), CancellationToken.None);\n    }\n\n    private async Task DisposePreviewForSourceChangeAsync()\n    {\n        var playbackCancellation = _playbackPreviewCancellation;\n        _playbackPreviewCancellation = null;\n        playbackCancellation?.Cancel();\n\n        var frameCancellation = _previewCancellation;\n        _previewCancellation = null;\n        if (frameCancellation is not null)\n        {\n            frameCancellation.Cancel();\n            frameCancellation.Dispose();\n        }\n        ++_previewRevision;\n\n        var player = _player;\n        _player = null;\n        if (player is not null)\n        {\n            player.PlaybackSession.PositionChanged -= PlayerPositionChanged;\n            player.MediaEnded -= PlayerMediaEnded;\n            player.MediaFailed -= PlayerMediaFailed;\n            player.Pause();\n            player.Source = null;\n            player.Dispose();\n        }\n\n        PreviewPlayer.IsFullWindow = false;\n        _playerMode = false;\n        _previewRendering = false;\n        _playerSourceStart = 0;\n        _playerSourceDuration = 0;\n        ApplyPreviewPresentation(false);\n\n        var previewPath = _playerPreviewPath;\n        _playerPreviewPath = null;\n        if (previewPath is not null)\n            await _application.DeleteEditorPreviewSegmentAsync(previewPath);\n    }''',
        "SOURCE-01 through SOURCE-06 OpenVideo flow",
    )
    write(editor_path, editor)

    write(picker_path, r'''using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace BiliSubStudio.App.Services;

public sealed class FilePickerService(Func<Window> window) : IFilePickerService
{
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const int OfnNoChangeDir = 0x00000008;

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".m2ts"];
    private static readonly string[] SubtitleExtensions = [".srt"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    public Task<string?> PickVideoAsync() => PickAsync(
        PickerViewMode.Thumbnail,
        PickerLocationId.VideosLibrary,
        VideoExtensions,
        "Chọn video",
        "Video hỗ trợ\0*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts\0\0");

    public Task<string?> PickSubtitleAsync() => PickAsync(
        PickerViewMode.List,
        PickerLocationId.DocumentsLibrary,
        SubtitleExtensions,
        "Chọn SRT tiếng Trung",
        "SubRip (*.srt)\0*.srt\0\0");

    public Task<string?> PickImageAsync() => PickAsync(
        PickerViewMode.Thumbnail,
        PickerLocationId.PicturesLibrary,
        ImageExtensions,
        "Chọn ảnh/logo",
        "Ảnh PNG/JPG\0*.png;*.jpg;*.jpeg\0\0");

    private async Task<string?> PickAsync(
        PickerViewMode viewMode,
        PickerLocationId location,
        IReadOnlyList<string> extensions,
        string title,
        string win32Filter)
    {
        var owner = window();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        Exception? primaryError = null;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = viewMode,
                SuggestedStartLocation = location,
            };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            return file is null ? null : ValidatePickedPath(file.Path, extensions);
        }
        catch (OperationCanceledException)
        {
            // SOURCE-02: closing/canceling a picker is not an error.
            return null;
        }
        catch (Exception error)
        {
            primaryError = error;
        }

        // SOURCE-03: only use the Win32 common dialog after FileOpenPicker itself failed.
        try
        {
            var fallback = PickWithWin32(hwnd, title, win32Filter);
            return fallback is null ? null : ValidatePickedPath(fallback, extensions);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception fallbackError)
        {
            throw new InvalidOperationException(
                $"Bộ chọn tệp Windows bị lỗi. FileOpenPicker: {primaryError?.Message ?? "không rõ"}. " +
                $"Fallback Win32 cũng không mở được: {fallbackError.Message}",
                new AggregateException(primaryError ?? new InvalidOperationException("FileOpenPicker failed"), fallbackError));
        }
    }

    private static string ValidatePickedPath(string path, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Bộ chọn tệp trả về đường dẫn rỗng.");
        var full = Path.GetFullPath(path);
        var extension = Path.GetExtension(full);
        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Loại tệp {extension} không được hỗ trợ ở hộp chọn này.");
        return full;
    }

    private static string? PickWithWin32(IntPtr hwnd, string title, string filter)
    {
        var buffer = new StringBuilder(32_768);
        var dialog = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Owner = hwnd,
            Filter = filter,
            FilterIndex = 1,
            File = buffer,
            MaxFile = buffer.Capacity,
            Title = title,
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
        };
        if (GetOpenFileNameW(ref dialog)) return buffer.ToString();
        var extendedError = CommDlgExtendedError();
        if (extendedError == 0) return null; // user canceled the fallback dialog
        throw new Win32Exception(unchecked((int)extendedError), $"Common dialog error 0x{extendedError:X8}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public StringBuilder File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? InitialDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedFlags;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
}
''')

    write(CORE_EDITOR / "EditorSourceSelection.cs", '''namespace BiliSubStudio.Core.Editor;\n\npublic static class EditorSourceSelection\n{\n    public static string NormalizeCandidatePath(string path)\n    {\n        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Video path is empty.", nameof(path));\n        var full = Path.GetFullPath(path);\n        if (!File.Exists(full)) throw new FileNotFoundException("Video đã bị di chuyển, xóa hoặc không còn truy cập được.", full);\n        return full;\n    }\n\n    public static bool IsSameSource(string? currentPath, string candidatePath)\n    {\n        if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(candidatePath)) return false;\n        return string.Equals(\n            Path.GetFullPath(currentPath),\n            Path.GetFullPath(candidatePath),\n            StringComparison.OrdinalIgnoreCase);\n    }\n}\n''')

    write(TESTS / "EditorSourceSelectionContract.cs", '''using BiliSubStudio.Core.Editor;\n\nnamespace BiliSubStudio.Core.ContractTests;\n\ninternal static class EditorSourceSelectionContract\n{\n    public static Task RunAsync()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "bilisub-source-selection-" + Guid.NewGuid().ToString("N"));\n        Directory.CreateDirectory(root);\n        try\n        {\n            var video = Path.Combine(root, "video.mp4");\n            File.WriteAllBytes(video, [0, 1, 2, 3]);\n            var nested = Path.Combine(root, "nested");\n            Directory.CreateDirectory(nested);\n            var alias = Path.Combine(nested, "..", "video.mp4");\n\n            var normalized = EditorSourceSelection.NormalizeCandidatePath(alias);\n            if (!string.Equals(normalized, Path.GetFullPath(video), StringComparison.OrdinalIgnoreCase))\n                throw new InvalidOperationException("source normalization changed the selected file");\n            if (!EditorSourceSelection.IsSameSource(video, alias))\n                throw new InvalidOperationException("same video was not recognized as the same source");\n            if (EditorSourceSelection.IsSameSource(null, video))\n                throw new InvalidOperationException("missing current source cannot equal a candidate");\n\n            var missingRejected = false;\n            try { EditorSourceSelection.NormalizeCandidatePath(Path.Combine(root, "missing.mp4")); }\n            catch (FileNotFoundException) { missingRejected = true; }\n            if (!missingRejected) throw new InvalidOperationException("missing video path was accepted");\n            return Task.CompletedTask;\n        }\n        finally\n        {\n            try { Directory.Delete(root, true); } catch { }\n        }\n    }\n}\n''')

    program = read(program_path)
    program = replace_exact(
        program,
        '        ("editor manual cue state persists locks and preserves timeline", EditorSubtitleManualContract.RunAsync),\n',
        '        ("editor manual cue state persists locks and preserves timeline", EditorSubtitleManualContract.RunAsync),\n        ("editor source selection keeps cancel/same-source transitions safe", EditorSourceSelectionContract.RunAsync),\n',
        "SOURCE contract registration",
    )
    write(program_path, program)

    validator = read(VALIDATOR)
    marker = '''require("Loaded += EditorPage_Loaded;" in editor and "private void EditorPage_Loaded" in editor_partials,\n        "Editor must use the actual Loaded event as its single feature initialization lifecycle")\n'''
    addition = marker + '''\npicker_source = read(CSHARP / "src/BiliSubStudio.App/Services/FilePickerService.cs")\nfor picker_marker in ("GetOpenFileNameW", "CommDlgExtendedError", "catch (OperationCanceledException)", "Fallback Win32"):\n    require(picker_marker in picker_source, f"Editor picker fallback/cancel contract missing {picker_marker}")\nrequire('Click="OpenVideo_Click"' in editor and "private async void OpenVideo_Click" in editor_partials,\n        "Editor Open Video must have one XAML handler named OpenVideo_Click")\nrequire("Pick_Click(" not in editor_partials, "legacy Pick_Click handler returned")\nopen_video = editor_partials.split("private async Task OpenVideoAsync()", 1)[1].split("private async Task SaveCurrentSourceStateForSwitchAsync()", 1)[0]\nrequire(open_video.count("SaveCurrentSourceStateForSwitchAsync();") == 1,\n        "OpenVideoAsync must save the old source state exactly once")\nrequire(open_video.count("DisposePreviewForSourceChangeAsync();") == 1,\n        "OpenVideoAsync must dispose the old preview exactly once")\nrequire("EditorSourceSelection.IsSameSource" in open_video, "same-source no-op guard missing")\nrequire("_application.Media.ProbeAsync(candidatePath" in open_video and "_path = candidatePath;" in open_video\n        and open_video.index("_application.Media.ProbeAsync(candidatePath") < open_video.index("_path = candidatePath;"),\n        "candidate video must be probed before mutating current Editor source state")\n'''
    validator = replace_exact(validator, marker, addition, "SOURCE static regression contracts")
    write(VALIDATOR, validator)

    print("Applied SOURCE-01 through SOURCE-06 cleanup")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

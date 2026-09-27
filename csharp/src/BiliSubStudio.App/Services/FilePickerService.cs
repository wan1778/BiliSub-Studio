using System.ComponentModel;
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

    // P1 multi-video (27/09): WinRT PickMultipleFilesAsync; fallback Win32
    // OFN_ALLOWMULTISELECT (buffer dir\0file1\0file2...\0\0, 1 file = full path).
    // Trả rỗng khi user hủy — caller giữ project nguyên, không báo lỗi.
    // P1.1 (27/09): validate path SAU khi dialog đóng, ngoài try — lỗi path
    // không được re-prompt dialog Win32 thứ 2 sau khi user đã chọn file.
    public async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        var owner = window();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        Exception? primaryError = null;
        IReadOnlyList<string>? rawPaths = null;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
            };
            foreach (var extension in VideoExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files is not null && files.Count > 0)
                rawPaths = files.Select(file => file.Path).ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception error)
        {
            primaryError = error;
        }

        if (rawPaths is not null)
            return rawPaths.Select(file => ValidatePickedPath(file, VideoExtensions)).ToArray();

        IReadOnlyList<string>? fallbackPaths = null;
        try
        {
            fallbackPaths = PickMultipleWithWin32(hwnd, "Chọn video (có thể chọn nhiều file)",
                "Video hỗ trợ\0*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts\0\0");
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception fallbackError)
        {
            throw new InvalidOperationException(
                $"Bộ chọn nhiều video bị lỗi. FileOpenPicker: {primaryError?.Message ?? "không rõ"}. " +
                $"Fallback Win32 cũng không mở được: {fallbackError.Message}",
                new AggregateException(primaryError ?? new InvalidOperationException("FileOpenPicker failed"), fallbackError));
        }
        return fallbackPaths.Select(file => ValidatePickedPath(file, VideoExtensions)).ToArray();
    }

    public Task<string?> PickSubtitleAsync() => PickAsync(
        PickerViewMode.List,
        PickerLocationId.DocumentsLibrary,
        SubtitleExtensions,
        "Thêm SRT Việt đã dịch",
        "SubRip (*.srt)\0*.srt\0\0");

    public Task<string?> PickTranslatedSubtitleAsync() => PickAsync(
        PickerViewMode.List,
        PickerLocationId.DocumentsLibrary,
        SubtitleExtensions,
        "Chọn SRT Việt đã dịch",
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

    private const int OfnAllowMultiselect = 0x00000200;

    private static IReadOnlyList<string> PickMultipleWithWin32(IntPtr hwnd, string title, string filter)
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
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnAllowMultiselect,
        };
        if (!GetOpenFileNameW(ref dialog))
        {
            var extendedError = CommDlgExtendedError();
            if (extendedError == 0) return []; // user canceled the fallback dialog
            throw new Win32Exception(unchecked((int)extendedError), $"Common dialog error 0x{extendedError:X8}");
        }
        // Multi-select: dir\0file1\0file2...\0\0; single pick = full path + \0\0.
        // P1.1 (27/09): trả path thô, validate là việc của caller NGOÀI try —
        // lỗi path không được báo nhầm "fallback không mở được" khi dialog đã
        // chọn file thành công.
        var parts = buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return [];
        return parts.Length == 1
            ? [parts[0]]
            : parts.Skip(1).Select(file => Path.Combine(parts[0], file)).ToArray();
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

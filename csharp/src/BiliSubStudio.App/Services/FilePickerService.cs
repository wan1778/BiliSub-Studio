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

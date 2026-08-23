using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace BiliSubStudio.App.Services;

public sealed class FilePickerService(Func<Window> window) : IFilePickerService
{
    public Task<string?> PickVideoAsync() => PickAsync(
        PickerViewMode.Thumbnail,
        PickerLocationId.VideosLibrary,
        [".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".m2ts"],
        "Video (*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts)\0*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts\0All files (*.*)\0*.*\0\0");

    public Task<string?> PickSubtitleAsync() => PickAsync(
        PickerViewMode.List,
        PickerLocationId.DocumentsLibrary,
        [".srt"],
        "SubRip subtitle (*.srt)\0*.srt\0All files (*.*)\0*.*\0\0");

    public Task<string?> PickImageAsync() => PickAsync(
        PickerViewMode.Thumbnail,
        PickerLocationId.PicturesLibrary,
        [".png", ".jpg", ".jpeg"],
        "Image (*.png;*.jpg;*.jpeg)\0*.png;*.jpg;*.jpeg\0All files (*.*)\0*.*\0\0");

    private async Task<string?> PickAsync(
        PickerViewMode viewMode,
        PickerLocationId suggestedStartLocation,
        IReadOnlyList<string> extensions,
        string legacyFilter)
    {
        var owner = window();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Cửa sổ BiliSub chưa sẵn sàng để mở hộp chọn file.");

        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = viewMode,
                SuggestedStartLocation = suggestedStartLocation,
            };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception modernPickerError)
        {
            var fallback = PickWithWin32Dialog(hwnd, legacyFilter);
            if (fallback.Cancelled) return null;
            if (!string.IsNullOrWhiteSpace(fallback.Path)) return fallback.Path;
            throw new InvalidOperationException(
                "Windows File Picker không mở được và hộp chọn file dự phòng cũng thất bại.",
                modernPickerError);
        }
    }

    private static (string? Path, bool Cancelled) PickWithWin32Dialog(IntPtr owner, string filter)
    {
        var buffer = new StringBuilder(32_768);
        var dialog = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Owner = owner,
            Filter = filter,
            File = buffer,
            MaxFile = buffer.Capacity,
            FilterIndex = 1,
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
        };
        if (GetOpenFileName(ref dialog)) return (buffer.ToString(), false);
        var error = CommDlgExtendedError();
        return error == 0
            ? (null, true)
            : throw new InvalidOperationException($"Hộp chọn file Win32 lỗi 0x{error:X8}.");
    }

    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string Filter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public StringBuilder File;
        public int MaxFile;
        public StringBuilder? FileTitle;
        public int MaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? InitialDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsExtended;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
}

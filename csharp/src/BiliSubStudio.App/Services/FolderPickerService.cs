using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace BiliSubStudio.App.Services;

public sealed class FolderPickerService(Func<Window> windowProvider) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string initialPath)
    {
        // Duong Win32 mo DUNG thu muc hien tai (initialPath): WinRT khong co API
        // nay, luc nao cung mo Downloads roi bat user tu mo den noi can den.
        var window = windowProvider();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            return await Win32FolderDialog.PickFolderAsync(
                window.DispatcherQueue, hwnd,
                BiliSubStudio.Core.IO.FileNamePolicy.ResolveInitialDirectory(initialPath),
                "Chọn thư mục lưu");
        }
        catch
        {
            // Fallback: WinRT cu (mo Downloads). Cham nhung khong bao gio hong hon.
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }

    public async Task OpenFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Thư mục lưu không còn tồn tại.");
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        if (!await Launcher.LaunchFolderAsync(folder))
        {
            throw new InvalidOperationException("Windows không mở được thư mục lưu.");
        }
    }
}

using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace BiliSubStudio.App.Services;

public sealed class FolderPickerService(Func<Window> windowProvider) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string initialPath)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(windowProvider());
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
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

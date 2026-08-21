using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace BiliSubStudio.App.Services;

public sealed class FilePickerService(Func<Window> window) : IFilePickerService
{
    public async Task<string?> PickVideoAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        foreach (var extension in new[] { ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".m2ts" })
            picker.FileTypeFilter.Add(extension);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window());
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return (await picker.PickSingleFileAsync())?.Path;
    }
}

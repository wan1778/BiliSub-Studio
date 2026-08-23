namespace BiliSubStudio.App.Services;

public interface IFilePickerService
{
    Task<string?> PickVideoAsync();
    Task<string?> PickSubtitleAsync();
    Task<string?> PickImageAsync();
}

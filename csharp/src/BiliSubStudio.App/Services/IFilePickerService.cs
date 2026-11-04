namespace BiliSubStudio.App.Services;

public interface IFilePickerService
{
    Task<string?> PickVideoAsync();
    Task<IReadOnlyList<string>> PickVideosAsync();
    Task<string?> PickSubtitleAsync();
    Task<string?> PickTranslatedSubtitleAsync();
    Task<string?> PickImageAsync();
}

namespace BiliSubStudio.App.Services;

public interface IFilePickerService
{
    Task<string?> PickVideoAsync();
    // P1 multi-video (27/09): chọn nhiều video 1 lần để nối tiếp trên Timeline.
    Task<IReadOnlyList<string>> PickVideosAsync();
    Task<string?> PickSubtitleAsync();
    Task<string?> PickTranslatedSubtitleAsync();
    Task<string?> PickImageAsync();
}

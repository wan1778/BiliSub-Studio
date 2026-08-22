namespace BiliSubStudio.App.Services;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string initialPath);

    Task OpenFolderAsync(string path);
}

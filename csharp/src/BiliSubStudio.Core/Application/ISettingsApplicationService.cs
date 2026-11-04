namespace BiliSubStudio.Core.Application;

public interface ISettingsApplicationService
{
    Task<SettingsSnapshot> InitializeAsync(CancellationToken cancellationToken = default);

    Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task<SettingsSnapshot> SetOutputDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SettingsSnapshot> SetOcrOutputDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the Edit tab's own output folder (video export, SRT Việt,
    /// preview render). Independent from the download and OCR folders.</summary>
    Task<SettingsSnapshot> SetEditorOutputDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SettingsSnapshot> SetThemeAsync(
        string theme,
        CancellationToken cancellationToken = default);

    Task<SettingsSnapshot> SetUpdateCheckAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

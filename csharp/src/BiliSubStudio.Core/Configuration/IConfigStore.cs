namespace BiliSubStudio.Core.Configuration;

public interface IConfigStore
{
    AppConfig Snapshot { get; }

    string? LastLoadWarning { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AppConfig> UpdateAsync(
        Func<AppConfig, AppConfig> update,
        CancellationToken cancellationToken = default);
}

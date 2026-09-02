using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public sealed class SettingsApplicationService(
    AppPaths paths,
    IConfigStore configStore,
    IStorageUsageReader storageUsageReader) : ISettingsApplicationService
{
    public async Task<SettingsSnapshot> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await configStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<SettingsSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        BuildSnapshotAsync(cancellationToken);

    public async Task<SettingsSnapshot> SetOutputDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = await ValidateWritableDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        await configStore.UpdateAsync(
            config => config with { OutputDirectory = path },
            cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SettingsSnapshot> SetOcrOutputDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        path = await ValidateWritableDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        await configStore.UpdateAsync(
            config => config with { OcrOutputDirectory = path },
            cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SettingsSnapshot> SetThemeAsync(
        string theme,
        CancellationToken cancellationToken = default)
    {
        theme = theme?.Trim().ToLowerInvariant() ?? string.Empty;
        if (theme is not ("dark" or "light"))
        {
            throw new ArgumentException("Theme phải là dark hoặc light.", nameof(theme));
        }

        await configStore.UpdateAsync(
            config => config with { Theme = theme },
            cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SettingsSnapshot> SetUpdateCheckAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await configStore.UpdateAsync(
            config => config with { CheckUpdates = enabled },
            cancellationToken).ConfigureAwait(false);
        return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SettingsSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var storage = await storageUsageReader.ReadAsync(paths, cancellationToken).ConfigureAwait(false);
        var drive = Path.GetPathRoot(paths.Root);
        if (string.IsNullOrWhiteSpace(drive))
        {
            drive = paths.Root;
        }

        return new SettingsSnapshot(
            paths.Root,
            drive,
            configStore.Snapshot,
            storage,
            configStore.LastLoadWarning);
    }

    private static async Task<string> ValidateWritableDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        path = path?.Trim() ?? string.Empty;
        if (path.Length == 0) throw new ArgumentException("Thư mục xuất rỗng.", nameof(path));

        path = Path.GetFullPath(path);
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".bilisub-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probe, "BiliSub Studio output write probe", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            throw new IOException("Thư mục đã chọn không cho phép BiliSub Studio ghi tệp.", error);
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch
            {
                // A failed cleanup must not turn a successful write-access probe into a settings failure.
            }
        }
        return path;
    }
}

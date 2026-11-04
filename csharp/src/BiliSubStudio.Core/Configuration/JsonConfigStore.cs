using System.Text.Json;

namespace BiliSubStudio.Core.Configuration;

public sealed class JsonConfigStore(AppPaths paths) : IConfigStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppConfig _current = AppConfigNormalizer.Normalize(new AppConfig(), paths);
    private bool _disposed;
    // Fix for the config loss issue 16/09: Update before Initialize (snapshot still default) yet
    // allowing a save wiped all user configuration. From now on Update loads the file first
    // (under the same gate) so it never overwrites existing configuration with defaults.
    // _initialized is only a cache (skip once loaded), not a behavior gate.
    private bool _initialized;

    public AppConfig Snapshot => Volatile.Read(ref _current);

    public string? LastLoadWarning { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedLockedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        paths.EnsureBootstrapDirectories();
        var defaults = AppConfigNormalizer.Normalize(new AppConfig(), paths);
        Volatile.Write(ref _current, defaults);
        LastLoadWarning = null;

        if (!File.Exists(paths.ConfigFile))
        {
            try
            {
                await AtomicJsonFile.WriteAsync(paths.ConfigFile, defaults, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableLoadFailure(exception))
            {
                // appstate.New also starts with defaults when its first save fails.
                LastLoadWarning = $"Không ghi được config mặc định: {exception.Message}";
            }

            _initialized = true;
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(paths.ConfigFile, cancellationToken)
                .ConfigureAwait(false);
            var loaded = AtomicJsonFile.Deserialize(json);
            Volatile.Write(ref _current, AppConfigNormalizer.Normalize(loaded, paths));
        }
        catch (Exception exception) when (IsRecoverableLoadFailure(exception))
        {
            // The Go baseline keeps its in-memory defaults and leaves the bad file untouched.
            LastLoadWarning = $"Không đọc được config cũ; đang dùng mặc định: {exception.Message}";
        }
        _initialized = true;
    }

    public async Task<AppConfig> UpdateAsync(
        Func<AppConfig, AppConfig> update,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Config loss issue 16/09: load first under the same gate — Update never
            // overwrites defaults onto an existing file, even when called before Initialize.
            await EnsureInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            var next = AppConfigNormalizer.Normalize(update(Snapshot), paths);
            // Match appstate.UpdateConfig: the in-memory snapshot changes before persistence.
            Volatile.Write(ref _current, next);
            await AtomicJsonFile.WriteAsync(paths.ConfigFile, next, cancellationToken)
                .ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private static bool IsRecoverableLoadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;
}

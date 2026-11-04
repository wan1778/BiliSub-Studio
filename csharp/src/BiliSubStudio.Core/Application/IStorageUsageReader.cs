using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public interface IStorageUsageReader
{
    Task<StorageUsage> ReadAsync(AppPaths paths, CancellationToken cancellationToken = default);

    // Download media button stuck issue 05/10: a recursive scan of ~900k files (Tools + voice
    // Cache) takes tens of minutes, and every Set* setting waits on it. The
    // settings path only needs display numbers: return the previous number immediately + scan in
    // the background.
    bool TryGetLast(out StorageUsage usage);
    void RefreshInBackground(AppPaths paths);
}

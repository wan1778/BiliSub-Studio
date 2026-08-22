using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public interface IStorageUsageReader
{
    Task<StorageUsage> ReadAsync(AppPaths paths, CancellationToken cancellationToken = default);
}

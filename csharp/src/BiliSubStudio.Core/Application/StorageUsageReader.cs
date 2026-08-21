using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public sealed class StorageUsageReader : IStorageUsageReader
{
    public Task<StorageUsage> ReadAsync(AppPaths paths, CancellationToken cancellationToken = default) =>
        Task.Run(
            () => new StorageUsage(
                DirectorySize(paths.Data, cancellationToken),
                DirectorySize(paths.Tools, cancellationToken),
                DirectorySize(paths.Ocr, cancellationToken),
                DirectorySize(paths.Temp, cancellationToken),
                DirectorySize(paths.Cache, cancellationToken)),
            cancellationToken);

    private static long DirectorySize(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(child);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}

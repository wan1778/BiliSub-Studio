using System.Text.Json;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Timeline;

// G2: separate persistence path with a test data root. All reads/writes stay
// inside Root. Production schema-7 state (Data/Projects) is never touched:
// the constructor rejects any root at or inside a Data/Projects tree, and
// every resolved path is prefix-checked before IO.
public sealed class TimelineProjectStoreV2
{
    public const string TestRootFolderName = "TimelineV2";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TimelineProjectStoreV2(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root.Trim());
        EnsureNotProductionRoot(full);
        _root = full;
        Directory.CreateDirectory(_root);
    }

    public static TimelineProjectStoreV2 FromAppPaths(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new TimelineProjectStoreV2(Path.Combine(paths.Temp, TestRootFolderName));
    }

    public string Root => _root;

    public async Task<TimelineProjectV2> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            TimelineProjectV2.ValidateName(name),
            DateTimeOffset.UtcNow);
        await SaveAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task SaveAsync(TimelineProjectV2 project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        TimelineProjectV2.ValidateId(project.Id);
        TimelineProjectV2.ValidateName(project.Name);
        var stored = project with { UpdatedUtc = DateTimeOffset.UtcNow };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteStoredFileAsync(stored, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TimelineProjectV2> LoadAsync(string id, CancellationToken cancellationToken)
    {
        TimelineProjectV2.ValidateId(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadStoredFileAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TimelineProjectV2>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return [];
            var projects = new List<TimelineProjectV2>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                if (!IsId(id)) continue;
                try
                {
                    projects.Add(await ReadStoredFileLockedAsync(id, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // One unreadable test record must not hide the rest of the shell list.
                }
            }
            return projects.OrderByDescending(project => project.UpdatedUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void EnsureNotProductionRoot(string fullRoot)
    {
        var segments = fullRoot.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (string.Equals(segments[index], "Data", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 1], "Projects", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Timeline V2 không được dùng thư mục production Data/Projects.");
        }
        if (segments.Length > 0 && string.Equals(segments[^1], "Projects", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Timeline V2 không được dùng thư mục production Projects.");
    }

    private string ProjectPath(string id)
    {
        TimelineProjectV2.ValidateId(id);
        var path = Path.GetFullPath(Path.Combine(_root, id + ".json"));
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Timeline V2 path thoát khỏi test root.");
        return path;
    }

    private async Task WriteStoredFileAsync(TimelineProjectV2 project, CancellationToken cancellationToken)
    {
        var path = ProjectPath(project.Id);
        Directory.CreateDirectory(_root);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = new TimelineProjectV2File(
                TimelineProjectV2.Schema, project.Id, project.Name, project.UpdatedUtc);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, payload, Json, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private async Task<TimelineProjectV2> ReadStoredFileAsync(string id, CancellationToken cancellationToken)
    {
        var path = ProjectPath(id);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Timeline V2 project không tồn tại.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var payload = await JsonSerializer.DeserializeAsync<TimelineProjectV2File>(stream, Json, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Timeline V2 project rỗng.");
        if (payload.Schema != TimelineProjectV2.Schema
            || !string.Equals(payload.Id, id, StringComparison.Ordinal))
            throw new InvalidDataException("Timeline V2 project không đúng phiên bản.");
        return new TimelineProjectV2(payload.Id, TimelineProjectV2.ValidateName(payload.Name), payload.UpdatedUtc);
    }

    private Task<TimelineProjectV2> ReadStoredFileLockedAsync(string id, CancellationToken cancellationToken) =>
        ReadStoredFileAsync(id, cancellationToken);

    private static bool IsId(string? id) =>
        id is { Length: 32 } && id.All(character => char.IsAsciiHexDigit(character));

    private sealed record TimelineProjectV2File(int Schema, string Id, string Name, DateTimeOffset UpdatedUtc);
}

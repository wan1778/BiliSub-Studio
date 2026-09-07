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
            DateTimeOffset.UtcNow,
            [],
            null,
            [],
            [],
            [],
            [],
            [],
            null,
            [],
            [],
            []);
        await SaveAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task SaveAsync(TimelineProjectV2 project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
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
            CleanupAbandonedTemporaries();
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

    // G19 crash recovery: a crash between FileStream create and File.Move
    // leaves one atomic-write temporary behind. ListAsync runs after every
    // restart, so abandoned temporaries (never a live write) are reaped here.
    private void CleanupAbandonedTemporaries()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "*.json.tmp-*", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(path); } catch { }
        }
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
                TimelineProjectV2.Schema, project.Id, project.Name, project.UpdatedUtc,
                project.Assets, project.Sequence, project.Items, project.SubtitleCues,
                project.AudioAssets, project.SourceAudio, project.VoiceCues, project.Music,
                project.Tracks, project.Transitions, project.TextItems);
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
        // G21: schema 4 joins 2/3 as a legacy input schema; all upgrade
        // forward-only to the current schema with explicit defaults.
        if (payload.Schema is not 2 and not 3 and not 4 and not TimelineProjectV2.Schema
            || !string.Equals(payload.Id, id, StringComparison.Ordinal))
            throw new InvalidDataException("Timeline V2 project không đúng phiên bản.");
        if (payload.Schema == 2 && payload.Items is { Count: > 0 })
            throw new InvalidDataException("Timeline V2 schema 2 không được chứa items.");
        // G8 audio state first appears in schema 4; schemas 2/3 must not carry it.
        if (payload.Schema is 2 or 3
            && (payload.SubtitleCues is not null || payload.AudioAssets is not null
                || payload.SourceAudio is not null || payload.VoiceCues is not null
                || payload.Music is not null))
            throw new InvalidDataException("Timeline V2 schema cũ không được chứa audio state G8.");
        var items = payload.Schema == 2
            ? []
            : payload.Items ?? throw new InvalidDataException("Timeline V2 schema 3+ thiếu items.");
        var clips = payload.Sequence?.VideoClips ?? [];
        var legacySchema = payload.Schema < TimelineProjectV2.Schema;
        var preAudioSchema = payload.Schema is 2 or 3;
        var subtitleCues = preAudioSchema
            ? []
            : payload.SubtitleCues ?? throw new InvalidDataException("Timeline V2 schema 4 thiếu subtitle_cues.");
        var audioAssets = preAudioSchema
            ? []
            : payload.AudioAssets ?? throw new InvalidDataException("Timeline V2 schema 4 thiếu audio_assets.");
        var sourceAudio = preAudioSchema
            ? TimelineSourceAudioState.Defaults(clips)
            : payload.SourceAudio ?? throw new InvalidDataException("Timeline V2 schema 4 thiếu source_audio.");
        var voiceCues = preAudioSchema
            ? []
            : payload.VoiceCues ?? throw new InvalidDataException("Timeline V2 schema 4 thiếu voice_cues.");
        var music = preAudioSchema ? null : payload.Music;
        // G21 schema 5: older payloads receive explicit empty multi-track state;
        // schema 5 payloads must provide the arrays (possibly empty).
        var tracks = legacySchema
            ? []
            : payload.Tracks ?? throw new InvalidDataException("Timeline V2 schema 5 thiếu tracks.");
        var transitions = legacySchema
            ? []
            : payload.Transitions ?? throw new InvalidDataException("Timeline V2 schema 5 thiếu transitions.");
        var textItems = legacySchema
            ? []
            : payload.TextItems ?? throw new InvalidDataException("Timeline V2 schema 5 thiếu text_items.");
        var project = new TimelineProjectV2(
            payload.Id,
            TimelineProjectV2.ValidateName(payload.Name),
            payload.UpdatedUtc,
            payload.Assets ?? [],
            payload.Sequence,
            items,
            subtitleCues,
            audioAssets,
            sourceAudio,
            voiceCues,
            music,
            tracks,
            transitions,
            textItems);
        project.Validate();
        return project;
    }

    private Task<TimelineProjectV2> ReadStoredFileLockedAsync(string id, CancellationToken cancellationToken) =>
        ReadStoredFileAsync(id, cancellationToken);

    private static bool IsId(string? id) =>
        id is { Length: 32 } && id.All(character => char.IsAsciiHexDigit(character));

    private sealed record TimelineProjectV2File(
        int Schema,
        string Id,
        string Name,
        DateTimeOffset UpdatedUtc,
        IReadOnlyList<TimelineMediaAsset>? Assets,
        TimelineSequence? Sequence,
        IReadOnlyList<TimelineItem>? Items,
        IReadOnlyList<TimelineSubtitleCue>? SubtitleCues,
        IReadOnlyList<TimelineAudioAsset>? AudioAssets,
        IReadOnlyList<TimelineSourceAudioState>? SourceAudio,
        IReadOnlyList<TimelineVoiceCue>? VoiceCues,
        TimelineMusicTrack? Music,
        IReadOnlyList<TimelineTrackV2>? Tracks = null,
        IReadOnlyList<TimelineTransition>? Transitions = null,
        IReadOnlyList<TimelineTextItem>? TextItems = null);
}

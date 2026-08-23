using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Editor;

public sealed record EditorSourceFingerprint(
    string Path,
    long Size,
    long LastWriteUtcTicks,
    int Width,
    int Height,
    double Duration);

public sealed record EditorSubtitlePlacement(double X, double Y, double Width, double Height)
{
    public static EditorSubtitlePlacement Default { get; } = new(.08, .70, .84, .22);
}

public sealed record EditorAudioSettings(string SourceMode, double SourceGain)
{
    public static EditorAudioSettings Default { get; } = new("keep", 1);
}

public sealed record EditorSubtitleProject(
    string SourcePath,
    long SourceSize,
    long SourceLastWriteUtcTicks,
    string SourceSha256,
    IReadOnlyList<EditorSubtitleCue> Cues,
    EditorSubtitlePlacement Placement,
    string SkillName,
    string SkillSha256,
    string OutputPath);

public sealed record EditorProject(
    int Schema,
    string Id,
    string Name,
    EditorSourceFingerprint Source,
    string FileName,
    IReadOnlyList<EditRegion> Regions,
    EditorSubtitleProject? Subtitle,
    DateTimeOffset UpdatedUtc,
    EditorAudioSettings? Audio = null);

public sealed class EditorProjectStore
{
    public const int CurrentSchema = 3;
    private const long MaxProjectBytes = 64L * 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public EditorProjectStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.Data, "Projects");
    }

    public async Task<EditorProject> LoadOrCreateAsync(
        string inputPath,
        int width,
        int height,
        double duration,
        CancellationToken cancellationToken)
    {
        var source = Fingerprint(inputPath, width, height, duration);
        var id = ProjectId(source.Path);
        var projectPath = ProjectPath(id);
        if (File.Exists(projectPath))
        {
            try
            {
                var info = new FileInfo(projectPath);
                if (info.Length <= 0 || info.Length > MaxProjectBytes)
                    throw new InvalidDataException("Project Editor có kích thước không hợp lệ.");
                await using var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var loaded = await JsonSerializer.DeserializeAsync<EditorProject>(stream, _json, cancellationToken)
                    ?? throw new InvalidDataException("Project Editor rỗng.");
                if (loaded.Schema is not (1 or 2 or CurrentSchema) || !string.Equals(loaded.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Project Editor không đúng phiên bản hoặc nguồn.");
                var regions = NormalizeRegions(loaded.Regions);
                return loaded with
                {
                    Schema = CurrentSchema,
                    Source = source,
                    Regions = regions,
                    Subtitle = NormalizeSubtitle(loaded.Subtitle),
                    Audio = NormalizeAudio(loaded.Audio),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Quarantine(projectPath);
            }
        }

        return new EditorProject(
            CurrentSchema,
            id,
            Path.GetFileNameWithoutExtension(source.Path),
            source,
            Path.GetFileNameWithoutExtension(source.Path) + "_edited.mp4",
            [],
            null,
            DateTimeOffset.UtcNow,
            EditorAudioSettings.Default);
    }

    public async Task SaveAsync(EditorProject project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Schema != CurrentSchema) throw new InvalidDataException("Không thể lưu project Editor khác phiên bản.");
        var expectedId = ProjectId(Path.GetFullPath(project.Source.Path));
        if (!string.Equals(project.Id, expectedId, StringComparison.Ordinal)) throw new InvalidDataException("Project Editor không khớp video nguồn.");
        var normalized = project with
        {
            Source = Fingerprint(project.Source.Path, project.Source.Width, project.Source.Height, project.Source.Duration),
            FileName = string.IsNullOrWhiteSpace(project.FileName) ? project.Name + "_edited.mp4" : project.FileName.Trim(),
            Regions = NormalizeRegions(project.Regions),
            Subtitle = NormalizeSubtitle(project.Subtitle),
            Audio = NormalizeAudio(project.Audio),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = ProjectPath(project.Id);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, _json, cancellationToken);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public string GetProjectPath(string inputPath) => ProjectPath(ProjectId(Path.GetFullPath(inputPath)));

    private static EditorSourceFingerprint Fingerprint(string inputPath, int width, int height, double duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var path = Path.GetFullPath(inputPath.Trim());
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0) throw new FileNotFoundException("Video nguồn của project Editor không tồn tại.", path);
        if (width <= 0 || height <= 0) throw new InvalidDataException("Kích thước video Editor không hợp lệ.");
        return new EditorSourceFingerprint(path, info.Length, info.LastWriteTimeUtc.Ticks, width, height, Math.Max(0, duration));
    }

    private static IReadOnlyList<EditRegion> NormalizeRegions(IReadOnlyList<EditRegion>? source)
    {
        if (source is null) return [];
        if (source.Count > 32) throw new InvalidDataException("Project Editor vượt quá 32 vùng.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<EditRegion>(source.Count);
        foreach (var region in source)
        {
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
                || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
                || !double.IsFinite(region.Start) || !double.IsFinite(region.End)
                || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                || region.X >= 1 || region.Y >= 1 || region.X + region.Width > 1.0001 || region.Y + region.Height > 1.0001)
                throw new InvalidDataException("Project Editor chứa vùng không hợp lệ.");
            var effect = region.Effect?.Trim().ToLowerInvariant();
            if (effect is not ("blur" or "mosaic" or "cover"))
                throw new InvalidDataException("Project Editor chứa hiệu ứng không hỗ trợ.");
            var identity = string.IsNullOrWhiteSpace(region.Id) ? string.Empty : region.Id.Trim();
            if (identity.Length == 0 || identity.Length > 64 || !identities.Add(identity))
            {
                do { identity = Guid.NewGuid().ToString("N"); }
                while (!identities.Add(identity));
            }
            normalized.Add(region with
            {
                Id = identity,
                Effect = effect,
                Strength = Math.Clamp(region.Strength, 2, 64),
            });
        }
        return normalized;
    }

    private static EditorSubtitleProject? NormalizeSubtitle(EditorSubtitleProject? subtitle)
    {
        if (subtitle is null) return null;
        var path = Path.GetFullPath(subtitle.SourcePath.Trim());
        if (!File.Exists(path) || subtitle.SourceSize <= 0 || subtitle.SourceLastWriteUtcTicks <= 0 ||
            subtitle.SourceSha256.Length != 64 || subtitle.SourceSha256.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("Project Editor chứa nguồn SRT không hợp lệ.");
        if (subtitle.Cues is null || subtitle.Cues.Count is 0 or > EditorSubtitleDocument.MaxCues)
            throw new InvalidDataException("Project Editor chứa số cue SRT không hợp lệ.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in subtitle.Cues)
        {
            if (cue.Id.Length is < 8 or > 64 || !ids.Add(cue.Id) || string.IsNullOrWhiteSpace(cue.Number) ||
                string.IsNullOrWhiteSpace(cue.Timing) || string.IsNullOrWhiteSpace(cue.SourceText) || cue.End <= cue.Start)
                throw new InvalidDataException("Project Editor chứa cue SRT không hợp lệ.");
        }
        var placement = subtitle.Placement ?? EditorSubtitlePlacement.Default;
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
            placement.X < 0 || placement.Y < 0 || placement.Width < .05 || placement.Height < .04 ||
            placement.X + placement.Width > 1.0001 || placement.Y + placement.Height > 1.0001)
            throw new InvalidDataException("Project Editor chứa vị trí phụ đề không hợp lệ.");
        return subtitle with
        {
            SourcePath = path,
            Placement = placement,
            SkillName = subtitle.SkillName?.Trim() ?? string.Empty,
            SkillSha256 = subtitle.SkillSha256?.Trim().ToLowerInvariant() ?? string.Empty,
            OutputPath = subtitle.OutputPath?.Trim() ?? string.Empty,
        };
    }

    public static EditorAudioSettings NormalizeAudio(EditorAudioSettings? audio)
    {
        if (audio is null) return EditorAudioSettings.Default;
        var mode = audio.SourceMode?.Trim().ToLowerInvariant();
        if (mode is not ("keep" or "duck" or "mute"))
            throw new InvalidDataException("Project Editor chứa chế độ âm thanh không hợp lệ.");
        if (!double.IsFinite(audio.SourceGain))
            throw new InvalidDataException("Project Editor chứa mức âm thanh không hợp lệ.");
        return mode switch
        {
            "keep" => new EditorAudioSettings("keep", 1),
            "mute" => new EditorAudioSettings("mute", 0),
            _ => new EditorAudioSettings("duck", Math.Clamp(audio.SourceGain, .05, .95)),
        };
    }

    private static string ProjectId(string inputPath)
    {
        var normalized = Path.GetFullPath(inputPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..24];
    }

    private string ProjectPath(string id) => Path.Combine(_directory, id + ".json");

    private static void Quarantine(string path)
    {
        try
        {
            var quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(path, quarantine, overwrite: false);
        }
        catch { }
    }
}

public sealed class EditorRegionDocument
{
    private sealed record Snapshot(IReadOnlyList<EditRegion> Regions, int SelectedIndex);
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly List<EditRegion> _regions = [];

    public IReadOnlyList<EditRegion> Regions => _regions;
    public int SelectedIndex { get; private set; } = -1;
    public EditRegion? Selected => SelectedIndex >= 0 && SelectedIndex < _regions.Count ? _regions[SelectedIndex] : null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Reset(IEnumerable<EditRegion> regions)
    {
        _regions.Clear();
        _regions.AddRange(regions.Select(EnsureIdentity));
        SelectedIndex = _regions.Count == 0 ? -1 : 0;
        _undo.Clear();
        _redo.Clear();
    }

    public void Select(int index) => SelectedIndex = index >= 0 && index < _regions.Count ? index : -1;

    public void BeginChange()
    {
        _undo.Push(new Snapshot(_regions.ToArray(), SelectedIndex));
        while (_undo.Count > 50)
        {
            var ordered = _undo.Reverse().Skip(1).ToArray();
            _undo.Clear();
            foreach (var item in ordered) _undo.Push(item);
        }
        _redo.Clear();
    }

    public void Add(EditRegion region)
    {
        BeginChange();
        _regions.Add(EnsureIdentity(region));
        SelectedIndex = _regions.Count - 1;
    }

    public bool ReplaceSelected(EditRegion region, bool capture = true)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _regions.Count) return false;
        if (capture) BeginChange();
        _regions[SelectedIndex] = EnsureIdentity(region with { Id = _regions[SelectedIndex].Id });
        return true;
    }

    public bool RemoveSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _regions.Count) return false;
        BeginChange();
        _regions.RemoveAt(SelectedIndex);
        if (_regions.Count == 0) SelectedIndex = -1;
        else if (SelectedIndex >= _regions.Count) SelectedIndex = _regions.Count - 1;
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    private bool Restore(Stack<Snapshot> source, Stack<Snapshot> destination)
    {
        if (source.Count == 0) return false;
        destination.Push(new Snapshot(_regions.ToArray(), SelectedIndex));
        var snapshot = source.Pop();
        _regions.Clear();
        _regions.AddRange(snapshot.Regions);
        SelectedIndex = snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < _regions.Count
            ? snapshot.SelectedIndex
            : _regions.Count - 1;
        return true;
    }

    private static EditRegion EnsureIdentity(EditRegion region) => region with
    {
        Id = string.IsNullOrWhiteSpace(region.Id) ? Guid.NewGuid().ToString("N") : region.Id.Trim(),
    };
}

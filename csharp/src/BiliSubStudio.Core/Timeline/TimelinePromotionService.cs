using System.Security.Cryptography;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.Timeline;

public sealed record TimelinePromotionRequest(
    string BackupTag,
    string BackupManifestSha256);

public sealed record TimelinePromotionReport(
    TimelineProjectV2 Project,
    IReadOnlyList<TimelineMigrationWarning> Warnings,
    EditorSourceFingerprint SourceFingerprint,
    string SourceJsonSha256Before,
    string SourceJsonSha256After,
    string MediaSha256Before,
    string MediaSha256After,
    string BackupTag,
    string BackupManifestSha256);

// G11 promotion-readiness owner (DEC-004/005, DEC-021). Copies one real
// schema-7 project into the isolated V2 store with hash evidence that the
// source project file and the source media are never modified. The service
// performs no writes itself: the only writer is TimelineProjectStoreV2, and
// the only readers are SHA-256 snapshots. A recorded production backup
// (BACKUP_PROCEDURE.md) is enforced at the API boundary: empty backup tags
// or malformed manifest hashes are rejected before any probe runs.
public sealed class TimelinePromotionService
{
    private readonly TimelineProjectStoreV2 _store;

    public TimelinePromotionService(TimelineProjectStoreV2 store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public TimelineProjectStoreV2 Store => _store;

    public async Task<TimelinePromotionReport> PromoteAsync(
        EditorProject source,
        string sourceJsonPath,
        string mediaPath,
        int width,
        int height,
        double durationSeconds,
        bool hasAudio,
        TimelinePromotionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ValidateBackupGate(request);
        if (source.Schema != 7)
            throw new InvalidDataException("Timeline promotion chỉ nhận project schema 7.");
        cancellationToken.ThrowIfCancellationRequested();

        var jsonFull = Path.GetFullPath(sourceJsonPath.Trim());
        var mediaFull = Path.GetFullPath(mediaPath.Trim());
        if (!File.Exists(jsonFull))
            throw new InvalidDataException("Không tìm thấy file project schema-7.");
        if (!File.Exists(mediaFull))
            throw new InvalidDataException(
                "Không tìm thấy source media; relink project classic trước khi copy sang V2.");

        var jsonBefore = HashFile(jsonFull);
        var mediaBefore = HashFile(mediaFull);
        var mediaInfo = new FileInfo(mediaFull);

        var migration = TimelineMigrator.MigrateSchema7(
            source,
            mediaFull,
            width,
            height,
            durationSeconds,
            hasAudio,
            mediaInfo.Length,
            mediaInfo.LastWriteTimeUtc.Ticks);
        await _store.SaveAsync(migration.Project, cancellationToken).ConfigureAwait(false);

        var jsonAfter = HashFile(jsonFull);
        var mediaAfter = HashFile(mediaFull);
        if (!string.Equals(jsonBefore, jsonAfter, StringComparison.Ordinal))
            throw new InvalidDataException("File project schema-7 đã thay đổi trong lúc promotion.");
        if (!string.Equals(mediaBefore, mediaAfter, StringComparison.Ordinal))
            throw new InvalidDataException("Source media đã thay đổi trong lúc promotion.");

        return new TimelinePromotionReport(
            migration.Project,
            migration.Warnings,
            migration.SourceFingerprint,
            jsonBefore,
            jsonAfter,
            mediaBefore,
            mediaAfter,
            request.BackupTag.Trim(),
            request.BackupManifestSha256.ToLowerInvariant());
    }

    private static void ValidateBackupGate(TimelinePromotionRequest request)
    {
        var tag = request.BackupTag?.Trim() ?? string.Empty;
        if (tag.Length is 0 or > 120)
            throw new InvalidDataException(
                "Timeline promotion cần backup tag đã ghi nhận (BACKUP_PROCEDURE.md).");
        var manifest = request.BackupManifestSha256 ?? string.Empty;
        if (manifest.Length != 64 || !manifest.All(char.IsAsciiHexDigit)
            || !string.Equals(manifest, manifest.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException(
                "Timeline promotion cần SHA-256 manifest của bản backup production.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

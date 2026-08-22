using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.Maintenance;

public sealed record UpdateInfo(string Current, string Latest, bool Available, IReadOnlyList<string> Notes, bool ChannelReady, string Message);
public sealed record PreparedUpdate(string Version, string PayloadDirectory, string UpdaterExecutable);

public sealed class UpdateService
{
    private const string StableManifestUrl = "https://raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/stable.json";
    private const string BetaManifestUrl = "https://raw.githubusercontent.com/wan1778/BiliSub-Studio/main/update/beta.json";
    private const string GitHubReleasePathPrefix = "/wan1778/BiliSub-Studio/releases/download/";
    private const string RequiredPayloadKind = "winui3-portable-zip";
    private static readonly HashSet<string> PreservedRootDirectories = new(
        ["Data", "Tools", "Temp", "Cache", "Downloads"],
        StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly JobManager _jobs;

    public UpdateService(AppPaths paths, HttpClient http, JobManager jobs)
    {
        _paths = paths; _http = http; _jobs = jobs;
    }

    public string CurrentVersion => typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "4.0.0-beta.12-csharp-p5";

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = await FetchManifestAsync(cancellationToken);
        var notes = manifest.Notes ?? [];
        if (!manifest.ChannelReady)
            return new UpdateInfo(CurrentVersion, manifest.Version, false, notes, false, "Kênh cập nhật GitHub chưa được công bố.");
        if (!string.Equals(manifest.PayloadKind, RequiredPayloadKind, StringComparison.Ordinal))
            return new UpdateInfo(CurrentVersion, manifest.Version, false, notes, false, "Kênh hiện tại chưa có payload C# WinUI 3; không tải nhầm payload không tương thích.");
        ValidateManifest(manifest);
        var available = IsNewerVersion(CurrentVersion, manifest.Version);
        return new UpdateInfo(CurrentVersion, manifest.Version, available, notes, true,
            available ? "Có bản WinUI 3 mới trên GitHub." : "Đang dùng bản mới nhất.");
    }

    public async Task<PreparedUpdate> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_jobs.HasActiveJobs) throw new InvalidOperationException("Đang có tác vụ; hãy hoàn tất hoặc hủy trước khi cập nhật.");
        var manifest = await FetchManifestAsync(cancellationToken);
        if (!manifest.ChannelReady) throw new InvalidOperationException("Kênh cập nhật GitHub chưa được công bố.");
        if (!string.Equals(manifest.PayloadKind, RequiredPayloadKind, StringComparison.Ordinal)) throw new InvalidOperationException("Manifest không phải payload WinUI 3 portable zip.");
        ValidateManifest(manifest);
        if (!IsNewerVersion(CurrentVersion, manifest.Version)) throw new InvalidOperationException("BiliSub Studio đã là phiên bản mới nhất.");
        var updateRoot = Path.Combine(_paths.Temp, "Update", SafeName(manifest.Version));
        var archive = Path.Combine(updateRoot, "payload.zip");
        var staging = Path.Combine(updateRoot, "payload");
        Directory.CreateDirectory(updateRoot);
        await DownloadVerifiedAsync(NormalizeDownloadUrl(manifest.DownloadUrl), archive, manifest.Size, manifest.Sha256, cancellationToken);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ExtractSafe(archive, staging);
        var executable = Directory.EnumerateFiles(staging, "BiliSubStudio.exe", SearchOption.AllDirectories).SingleOrDefault()
            ?? throw new InvalidDataException("Payload update thiếu đúng một BiliSubStudio.exe.");
        ValidatePe(executable);
        var payloadDirectory = Path.GetDirectoryName(executable)!;
        ValidatePayloadLayout(payloadDirectory);
        var updaterDirectory = Path.Combine(updateRoot, "updater-runtime");
        CopyRuntime(AppContext.BaseDirectory, updaterDirectory);
        var updater = Path.Combine(updaterDirectory, "BiliSubStudio.exe");
        if (!File.Exists(updater)) throw new FileNotFoundException("Không tạo được updater runtime.", updater);
        return new PreparedUpdate(manifest.Version, payloadDirectory, updater);
    }

    public void LaunchPrepared(PreparedUpdate update)
    {
        BreakawayLauncher.Start(update.UpdaterExecutable,
        [
            "--apply-portable-update", update.PayloadDirectory, Path.GetFullPath(AppContext.BaseDirectory),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);
    }

    public static async Task<bool> TryApplyFromCommandLineAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var index = Array.IndexOf(arguments, "--apply-portable-update");
        if (index < 0) return false;
        if (arguments.Length <= index + 3) throw new ArgumentException("Thiếu tham số apply portable update.");
        var payload = Path.GetFullPath(arguments[index + 1]);
        var target = Path.GetFullPath(arguments[index + 2]);
        var parentId = int.Parse(arguments[index + 3], System.Globalization.CultureInfo.InvariantCulture);
        ValidatePayloadLayout(payload);
        if (!File.Exists(Path.Combine(target, "BiliSubStudio.exe"))) throw new InvalidDataException("Thư mục đích không phải BiliSub Studio portable hiện có.");
        try { using var parent = Process.GetProcessById(parentId); await parent.WaitForExitAsync(cancellationToken); } catch (ArgumentException) { }
        await Task.Delay(500, cancellationToken);
        try
        {
            await ApplyPayloadTransactionalAsync(payload, target, cancellationToken);
        }
        catch
        {
            TryStartPortable(target);
            throw;
        }
        Process.Start(new ProcessStartInfo(Path.Combine(target, "BiliSubStudio.exe")) { UseShellExecute = true, WorkingDirectory = target });
        return true;
    }

    private async Task<UpdateManifest> FetchManifestAsync(CancellationToken cancellationToken)
    {
        var endpoint = CurrentVersion.Contains('-', StringComparison.Ordinal) ? BetaManifestUrl : StableManifestUrl;
        using var response = await _http.GetAsync(endpoint, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateManifest(CurrentVersion, string.Empty, string.Empty, 0, RequiredPayloadKind,
                ["Kênh cập nhật GitHub chưa được công bố."], false);
        }
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsByteArrayAsync(cancellationToken))
            ?? throw new InvalidDataException("Manifest update GitHub rỗng.");
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.DownloadUrl) || manifest.Size <= 0 ||
            string.IsNullOrWhiteSpace(manifest.Sha256) || !Regex.IsMatch(manifest.Sha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("Manifest update không hợp lệ.");
        _ = NormalizeDownloadUrl(manifest.DownloadUrl);
    }

    private async Task DownloadVerifiedAsync(string url, string path, long expectedSize, string expectedSha256, CancellationToken cancellationToken)
    {
        var temporary = path + ".part";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[256 * 1024];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken); if (read == 0) break;
                total += read; if (total > expectedSize) throw new InvalidDataException("Payload update lớn hơn manifest.");
                hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true); output.Close();
            if (total != expectedSize) throw new InvalidDataException($"Update size {total}, mong đợi {expectedSize}.");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 bản cập nhật không khớp.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void ExtractSafe(string archive, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 50_000) throw new InvalidDataException("Payload update có quá nhiều file.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Payload update giải nén vượt giới hạn 4 GiB.");
            if (entry.FullName.Split('/', '\\').Any(part => part.Contains(':')))
                throw new InvalidDataException("Payload update chứa alternate stream/path không hợp lệ.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload update chứa path traversal.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyRuntime(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            if (!PreservedRootDirectories.Contains(Path.GetFileName(directory))) CopyPayload(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void ValidatePayloadLayout(string payloadDirectory)
    {
        if (!File.Exists(Path.Combine(payloadDirectory, "BiliSubStudio.exe"))) throw new InvalidDataException("Payload update không có BiliSubStudio.exe ở thư mục gốc.");
        var protectedEntry = Directory.EnumerateFileSystemEntries(payloadDirectory)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name is not null && PreservedRootDirectories.Contains(name));
        if (protectedEntry is not null) throw new InvalidDataException($"Payload update không được chứa thư mục dữ liệu được bảo vệ: {protectedEntry}.");
    }

    private static void CopyPayload(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyPayload(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task ApplyPayloadTransactionalAsync(string payload, string target, CancellationToken cancellationToken)
    {
        payload = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Length == 0 || string.Equals(target, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Từ chối cập nhật vào thư mục gốc ổ đĩa.");
        if (!Directory.Exists(payload) || !Directory.Exists(target)) throw new DirectoryNotFoundException("Thiếu thư mục payload hoặc thư mục cài đặt.");
        if (string.Equals(payload, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload update trùng thư mục đích.");
        var targetPrefix = target + Path.DirectorySeparatorChar;
        if (payload.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(target, payload);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (!PreservedRootDirectories.Contains(first))
                throw new InvalidDataException("Payload nằm trong runtime sẽ bị thay thế.");
        }

        ValidatePayloadLayout(payload);
        var targetParent = Directory.GetParent(target)?.FullName;
        var backupRoot = string.Equals(Path.GetFileName(target), AppPaths.InstalledRuntimeDirectoryName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(targetParent)
            ? Path.Combine(targetParent, "Temp", "Update", "rollback-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(target, "Temp", "Update", "rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        try
        {
            MoveUnprotectedRootEntries(target, backupRoot);
            await CopyPayloadDurableAsync(payload, target, cancellationToken);
            ValidatePe(Path.Combine(target, "BiliSubStudio.exe"));
        }
        catch (Exception applyError)
        {
            try
            {
                DeleteUnprotectedRootEntries(target);
                RestoreRootEntries(backupRoot, target);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException($"Cập nhật và rollback đều thất bại. Backup còn tại {backupRoot}.",
                    new AggregateException(applyError, rollbackError));
            }
            ExceptionDispatchInfo.Capture(applyError).Throw();
            throw;
        }
        TryDeleteDirectory(backupRoot);
    }

    private static void MoveUnprotectedRootEntries(string source, string backup)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (PreservedRootDirectories.Contains(name)) continue;
            var destination = Path.Combine(backup, name);
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
    }

    private static void DeleteUnprotectedRootEntries(string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            if (PreservedRootDirectories.Contains(Path.GetFileName(entry))) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    private static void RestoreRootEntries(string backup, string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(backup))
        {
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
    }

    private static async Task CopyPayloadDurableAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyPayloadDurableAsync(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }

    private static string NormalizeDownloadUrl(string value)
    {
        value = value.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("download_url update không hợp lệ.");
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(GitHubReleasePathPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Payload update phải là GitHub Release asset của wan1778/BiliSub-Studio.");
        return uri.ToString();
    }

    private static void ValidatePe(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (file.Length < 256 || reader.ReadUInt16() != 0x5A4D)
            throw new InvalidDataException("BiliSubStudio.exe trong update không phải PE.");
        file.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset < 0x40 || peOffset > file.Length - 26)
            throw new InvalidDataException("BiliSubStudio.exe có PE header offset không hợp lệ.");
        file.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("BiliSubStudio.exe không phải PE x86-64.");
        var sectionCount = reader.ReadUInt16();
        file.Position = peOffset + 20;
        var optionalHeaderSize = reader.ReadUInt16();
        var characteristics = reader.ReadUInt16();
        var sectionTableEnd = peOffset + 24L + optionalHeaderSize + sectionCount * 40L;
        if (sectionCount == 0 || optionalHeaderSize < 0xF0 || sectionTableEnd > file.Length || (characteristics & 0x0002) == 0)
            throw new InvalidDataException("BiliSubStudio.exe có COFF/optional header không hợp lệ.");
        file.Position = peOffset + 24;
        if (reader.ReadUInt16() != 0x020B)
            throw new InvalidDataException("BiliSubStudio.exe không phải PE32+.");
    }

    public static bool IsNewerVersion(string current, string latest)
    {
        static (Version Core, string[] Pre) Parse(string value)
        {
            var withoutBuild = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
            var parts = withoutBuild.Split('-', 2);
            var parsed = Version.TryParse(parts[0], out var version) ? version : new Version(0, 0);
            var core = new Version(Math.Max(0, parsed.Major), Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            var prerelease = parts.Length == 2
                ? Regex.Split(parts[1], @"[.-]|(?<=\D)(?=\d)|(?<=\d)(?=\D)")
                    .Where(identifier => identifier.Length > 0)
                    .ToArray()
                : [];
            return (core, prerelease);
        }
        var left = Parse(current);
        var right = Parse(latest);
        var coreComparison = left.Core.CompareTo(right.Core);
        if (coreComparison != 0) return coreComparison < 0;
        if (left.Pre.Length == 0) return false;
        if (right.Pre.Length == 0) return true;
        for (var index = 0; index < Math.Min(left.Pre.Length, right.Pre.Length); index++)
        {
            var leftNumeric = long.TryParse(left.Pre[index], out var leftNumber);
            var rightNumeric = long.TryParse(right.Pre[index], out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric) comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric != rightNumeric) comparison = leftNumeric ? -1 : 1;
            else comparison = string.Compare(left.Pre[index], right.Pre[index], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison < 0;
        }
        return left.Pre.Length < right.Pre.Length;
    }

    private static string SafeName(string value) => string.Concat(value.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));
    private static void TryStartPortable(string target)
    {
        try
        {
            var executable = Path.Combine(target, "BiliSubStudio.exe");
            if (File.Exists(executable)) Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = target });
        }
        catch { }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch { } }

    private sealed record UpdateManifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("download_url")] string DownloadUrl,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("payload_kind")] string PayloadKind,
        [property: JsonPropertyName("notes")] string[]? Notes,
        [property: JsonPropertyName("channel_ready")] bool ChannelReady);

    private static class BreakawayLauncher
    {
        public static void Start(string executable, IReadOnlyList<string> arguments)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var command = new StringBuilder(Quote(executable), 32_768);
            foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000000 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Không khởi động được updater breakaway.");
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
        }

        private static string Quote(string value)
        {
            var output = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"')
                {
                    output.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                output.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            output.Append('\\', backslashes * 2).Append('"');
            return output.ToString();
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

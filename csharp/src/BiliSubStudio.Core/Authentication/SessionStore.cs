using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Authentication;

public sealed class SessionStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _cookie = string.Empty;

    public SessionStore(AppPaths paths) => _paths = paths;
    public string Cookie => _cookie;
    public bool HasCookie => _cookie.Length > 0;
    public string? LastLoadWarning { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cookie = string.Empty;
            LastLoadWarning = null;
            if (!File.Exists(_paths.SessionFile)) return;
            try
            {
                var encrypted = await File.ReadAllBytesAsync(_paths.SessionFile, cancellationToken);
                _cookie = NormalizeCookie(Encoding.UTF8.GetString(Unprotect(encrypted)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or CryptographicException or InvalidDataException or PlatformNotSupportedException)
            {
                QuarantineInvalidSession();
                LastLoadWarning = "Phiên đăng nhập đã hỏng hoặc không thuộc tài khoản Windows hiện tại; ứng dụng đã cách ly phiên cũ.";
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SetCookieAsync(string raw, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCookie(raw);
        if (normalized.Length == 0) throw new ArgumentException("Cookie rỗng.", nameof(raw));
        var encrypted = Protect(Encoding.UTF8.GetBytes(normalized));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.Data);
            var temporary = _paths.SessionFile + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await file.WriteAsync(encrypted, cancellationToken);
                    await file.FlushAsync(cancellationToken);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _paths.SessionFile, overwrite: true);
            }
            finally { TryDelete(temporary); }
            _cookie = normalized;
            LastLoadWarning = null;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cookie = string.Empty;
            LastLoadWarning = null;
            TryDelete(_paths.SessionFile);
            TryDelete(_paths.SessionFile + ".invalid");
            TryDelete(Path.Combine(_paths.Temp, "bilibili_cookies.txt"));
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> WriteNetscapeFileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cookie.Length == 0) return null;
            Directory.CreateDirectory(_paths.Temp);
            var path = Path.Combine(_paths.Temp, "bilibili_cookies.txt");
            var temporary = path + ".tmp";
            var output = new StringBuilder("# Netscape HTTP Cookie File\n");
            foreach (var part in _cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.Trim().Split('=', 2);
                if (split.Length != 2 || split[0].Trim().Length == 0) continue;
                output.Append(".bilibili.com\tTRUE\t/\tTRUE\t2147483647\t")
                    .Append(split[0].Trim()).Append('\t').Append(split[1].Trim()).Append('\n');
            }
            try
            {
                await File.WriteAllTextAsync(temporary, output.ToString(), new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally { TryDelete(temporary); }
            return path;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteTemporaryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TryDelete(Path.Combine(_paths.Temp, "bilibili_cookies.txt"));
            TryDelete(Path.Combine(_paths.Temp, "bilibili_cookies.txt.tmp"));
        }
        finally { _gate.Release(); }
    }

    public static string NormalizeCookie(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        if (value.Length > 0 && !value.Contains('=') && !value.Contains(';') && !value.Any(char.IsWhiteSpace) && IsCookieValue(value))
            return "SESSDATA=" + value;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Trim().Split('=', 2);
            var name = split[0].Trim();
            var cookieValue = split.Length == 2 ? split[1].Trim() : string.Empty;
            if (split.Length != 2 || !IsCookieName(name) || !IsCookieValue(cookieValue) || !seen.Add(name)) continue;
            output.Add(name + "=" + cookieValue);
        }
        return string.Join("; ", output);
    }

    private static bool IsCookieName(string value) => value.Length > 0 && value.All(character =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
        '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private static bool IsCookieValue(string value) => value.All(character => character >= 0x20 && character != 0x7F && character != ';');

    private static byte[] Protect(byte[] input) => Crypt(input, protect: true);
    private static byte[] Unprotect(byte[] input) => Crypt(input, protect: false);

    private static byte[] Crypt(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI chỉ khả dụng trên Windows.");
        var inputBlob = ToBlob(input);
        DATA_BLOB output = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), protect ? "CryptProtectData" : "CryptUnprotectData");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DATA_BLOB { Length = data.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)] private struct DATA_BLOB { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref DATA_BLOB input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private void QuarantineInvalidSession()
    {
        var quarantine = _paths.SessionFile + ".invalid";
        try
        {
            TryDelete(quarantine);
            File.Move(_paths.SessionFile, quarantine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

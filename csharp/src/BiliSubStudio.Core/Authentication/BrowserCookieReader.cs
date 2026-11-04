using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.IO;
using Microsoft.Data.Sqlite;

namespace BiliSubStudio.Core.Authentication;

/// <summary>A Bilibili session found inside a local browser's cookie store.</summary>
public sealed record BrowserCookieSession(string Browser, string CookieHeader);

/// <summary>
/// Reads the Bilibili session the user already has in a local browser, so
/// logging in on the browser is enough - the app imports that session and saves
/// it through the usual validate + DPAPI path instead of asking anyone to copy
/// a SESSDATA by hand. This is the same source yt-dlp's
/// --cookies-from-browser reads: the browser's own cookie store on this PC.
/// A browser that is not installed, is locked, or stores nothing is skipped -
/// importing a session must never break the rest of the app.
/// </summary>
public static class BrowserCookieReader
{
    private const string HostFilter = "%bilibili.com%";

    // Chromium layout: profile dir → cookies database; both the pre-2024 and
    // the current (Network) file names are supported.
    private static readonly string[] ChromiumCookieFiles = ["Network\\Cookies", "Cookies"];

    private static readonly (string Name, string UserData)[]
        ChromiumBrowsers =
        [
            ("Chrome", "Google\\Chrome\\User Data"),
            ("Edge", "Microsoft\\Edge\\User Data"),
            ("Brave", "BraveSoftware\\Brave-Browser\\User Data"),
        ];

    public static IReadOnlyList<BrowserCookieSession> ReadBilibiliSessions()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = ChromiumBrowsers
            .Select(x => (x.Name, UserData: Path.Combine(localAppData, x.UserData)))
            .ToArray();
        var firefoxProfiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles");
        return ReadBilibiliSessions(roots, firefoxProfiles);
    }

    // Test seam: the same reading logic against chosen cookie stores instead of
    // the machine's installed browsers.
    internal static IReadOnlyList<BrowserCookieSession> ReadBilibiliSessions(
        IReadOnlyList<(string Name, string UserData)> chromiumRoots,
        string? firefoxProfilesRoot)
    {
        var sessions = new List<BrowserCookieSession>();
        foreach (var (name, userData) in chromiumRoots)
            foreach (var header in ReadChromium(userData, name)) sessions.Add(header);
        foreach (var header in ReadFirefox(firefoxProfilesRoot)) sessions.Add(header);
        return sessions;
    }

    private static IEnumerable<BrowserCookieSession> ReadChromium(string userDataRoot, string browser)
    {
        if (!Directory.Exists(userDataRoot)) yield break;
        byte[]? masterKey = null;
        foreach (var profile in EnumerateProfiles(userDataRoot))
        {
            foreach (var cookieFile in ChromiumCookieFiles)
            {
                var path = Path.Combine(profile, cookieFile);
                if (!File.Exists(path)) continue;
                masterKey ??= ReadChromiumMasterKey(Path.Combine(userDataRoot, "Local State"));
                var header = ReadSqlite(path, chromium: true, masterKey);
                if (!string.IsNullOrWhiteSpace(header))
                {
                    yield return new BrowserCookieSession(browser, header);
                    break;
                }
            }
        }
    }

    private static IEnumerable<BrowserCookieSession> ReadFirefox(string? profilesRoot)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
        {
            var path = Path.Combine(profile, "cookies.sqlite");
            if (!File.Exists(path)) continue;
            var header = ReadSqlite(path, chromium: false, masterKey: null);
            if (!string.IsNullOrWhiteSpace(header))
            {
                yield return new BrowserCookieSession("Firefox", header);
                break;
            }
        }
    }

    private static IEnumerable<string> EnumerateProfiles(string userDataRoot)
    {
        var defaults = Path.Combine(userDataRoot, "Default");
        if (Directory.Exists(defaults)) yield return defaults;
        foreach (var profile in Directory.EnumerateDirectories(userDataRoot, "Profile *"))
            yield return profile;
    }

    private static byte[]? ReadChromiumMasterKey(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath)) return null;
            // Local State is JSON, but tolerate a UTF-8 BOM the parser rejects.
            var bytes = File.ReadAllBytes(localStatePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes[3..];
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("os_crypt", out var osCrypt) || osCrypt.ValueKind != JsonValueKind.Object) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String) return null;
            var encrypted = Convert.FromBase64String(keyElement.GetString() ?? string.Empty);
            // The value is DPAPI-protected with a 5-byte "DPAPI" prefix.
            if (encrypted.Length < 6 || Encoding.ASCII.GetString(encrypted, 0, 5) != "DPAPI") return null;
            return WindowsDataProtection.Unprotect(encrypted[5..]);
        }
        catch (Exception)
        {
            // A store we cannot parse (locked/foreign/corrupt) is skipped, never fatal.
            return null;
        }
    }

    // The browser may keep the database open, so read a private copy and never
    // leave temporary files behind.
    private static string? ReadSqlite(string sourcePath, bool chromium, byte[]? masterKey)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "bilisub-cookies-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            var connection = new SqliteConnection($"Data Source={temporary};Mode=ReadOnly;Pooling=false");
            try
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = chromium
                    ? "SELECT name, value, encrypted_value FROM cookies WHERE host_key LIKE $host"
                    : "SELECT name, value, '' FROM moz_cookies WHERE host LIKE $host";
                command.Parameters.AddWithValue("$host", HostFilter);
                var parts = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (chromium && value.Length == 0 && !reader.IsDBNull(2))
                    {
                        var encrypted = (byte[])reader.GetValue(2);
                        var decrypted = DecryptChromiumValue(encrypted, masterKey);
                        if (decrypted is null) continue;
                        value = decrypted;
                    }
                    if (value.Length == 0) continue;
                    parts.Add(name + "=" + value);
                }
                return parts.Count == 0 ? null : string.Join("; ", parts);

            }
            finally
            {
                connection.Close();
                connection.Dispose();
            }
        }
        catch (Exception)
        {
            // A locked/foreign database is skipped, not fatal.
            return null;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static string? DecryptChromiumValue(byte[] encrypted, byte[]? masterKey)
    {
        if (encrypted.Length == 0) return null;
        if (masterKey is null || masterKey.Length == 0)
        {
            return null;
        }
        try
        {
            // v10/v11 = AES-256-GCM with a 12-byte nonce prefix; anything else
            // (legacy DPAPI-only values) decrypted directly.
            if (Encoding.ASCII.GetString(encrypted, 0, 3) is "v10" or "v11")
            {
                var nonce = encrypted.AsSpan(3, 12);
                var cipherWithTag = encrypted.AsSpan(15);
                var cipherSize = cipherWithTag.Length - 16;
                if (cipherSize <= 0) return null;
                var plain = new byte[cipherSize];
                using var aes = new AesGcm(masterKey, 16);
                aes.Decrypt(nonce, cipherWithTag[..cipherSize], cipherWithTag[cipherSize..], plain);
                return Encoding.UTF8.GetString(plain);
            }
            var direct = WindowsDataProtection.Unprotect(encrypted);
            return Encoding.UTF8.GetString(direct);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

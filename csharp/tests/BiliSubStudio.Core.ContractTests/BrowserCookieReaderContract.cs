using System.Security.Cryptography;
using System.Text;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.IO;
using Microsoft.Data.Sqlite;

namespace BiliSubStudio.Core.ContractTests;

/// <summary>
/// BROWSER-SESSION-IMPORT: logging in on the browser is enough. The reader must
/// find the session in a Chromium cookie store (v10 AES-GCM values under the
/// DPAPI master key) and in a Firefox store, keep ONLY bilibili hosts, and
/// never throw when a store is absent or unreadable.
/// </summary>
internal static class BrowserCookieReaderContract
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} (expected {expected}, got {actual})");
    }

    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows-only surface (DPAPI); on other platforms nothing is read.
            Equal(0, BrowserCookieReader.ReadBilibiliSessions().Count, "a non-Windows host must read nothing");
            await Task.CompletedTask;
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "bilisub-browser-cookie-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chromium = Path.Combine(root, "chrome", "User Data");
            var firefox = Path.Combine(root, "firefox", "Profiles");
            var masterKey = RandomNumberGenerator.GetBytes(32);
            WriteChromiumStore(chromium, masterKey);
            WriteFirefoxStore(firefox);

            var sessions = BrowserCookieReader.ReadBilibiliSessions(
                [("Chrome", chromium), ("Edge", Path.Combine(root, "edge", "User Data"))],
                firefox);

                        Equal(2, sessions.Count, "both the Chromium and the Firefox store must yield a session");
            Equal("Chrome", sessions[0].Browser, "the Chromium session must be first");
            var chromeHeader = sessions[0].CookieHeader;
            Require(chromeHeader.Contains("SESSDATA=", StringComparison.Ordinal),
                "the Chromium v10 SESSDATA value was not decrypted");
            Require(chromeHeader.Contains("SESSDATA=abc%3D%3Ddef", StringComparison.Ordinal),
                "the decrypted SESSDATA value is wrong");
            Require(chromeHeader.Contains("bili_jct=", StringComparison.Ordinal),
                "a plaintext Chromium cookie value was not read");
            Require(chromeHeader.Contains("DedeUserID=", StringComparison.Ordinal),
                "the www.bilibili.com host cookie was not read");
            Require(!chromeHeader.Contains("SID=", StringComparison.Ordinal),
                "a non-bilibili cookie leaked into the imported session");
            Equal("Firefox", sessions[1].Browser, "the Firefox session must be second");
            Require(sessions[1].CookieHeader.Contains("SESSDATA=firefox-session", StringComparison.Ordinal),
                "the Firefox store session was not read");

            // A machine with no browser store at all is not an error.
            Equal(0, BrowserCookieReader.ReadBilibiliSessions(
                    [("Chrome", Path.Combine(root, "missing"))],
                    Path.Combine(root, "missing-profiles")).Count,
                "a machine with no browser store must yield no session, not an error");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        await Task.CompletedTask;
    }

    private static void WriteChromiumStore(string userDataRoot, byte[] masterKey)
    {
        var localState = Path.Combine(userDataRoot, "Local State");
        Directory.CreateDirectory(Path.GetDirectoryName(localState)!);
        // Chromium stores base64("DPAPI" + dpapi(master key)) in Local State.
        var protectedMasterKey = WindowsDataProtection.Protect(masterKey);
        var withPrefix = new byte[5 + protectedMasterKey.Length];
        Encoding.ASCII.GetBytes("DPAPI").CopyTo(withPrefix, 0);
        protectedMasterKey.CopyTo(withPrefix, 5);
        var encryptedKey = Convert.ToBase64String(withPrefix);
        File.WriteAllText(localState,
            $"{{\"os_crypt\":{{\"encrypted_key\":\"{encryptedKey}\"}}}}", new UTF8Encoding(false));

        var cookiesDb = Path.Combine(userDataRoot, "Default", "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(cookiesDb)!);
        using var connection = new SqliteConnection($"Data Source={cookiesDb};Pooling=false");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE cookies (host_key TEXT, name TEXT, value TEXT, encrypted_value BLOB)";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO cookies (host_key, name, value, encrypted_value) VALUES ($host, $name, $value, $encrypted)";
            var host = insert.Parameters.AddWithValue("$host", string.Empty);
            var name = insert.Parameters.AddWithValue("$name", string.Empty);
            var value = insert.Parameters.AddWithValue("$value", string.Empty);
            var encrypted = insert.Parameters.AddWithValue("$encrypted", Array.Empty<byte>());
            // The real Bilibili session cookie: AES-256-GCM with the DPAPI master key.
            host.Value = ".bilibili.com";
            name.Value = "SESSDATA";
            value.Value = string.Empty;
            encrypted.Value = EncryptV10(masterKey, Encoding.UTF8.GetBytes("abc%3D%3Ddef"));
            insert.ExecuteNonQuery();
            host.Value = ".bilibili.com";
            name.Value = "bili_jct";
            value.Value = "plain-jct";
            encrypted.Value = Array.Empty<byte>();
            insert.ExecuteNonQuery();
            host.Value = ".www.bilibili.com";
            name.Value = "DedeUserID";
            value.Value = "12345";
            encrypted.Value = Array.Empty<byte>();
            insert.ExecuteNonQuery();
            // Another site's cookie must be filtered out by the host filter.
            host.Value = ".google.com";
            name.Value = "SID";
            value.Value = "secret";
            encrypted.Value = Array.Empty<byte>();
            insert.ExecuteNonQuery();
        }
    }

    private static void WriteFirefoxStore(string profilesRoot)
    {
        var profile = Path.Combine(profilesRoot, "abcd.default-release");
        Directory.CreateDirectory(profile);
        var db = Path.Combine(profile, "cookies.sqlite");
        using var connection = new SqliteConnection($"Data Source={db};Pooling=false");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE moz_cookies (host TEXT, name TEXT, value TEXT)";
        create.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO moz_cookies (host, name, value) VALUES ($host, $name, $value)";
        insert.Parameters.AddWithValue("$host", ".bilibili.com");
        insert.Parameters.AddWithValue("$name", "SESSDATA");
        insert.Parameters.AddWithValue("$value", "firefox-session");
        insert.ExecuteNonQuery();
    }

    private static byte[] EncryptV10(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var output = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(output, 0);
        nonce.CopyTo(output, 3);
        cipher.CopyTo(output, 3 + nonce.Length);
        tag.CopyTo(output, 3 + nonce.Length + cipher.Length);
        return output;
    }
}

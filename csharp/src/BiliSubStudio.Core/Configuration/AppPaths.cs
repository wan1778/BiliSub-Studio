namespace BiliSubStudio.Core.Configuration;

public sealed record AppPaths(
    string Root,
    string Data,
    string Tools,
    string Ocr,
    string Temp,
    string Cache,
    string DefaultDownloads)
{
    public const string InstalledRuntimeDirectoryName = "Runtime";

    public static AppPaths FromRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var absoluteRoot = Path.GetFullPath(root);
        return new AppPaths(
            absoluteRoot,
            Path.Combine(absoluteRoot, "Data"),
            Path.Combine(absoluteRoot, "Tools"),
            Path.Combine(absoluteRoot, "Tools", "OCR"),
            Path.Combine(absoluteRoot, "Temp"),
            Path.Combine(absoluteRoot, "Cache"),
            Path.Combine(absoluteRoot, "Downloads"));
    }

    public static AppPaths FromExecutableDirectory()
    {
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(executableDirectory), InstalledRuntimeDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            var installRoot = Directory.GetParent(executableDirectory)?.FullName
                ?? throw new InvalidOperationException("Không xác định được thư mục cài đặt BiliSub Studio.");
            return FromRoot(installRoot);
        }
        return FromRoot(executableDirectory);
    }

    public string ConfigFile => Path.Combine(Data, "config.json");

    public string SessionFile => Path.Combine(Data, "session.bin");

    public void EnsureBootstrapDirectories()
    {
        // Exact parity with appstate.New: OCR itself is created only by the OCR owner.
        foreach (var directory in new[] { Data, Tools, Temp, Cache, DefaultDownloads })
        {
            Directory.CreateDirectory(directory);
        }
    }
}

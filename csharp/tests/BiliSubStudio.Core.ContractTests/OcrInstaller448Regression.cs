using System.Reflection;
using System.Runtime.CompilerServices;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

internal static class OcrInstaller448Regression
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var installerType = typeof(OcrResult).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OcrInstaller");
        var constructor = installerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(AppPaths), typeof(HttpClient), typeof(ProcessRunner)],
            modifiers: null)
            ?? throw new InvalidOperationException("missing OcrInstaller constructor");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("LocalAppData unavailable in Windows OCR contract");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "BiliSubStudio-Ocr448-Contract");
        var installer = constructor.Invoke([AppPaths.FromRoot(fixtureRoot), new HttpClient(), new ProcessRunner()]);
        var bootstrapProperty = installerType.GetProperty("BootstrapRoot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing LocalAppData OCR bootstrap root");
        var bootstrap = (string)(bootstrapProperty.GetValue(installer) ?? string.Empty);
        if (!Path.GetFullPath(bootstrap).StartsWith(Path.GetFullPath(local), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR bootstrap is not isolated under LocalAppData");
        if (Path.GetFullPath(bootstrap).StartsWith(Path.GetFullPath(fixtureRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR bootstrap still follows the install/portable root");

        var linkFailure = installerType.GetMethod("IsMinorVersionLinkFailure", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing uv minor-version link recovery detector");
        foreach (var message in new[]
                 {
                     "error: Failed to create Python minor version link directory",
                     "path cannot be traversed because it contains an untrusted mount point (os error 448)",
                 })
        {
            if (linkFailure.Invoke(null, [message]) is not true)
                throw new InvalidOperationException("OCR installer no longer recognizes Windows uv junction failure");
        }

        var patchParser = installerType.GetMethod("ManagedPythonPatch", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing patch-version Python recovery parser");
        var valid = (int)(patchParser.Invoke(null, [Path.Combine(local, "cpython-3.12.11-windows-x86_64-none")]) ?? -1);
        var minorLink = (int)(patchParser.Invoke(null, [Path.Combine(local, "cpython-3.12-windows-x86_64-none")]) ?? -1);
        if (valid != 11 || minorLink != -1)
            throw new InvalidOperationException("OCR installer can confuse the broken minor junction with the real patch interpreter");
    }
}

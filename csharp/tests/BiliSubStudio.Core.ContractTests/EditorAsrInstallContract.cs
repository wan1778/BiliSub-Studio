using System.Reflection;
using System.Text.Json.Nodes;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Processes;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorAsrInstallContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-asr-install-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.FromRoot(root);
        var runtime = Path.Combine(paths.Tools, "ASR", "runtime");
        var python = Path.Combine(runtime, "venv", "Scripts", "python.exe");
        var worker = Path.Combine(paths.Tools, "ASR", "worker.py");
        var manifest = Path.Combine(runtime, "install.json");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        try
        {
            // This offline contract tests the manifest/file gate, not inference.
            // Copy an existing real executable for the presence check; never run it.
            File.Copy(Environment.ProcessPath!, python);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "ASR", "worker.py"), worker);
            var assembly = typeof(LocalAsrStatus).Assembly;
            var type = assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrInstaller")!;
            var bootstrapType = assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")!;
            using var http = new HttpClient();
            var processes = new ProcessRunner();
            var bootstrap = Activator.CreateInstance(bootstrapType, paths, http, processes)!;
            using var installer = (IDisposable)Activator.CreateInstance(type, paths, bootstrap, processes)!;
            var write = type.GetMethod("WriteRuntimeManifestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool Matches() => (bool)type.GetMethod("RuntimeMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(installer, null)!;
            void Check(bool valid, string message)
            {
                if (!valid) throw new InvalidOperationException(message);
            }

            Check(!Matches(), "missing ASR manifest accepted");
            await (Task)write.Invoke(installer, [worker, CancellationToken.None])!;
            var original = await File.ReadAllTextAsync(manifest);
            var json = JsonNode.Parse(original)!.AsObject();
            Check(json["schema"]!.GetValue<int>() == 1
                && json["faster_whisper"]!.GetValue<string>() == "1.2.1"
                && json["c_translate2"]!.GetValue<string>() == "4.8.1",
                "writer changed the installed snake_case manifest format");
            Check(Matches(), "ASR installer rejects its own freshly written manifest");
            using (var reopened = (IDisposable)Activator.CreateInstance(type, paths, bootstrap, processes)!)
            {
                var status = (LocalAsrStatus)type.GetProperty("Status")!.GetValue(reopened)!;
                Check(status.RuntimeReady && !status.ModelReady, "reopen must accept runtime without inventing model readiness");
            }
            foreach (var (key, value) in new (string, JsonNode)[]
            {
                ("schema", JsonValue.Create(2)),
                ("faster_whisper", JsonValue.Create("0.0.0")),
                ("c_translate2", JsonValue.Create("0.0.0")),
                ("worker_sha256", JsonValue.Create(new string('0', 64))),
            })
            {
                var invalid = JsonNode.Parse(original)!.AsObject();
                invalid[key] = value;
                await File.WriteAllTextAsync(manifest, invalid.ToJsonString());
                Check(!Matches(), "ASR gate accepted mismatched " + key);
            }
            foreach (var invalid in new[] { "{broken", "null", "{}" })
            {
                await File.WriteAllTextAsync(manifest, invalid);
                Check(!Matches(), "ASR gate accepted invalid/incomplete JSON");
            }
            await File.WriteAllTextAsync(manifest, original);
            var workerBytes = await File.ReadAllBytesAsync(worker);
            await File.AppendAllTextAsync(worker, "\n# changed worker\n");
            Check(!Matches(), "ASR gate accepted changed worker bytes");
            await File.WriteAllBytesAsync(worker, workerBytes);
            Check(Matches(), "restored valid ASR runtime was not reusable");
            File.Delete(python);
            Check(!Matches(), "ASR gate accepted a missing Python executable");
            Check(!Directory.EnumerateFiles(runtime, "install.json.tmp-*").Any(), "manifest write left temporary files");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

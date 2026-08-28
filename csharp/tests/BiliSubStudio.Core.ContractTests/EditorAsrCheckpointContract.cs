using System.Reflection;
using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Real temporary files/Windows handles, not a model or inference test.
internal static class EditorAsrCheckpointContract
{
    private static readonly Type Writer = typeof(LocalAsrStatus).Assembly.GetType("BiliSubStudio.Core.Editor.AsrCheckpointFile")!;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static Task WriteAsync(string path, int generation, CancellationToken token = default, Action<string>? warning = null) =>
        (Task)Writer.GetMethod("WriteAsync", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(JsonElement))
            .Invoke(null, [path, JsonSerializer.SerializeToElement(new { generation }), Json, token, warning])!;

    private static int Generation(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("generation").GetInt32();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-asr-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "normal.json");
            await WriteAsync(path, 1);
            Check(Generation(path) == 1 && !File.Exists(path + ".bak"), "first checkpoint publication failed");
            await WriteAsync(path, 2);
            Check(Generation(path) == 2 && Generation(path + ".bak") == 1, "replacement did not keep prior committed checkpoint");
            Check(!Directory.EnumerateFiles(root, "normal.json.tmp-*").Any(), "successful publication leaked temporary file");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await WriteAsync(path, 3, cancelled.Token); throw new InvalidOperationException("cancelled write succeeded"); }
                catch (OperationCanceledException) { }
            }
            Check(Generation(path) == 2 && !Directory.EnumerateFiles(root, "normal.json.tmp-*").Any(), "pre-cancel mutated checkpoint");
            var retryable = Writer.GetMethod("IsRetryable", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((bool)retryable.Invoke(null, [new UnauthorizedAccessException()])!, "access-denied cannot be retried");
            Check((bool)retryable.Invoke(null, [new IOException("sharing", unchecked((int)0x80070020))])!, "sharing violation cannot be retried");
            Check((bool)retryable.Invoke(null, [new IOException("remove denied", unchecked((int)0x80070497))])!, "replace removal denial cannot be retried");
            Check(!(bool)retryable.Invoke(null, [new IOException("partial replacement", unchecked((int)0x80070499))])!, "partial replacement must retain recovery files rather than retry blindly");
            Check(!(bool)retryable.Invoke(null, [new IOException("disk full", unchecked((int)0x80070070))])!, "disk-full must not be treated as a lock");
            await VerifyBackupLoadAsync(root);
            if (OperatingSystem.IsWindows())
            {
                await VerifySharedReaderAsync(Path.Combine(root, "shared.json"));
                await VerifyTransientLockAsync(Path.Combine(root, "transient.json"));
                await VerifyPermanentLockAsync(Path.Combine(root, "locked.json"));
                await VerifyCancelDuringRetryAsync(Path.Combine(root, "cancel.json"));
                await VerifyReadOnlyAsync(Path.Combine(root, "readonly.json"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifySharedReaderAsync(string path)
    {
        await WriteAsync(path, 1);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await WriteAsync(path, 2);
        Check(Generation(path) == 2 && Generation(path + ".bak") == 1, "delete-sharing reader blocked atomic replace");
    }

    private static async Task VerifyTransientLockAsync(string path)
    {
        await WriteAsync(path, 1);
        var retried = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = WriteAsync(path, 2, warning: _ => retried.TrySetResult(true));
        try
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Generation(path) == 1, "locked checkpoint was overwritten in place");
        }
        finally
        {
            locked.Dispose();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Check(Generation(path) == 2 && Generation(path + ".bak") == 1, "retry did not recover after real handle release");
    }

    private static async Task VerifyPermanentLockAsync(string path)
    {
        await WriteAsync(path, 1);
        await WriteAsync(path, 2);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { await WriteAsync(path, 3); throw new InvalidOperationException("permanent lock was ignored"); }
        catch (IOException error)
        {
            Check(error.Message.Contains(path) && error.Message.Contains("HRESULT=0x")
                && error.Message.Contains(".tmp-") && error.InnerException is not null, "failure lost file/error/recovery details");
        }
        Check(Generation(path) == 2 && Generation(path + ".bak") == 1, "permanent lock lost committed checkpoint");
        var pending = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*").Single();
        Check(Generation(pending) == 3, "fully flushed failed publication was deleted");
    }

    private static async Task VerifyCancelDuringRetryAsync(string path)
    {
        await WriteAsync(path, 1);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = new CancellationTokenSource();
        try
        {
            await WriteAsync(path, 2, cancel.Token, _ => cancel.Cancel());
            throw new InvalidOperationException("cancel during retry was ignored");
        }
        catch (OperationCanceledException) { }
        Check(Generation(path) == 1, "cancel changed committed checkpoint");
        var pending = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*").Single();
        Check(Generation(pending) == 2, "cancel deleted complete recovery snapshot");
    }

    private static async Task VerifyReadOnlyAsync(string path)
    {
        await WriteAsync(path, 1);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            try { await WriteAsync(path, 2); throw new InvalidOperationException("read-only checkpoint was forced writable"); }
            catch (IOException error) { Check(error.Message.Contains("ReadOnly"), "read-only failure omitted attributes"); }
            Check(Generation(path) == 1 && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly), "writer changed user permissions/attributes");
        }
        finally
        {
            // Only this test's files; a failed native replacement may have
            // copied attributes to the retained replacement snapshot.
            File.SetAttributes(path, FileAttributes.Normal);
            foreach (var pending in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*"))
                File.SetAttributes(pending, FileAttributes.Normal);
        }
    }

    private static async Task VerifyBackupLoadAsync(string root)
    {
        var assembly = typeof(LocalAsrStatus).Assembly;
        var serviceType = assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrService")!;
        // This private loader uses only its JSON policy: no installer, model or process is invoked.
        var service = Activator.CreateInstance(serviceType, AppPaths.FromRoot(root), null, null, null, null)!;
        var load = serviceType.GetMethod("LoadCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var revision = assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrInstaller")!
            .GetField("ModelRevision", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!.ToString();
        var path = Path.Combine(root, "load.json");
        var checkpoint = new { schema = 2, key = "expected", model_revision = revision, device = "cpu", compute_type = "int8",
            frontier = 0, complete = false, cues = Array.Empty<object>() };
        async Task<string> DeviceAsync(string key = "expected")
        {
            var task = (Task)load.Invoke(service, [path, key, CancellationToken.None, null])!;
            await task;
            var value = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return (string)value.GetType().GetProperty("Device")!.GetValue(value)!;
        }
        await File.WriteAllTextAsync(path + ".bak", JsonSerializer.Serialize(checkpoint));
        await File.WriteAllTextAsync(path, "{incomplete");
        Check(await DeviceAsync() == "cpu", "valid backup was not recovered after corrupt primary");
        Check(await DeviceAsync("other-source") == "", "backup bypassed source identity validation");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(checkpoint with { device = "cuda" }));
        Check(await DeviceAsync() == "cuda", "backup superseded valid primary");
        File.Delete(path);
        Check(await DeviceAsync() == "cpu", "missing primary did not recover valid backup");
        File.Delete(path + ".bak");
        await File.WriteAllTextAsync(path + ".tmp-" + Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(checkpoint));
        Check(await DeviceAsync() == "", "unpublished temporary snapshot was automatically trusted");
    }
}

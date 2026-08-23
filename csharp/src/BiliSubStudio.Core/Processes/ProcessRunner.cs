using System.Diagnostics;
using System.Text;

namespace BiliSubStudio.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunStreamingAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, ValueTask> standardOutputLine,
        IReadOnlyDictionary<string, string?>? environment = null,
        OwnedProcessGroup? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(standardOutputLine);
        var start = BuildStartInfo(executable, arguments, environment);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Không khởi động được {Path.GetFileName(executable)}.");
        }
        using var ownership = owner?.Track(process);
        using var registration = cancellationToken.Register(() => Kill(process));
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var stdout = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                stdout.AppendLine(line);
                await standardOutputLine(line, cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr);
        }
        finally
        {
            Kill(process);
            await ReapAsync(process, stderrTask);
        }
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? standardOutputLine = null,
        OwnedProcessGroup? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var start = BuildStartInfo(executable, arguments, environment);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Không khởi động được {Path.GetFileName(executable)}.");
        }
        using var ownership = owner?.Track(process);

        using var registration = cancellationToken.Register(() => Kill(process));
        // Drain stderr for the lifetime of the child. Cancelling the read itself can
        // leave a live child blocked on a full pipe while shutdown is trying to kill it.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var stdout = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                stdout.AppendLine(line);
                standardOutputLine?.Invoke(line);
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr);
        }
        finally
        {
            // Callback/parser failures are just as capable of abandoning ffmpeg or
            // yt-dlp as cancellation. Always terminate and reap the exact child tree.
            Kill(process);
            await ReapAsync(process, stderrTask);
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        }
        return start;
    }

    public async Task<byte[]> CaptureBytesAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        OwnedProcessGroup? owner = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Không khởi động được {Path.GetFileName(executable)}.");
        }
        using var ownership = owner?.Track(process);
        using var registration = cancellationToken.Register(() => Kill(process));
        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(executable)}: {error.Trim()}");
            }
            return output.ToArray();
        }
        finally
        {
            Kill(process);
            await ReapAsync(process, errorTask);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task ReapAsync(Process process, Task<string> stderrTask)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { _ = await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }
}

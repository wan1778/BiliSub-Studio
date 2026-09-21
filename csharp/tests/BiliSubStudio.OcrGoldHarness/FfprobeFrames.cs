using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BiliSubStudio.OcrGoldHarness;

internal sealed record ProbeFrame(long Pts, double PtsTime, double? Duration);
internal sealed record FrameInventory(
    int TimeBaseNum,
    int TimeBaseDen,
    double StreamStartTime,
    IReadOnlyList<ProbeFrame> Frames,
    string FfprobeVersion,
    IReadOnlyList<string> Command)
{
    public ProbeFrame Resolve(long pts, double epsilon)
    {
        var index = ResolveIndex(pts);
        var expectedTime = pts * TimeBaseNum / (double)TimeBaseDen;
        if (Math.Abs(Frames[index].PtsTime - expectedTime) > epsilon)
            throw new ManifestException($"PTS {pts} disagrees with ffprobe pts_time by more than {epsilon.ToString("R", CultureInfo.InvariantCulture)} seconds.");
        return Frames[index];
    }

    public int ResolveIndex(long pts)
    {
        var matches = Frames.Select((frame, index) => (frame, index)).Where(item => item.frame.Pts == pts).ToArray();
        if (matches.Length != 1) throw new ManifestException($"PTS {pts} does not identify exactly one decoded source frame.");
        return matches[0].index;
    }

    public int NearestIndex(double ptsTime)
    {
        if (Frames.Count == 0) throw new InvalidOperationException("Cannot snap timing without decoded frames.");
        return Enumerable.Range(0, Frames.Count).MinBy(index => Math.Abs(Frames[index].PtsTime - ptsTime));
    }
}

internal static class FfprobeFrames
{
    public static async Task<FrameInventory> ReadAsync(string executable, string videoPath, CancellationToken cancellationToken)
    {
        executable = ResolveExecutable(executable);
        var version = await RunAsync(executable, ["-version"], cancellationToken);
        var arguments = new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames",
            "-show_entries", "stream=time_base,start_time:frame=pts,best_effort_timestamp,best_effort_timestamp_time,pkt_duration_time",
            "-of", "json", videoPath,
        };
        var output = await RunAsync(executable, arguments, cancellationToken);
        JsonDocument document;
        try { document = JsonDocument.Parse(output.StandardOutput); }
        catch (JsonException error) { throw new MissingInputException("ffprobe returned invalid JSON.", error); }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1)
                throw new MissingInputException("ffprobe did not return exactly one selected video stream.");
            var stream = streams[0];
            var timeBase = RequiredString(stream, "time_base").Split('/');
            if (timeBase.Length != 2
                || !int.TryParse(timeBase[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                || !int.TryParse(timeBase[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
                || num <= 0 || den <= 0)
                throw new MissingInputException("ffprobe returned an invalid video time_base.");
            var startTime = OptionalDouble(stream, "start_time") ?? 0;
            if (!document.RootElement.TryGetProperty("frames", out var framesElement) || framesElement.ValueKind != JsonValueKind.Array)
                throw new MissingInputException("ffprobe returned no frame inventory.");
            var frames = new List<ProbeFrame>();
            foreach (var frame in framesElement.EnumerateArray())
            {
                var pts = OptionalLong(frame, "pts") ?? OptionalLong(frame, "best_effort_timestamp")
                    ?? throw new MissingInputException("A decoded frame has no integer PTS; frame timing cannot be scored without dropping data.");
                var ptsTime = OptionalDouble(frame, "best_effort_timestamp_time")
                    ?? pts * num / (double)den;
                var duration = OptionalDouble(frame, "pkt_duration_time");
                if (!double.IsFinite(ptsTime) || duration is { } finiteDuration && (!double.IsFinite(finiteDuration) || finiteDuration <= 0))
                    throw new MissingInputException("ffprobe returned non-finite frame timing.");
                frames.Add(new ProbeFrame(pts, ptsTime, duration));
            }
            if (frames.Count == 0) throw new MissingInputException("ffprobe decoded zero video frames.");
            for (var index = 1; index < frames.Count; index++)
            {
                if (frames[index].Pts <= frames[index - 1].Pts || frames[index].PtsTime <= frames[index - 1].PtsTime)
                    throw new MissingInputException("Decoded frame PTS are not strictly increasing; refusing to sort or drop frames.");
            }
            return new FrameInventory(num, den, startTime, frames,
                version.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown",
                new[] { executable }.Concat(arguments).ToArray());
        }
    }

    private static string ResolveExecutable(string executable)
    {
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(executable);
            if (!File.Exists(fullPath)) throw new MissingInputException($"ffprobe does not exist: {fullPath}");
            return fullPath;
        }
        return executable;
    }

    private static async Task<ProcessOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new MissingInputException($"Could not start {executable}.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var result = new ProcessOutput(await stdout, await stderr);
            if (process.ExitCode != 0)
                throw new MissingInputException($"{executable} exited {process.ExitCode}: {result.StandardError.Trim()}");
            return result;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new MissingInputException($"Cannot execute ffprobe '{executable}': {error.Message}", error);
        }
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new MissingInputException($"ffprobe field '{name}' is missing.");

    private static long? OptionalLong(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numeric)) return numeric;
        return property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private static double? OptionalDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric)) return numeric;
        return property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) ? text : null;
    }

    private sealed record ProcessOutput(string StandardOutput, string StandardError);
}

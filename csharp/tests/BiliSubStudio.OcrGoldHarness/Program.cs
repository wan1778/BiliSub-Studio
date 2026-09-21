using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.OcrGoldHarness;

return await EntryPoint.RunAsync(args);

internal static class EntryPoint
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            ScorerSelfTests.Run();
            var segments = ProductionTopology.BuildSegments("balanced", 1, 60, 2, 5);
            if (segments[0].CoreStart != 5 || segments[0].CoreEnd != 35
                || segments[1].CoreStart != 35 || segments[1].CoreEnd != 65
                || segments[0].ScanStart != 5 || segments[1].ScanEnd != 65
                || segments[0].ScanEnd <= 35 || segments[1].ScanStart >= 35)
                throw new InvalidOperationException("Self-test failed: production lane offset/overlap topology.");
            Console.WriteLine("SELF-TEST OK: temporal matcher, Unicode-rune scorer, and offset lane topology");
            return 0;
        }

        RunOptions? options = null;
        try
        {
            Console.Error.WriteLine("HARNESS: parsing arguments.");
            options = RunOptions.Parse(args);
            EnsureExternalScratch(options.Manifest, options.Scratch, options.Report);
            Console.Error.WriteLine($"HARNESS: manifest={options.Manifest} scratch={options.Scratch} report={options.Report}");
            var manifest = GoldManifest.Load(options.Manifest);
            ValidateFixtureFiles(manifest);
            Directory.CreateDirectory(options.Scratch);
            Directory.CreateDirectory(Path.GetDirectoryName(options.Report)!);
            var report = await ExecuteAsync(manifest, options);
            Console.Error.WriteLine($"HARNESS: execute finished status={report.Status} cases={report.Cases.Count}");
            await WriteJsonAtomicAsync(options.Report, report);
            if (report.Status == "pass")
            {
                Console.WriteLine($"PASS: {manifest.CorpusId} ({report.Cases.Count} runtime case(s)); report: {options.Report}");
                return 0;
            }
            Console.Error.WriteLine($"FAIL: {string.Join("; ", report.Failures)}; report: {options.Report}");
            return 1;
        }
        catch (Exception error) when (error is MissingInputException or ManifestException)
        {
            Console.Error.WriteLine("INFRASTRUCTURE ERROR: " + error.Message);
            await TryWriteErrorReportAsync(options, "infrastructure_error", error);
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("ERROR: " + error);
            await TryWriteErrorReportAsync(options, "error", error);
            return 1;
        }
    }

    private static async Task<GoldRunReport> ExecuteAsync(GoldManifest manifest, RunOptions options)
    {
        var appRoot = Path.Combine(options.Scratch, "app");
        var paths = AppPaths.FromRoot(appRoot);
        paths.EnsureBootstrapDirectories();
        var coreAssembly = typeof(BiliSubApplication).Assembly.Location;
        var worker = Path.Combine(AppContext.BaseDirectory, "Assets", "worker.py");
        if (!File.Exists(worker)) throw new MissingInputException($"Candidate OCR worker is missing: {worker}");
        var caseReports = new List<GoldCaseReport>();
        var failures = new List<string>();
        // Deliberately never dispose BiliSubApplication here: its
        // WindowsProcessContainment job kills the whole tree (including this
        // harness process itself) when closed, which would silently swallow
        // the exit code and any buffered report output.
        var application = new BiliSubApplication(paths);
        await application.InitializeAsync();
        var ffprobe = options.Ffprobe ?? await application.Tools.EnsureFfprobeAsync(CancellationToken.None);
        foreach (var goldCase in manifest.Cases)
        {
            var videoPath = ResolveFixturePath(manifest.ManifestPath, goldCase.Video);
            var inventory = await FfprobeFrames.ReadAsync(ffprobe, videoPath, CancellationToken.None);
            var absoluteStart = inventory.StreamStartTime + goldCase.Scan.StartSeconds;
            var absoluteEnd = absoluteStart + goldCase.Scan.DurationSeconds;
            foreach (var cue in goldCase.ExpectedCues)
            {
                var onset = inventory.Resolve(cue.OnsetPts, manifest.Thresholds.PtsEpsilonSeconds);
                var offset = inventory.Resolve(cue.OffsetPts, manifest.Thresholds.PtsEpsilonSeconds);
                if (onset.PtsTime + manifest.Thresholds.PtsEpsilonSeconds < absoluteStart
                    || offset.PtsTime >= absoluteEnd - manifest.Thresholds.PtsEpsilonSeconds)
                    throw new ManifestException($"Case '{goldCase.Id}' cue '{cue.Id}' is outside its scan interval.");
            }
            var request = new OcrScanRequest(
                videoPath, goldCase.Scan.Region, goldCase.Scan.Mode, goldCase.Scan.Device,
                goldCase.Scan.Parallelism, goldCase.Scan.Sensitivity,
                goldCase.Scan.DurationSeconds, goldCase.Scan.StartSeconds);
            var jobId = application.StartOcrScan(request, OcrScanStartMode.Fresh);
            if (!application.Jobs.TryGet(jobId, out var job) || job is null)
                throw new InvalidOperationException("The public OCR facade did not register its scan job.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                await job.Completion.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await application.CancelOcrScanAsync(jobId, request, CancellationToken.None);
                throw new TimeoutException($"OCR gold case '{goldCase.Id}' exceeded {options.TimeoutSeconds} seconds.");
            }
            var snapshot = application.Jobs.GetSnapshot(jobId);
            if (!snapshot.Done || snapshot.Status != "done" || snapshot.Result is not OcrScanResult scanResult || scanResult.Paused)
                throw new InvalidOperationException($"OCR gold case '{goldCase.Id}' did not complete: {snapshot.Error ?? snapshot.Message}");

            var expectedDecodedFrames = inventory.Frames.Count(frame =>
                frame.PtsTime + manifest.Thresholds.PtsEpsilonSeconds >= absoluteStart
                && frame.PtsTime < absoluteEnd - manifest.Thresholds.PtsEpsilonSeconds);
            // Lanes are produced with Guard/ActiveGuard overlap windows and cue
            // ownership by CoreStart; Frames sums per-lane decodes. The exact
            // per-frame ownership accounting and lane-completion checks below
            // must therefore use the production segment model (parallelism
            // > 1) rather than assuming scanResult.Frames == unique frames.
            var laneSegments = ProductionTopology.BuildSegments(goldCase.Scan.Mode, goldCase.Scan.Sensitivity,
                goldCase.Scan.DurationSeconds, scanResult.ParallelismSelected, goldCase.Scan.StartSeconds);
            var epsilon = manifest.Thresholds.PtsEpsilonSeconds;
            bool Contains(ProbeFrame frame, double start, double end) =>
                frame.PtsTime + epsilon >= inventory.StreamStartTime + start
                && frame.PtsTime < inventory.StreamStartTime + end - epsilon;
            var expectedLaneFrames = laneSegments.Sum(segment =>
                inventory.Frames.Count(frame => Contains(frame, segment.ScanStart, segment.ScanEnd)));
            var coresTile = Math.Abs(laneSegments[0].CoreStart - goldCase.Scan.StartSeconds) <= epsilon
                && Math.Abs(laneSegments[^1].CoreEnd - goldCase.Scan.StartSeconds - goldCase.Scan.DurationSeconds) <= epsilon
                && laneSegments.All(segment => segment.CoreEnd > segment.CoreStart
                    && segment.ScanStart <= segment.CoreStart && segment.ScanEnd >= segment.CoreEnd)
                && laneSegments.Zip(laneSegments.Skip(1)).All(pair => Math.Abs(pair.First.CoreEnd - pair.Second.CoreStart) <= epsilon)
                && inventory.Frames.All(frame => laneSegments.Count(segment => Contains(frame, segment.CoreStart, segment.CoreEnd))
                    == (Contains(frame, goldCase.Scan.StartSeconds, goldCase.Scan.StartSeconds + goldCase.Scan.DurationSeconds) ? 1 : 0));
            var noFrameDrops = expectedDecodedFrames > 0
                && coresTile
                && scanResult.Frames == expectedLaneFrames
                && scanResult.CompletedLanes == laneSegments.Count
                && (!int.TryParse(goldCase.Scan.Parallelism, out var requestedLanes) || requestedLanes == laneSegments.Count);
            var score = GoldScorer.Score(goldCase, scanResult.Cues, inventory, manifest.Thresholds);
            var caseFailures = score.Metrics.Failures.ToList();
            if (!coresTile)
                caseFailures.Add("production lane segments do not tile the scan interval with contiguous cores");
            if (!noFrameDrops)
                caseFailures.Add($"decoded frame accounting mismatch: production={scanResult.Frames}, laneSegments={expectedLaneFrames}, unique={expectedDecodedFrames}, completedLanes={scanResult.CompletedLanes}/{laneSegments.Count}");
            failures.AddRange(caseFailures.Select(failure => $"{goldCase.Id}: {failure}"));
            caseReports.Add(new GoldCaseReport(
                goldCase.Id,
                goldCase.Video,
                goldCase.VideoSha256,
                goldCase.Provenance,
                new ProbeReport(inventory.TimeBaseNum, inventory.TimeBaseDen, inventory.StreamStartTime,
                    inventory.Frames.Count, expectedDecodedFrames, inventory.FfprobeVersion, inventory.Command),
                new ScanReport(scanResult.Frames, scanResult.OcrImages, scanResult.MediaSeconds,
                    scanResult.ElapsedSeconds, scanResult.RealtimeSpeed, scanResult.ParallelismSelected,
                    scanResult.Decoder, scanResult.WorkerCount, scanResult.WorkerKinds, noFrameDrops,
                    expectedLaneFrames, expectedLaneFrames - expectedDecodedFrames, scanResult.CompletedLanes, laneSegments),
                score,
                scanResult.Cues,
                snapshot.Logs,
                caseFailures.Count == 0 ? "pass" : "fail",
                caseFailures));
        }

        if (caseReports.Count == 0) throw new InvalidOperationException("No runtime corpus case was executed.");
        return new GoldRunReport(
            1,
            failures.Count == 0 ? "pass" : "fail",
            options.Label,
            options.Revision,
            DateTimeOffset.UtcNow,
            manifest.CorpusId,
            manifest.Description,
            Path.GetFullPath(manifest.ManifestPath),
            Path.GetFullPath(options.Scratch),
            new AssemblyReport(coreAssembly, await Sha256Async(coreAssembly),
                typeof(BiliSubApplication).Assembly.GetName().Version?.ToString() ?? "unknown",
                typeof(BiliSubApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"),
            new FileReport(worker, await Sha256Async(worker)),
            manifest.Thresholds,
            caseReports,
            failures);
    }

    private static void ValidateFixtureFiles(GoldManifest manifest)
    {
        foreach (var goldCase in manifest.Cases)
        {
            var path = ResolveFixturePath(manifest.ManifestPath, goldCase.Video);
            if (!File.Exists(path))
                throw new MissingInputException($"Fixture for case '{goldCase.Id}' is missing: {path}. Run csharp/scripts/generate_ocr_gold_fixture.py with an explicit Chinese font.");
            if (new FileInfo(path).Length == 0) throw new MissingInputException($"Fixture for case '{goldCase.Id}' is empty: {path}");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, goldCase.VideoSha256, StringComparison.Ordinal))
                throw new MissingInputException($"Fixture SHA-256 mismatch for case '{goldCase.Id}': expected {goldCase.VideoSha256}, got {actual}.");
        }
    }

    private static string ResolveFixturePath(string manifestPath, string relativePath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var result = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(result, root)) throw new ManifestException($"Fixture escapes the manifest directory: {relativePath}");
        return result;
    }

    private static void EnsureExternalScratch(string manifestPath, string scratchPath, string reportPath)
    {
        var scratch = Path.GetFullPath(scratchPath);
        var report = Path.GetFullPath(reportPath);
        var repo = FindRepositoryRoot(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        if (repo is not null && IsWithin(scratch, repo))
            throw new ManifestException($"Scratch must be outside the repository: {scratch}");
        if (!IsWithin(report, scratch)) throw new ManifestException($"Report must be written inside external scratch: {report}");
    }

    private static string? FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static bool IsWithin(string child, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, ReportJson));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private static async Task TryWriteErrorReportAsync(RunOptions? options, string status, Exception error)
    {
        if (options is null) return;
        try
        {
            EnsureExternalScratch(options.Manifest, options.Scratch, options.Report);
            Directory.CreateDirectory(options.Scratch);
            Directory.CreateDirectory(Path.GetDirectoryName(options.Report)!);
            await WriteJsonAtomicAsync(options.Report, new
            {
                schema_version = 1,
                status,
                label = options.Label,
                revision = options.Revision,
                created_utc = DateTimeOffset.UtcNow,
                error = error.Message,
                exception = error.GetType().FullName,
            });
        }
        catch { }
    }
}

internal sealed record RunOptions(
    string Manifest,
    string Scratch,
    string Report,
    string? Ffprobe,
    string Label,
    string Revision,
    int TimeoutSeconds)
{
    public static RunOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
            throw new ManifestException("Usage: dotnet run --project BiliSubStudio.OcrGoldHarness.csproj -- --manifest PATH --scratch EXTERNAL_PATH --report PATH [--ffprobe PATH] [--label candidate] [--revision VALUE] [--timeout-seconds 7200]");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(["--manifest", "--scratch", "--report", "--ffprobe", "--label", "--revision", "--timeout-seconds"], StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !allowed.Contains(args[index])) throw new ManifestException($"Unknown or valueless argument: {args[index]}");
            if (!values.TryAdd(args[index], args[index + 1])) throw new ManifestException($"Duplicate argument: {args[index]}");
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ManifestException($"{name} is required.");
        var manifest = Path.GetFullPath(Required("--manifest"));
        var scratch = Path.GetFullPath(Required("--scratch"));
        var report = Path.GetFullPath(Required("--report"));
        var ffprobe = values.GetValueOrDefault("--ffprobe");
        var label = values.GetValueOrDefault("--label", "candidate");
        var revision = values.GetValueOrDefault("--revision", "working-tree");
        if (!int.TryParse(values.GetValueOrDefault("--timeout-seconds", "7200"), out var timeout) || timeout is < 1 or > 86400)
            throw new ManifestException("--timeout-seconds must be an integer in [1, 86400].");
        return new RunOptions(manifest, scratch, report, ffprobe, label, revision, timeout);
    }
}

internal sealed record GoldRunReport(
    int SchemaVersion,
    string Status,
    string Label,
    string Revision,
    DateTimeOffset CreatedUtc,
    string CorpusId,
    string Description,
    string ManifestPath,
    string ScratchPath,
    AssemblyReport CoreAssembly,
    FileReport OcrWorker,
    GoldThresholds Thresholds,
    IReadOnlyList<GoldCaseReport> Cases,
    IReadOnlyList<string> Failures);

internal sealed record AssemblyReport(string Path, string Sha256, string AssemblyVersion, string InformationalVersion);
internal sealed record FileReport(string Path, string Sha256);
internal sealed record ProbeReport(int TimeBaseNum, int TimeBaseDen, double StreamStartTime, int TotalFrames, int FramesInScan, string Version, IReadOnlyList<string> Command);
internal sealed record ScanReport(int Frames, int OcrImages, double MediaSeconds, double ElapsedSeconds, double RealtimeSpeed, int Parallelism, string Decoder, int WorkerCount, string WorkerKinds, bool NoFrameDrops, int ExpectedLaneFrames, int ExpectedOverlapFrames, int CompletedLanes, IReadOnlyList<ProductionSegment> Segments);
internal sealed record GoldCaseReport(
    string Id,
    string Video,
    string VideoSha256,
    GoldProvenance Provenance,
    ProbeReport Ffprobe,
    ScanReport ProductionScan,
    ScoreResult Score,
    IReadOnlyList<OcrCue> CandidateCues,
    IReadOnlyList<string> JobLogs,
    string Status,
    IReadOnlyList<string> Failures);

using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Authentication;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Hardware;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Media;
using BiliSubStudio.Core.Maintenance;
using BiliSubStudio.Core.Ocr;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Subtitle;
using BiliSubStudio.Core.Video;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace BiliSubStudio.Core.ContractTests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("default config matches Go baseline", DefaultConfigMatchesGoAsync),
        ("legacy normalization matches appstate", LegacyNormalizationMatchesGoAsync),
        ("partial legacy JSON preserves defaults", PartialLegacyJsonPreservesDefaultsAsync),
        ("config persists and reopens", ConfigPersistsAndReopensAsync),
        ("application boundary validates settings", ApplicationBoundaryValidatesAsync),
        ("concurrent updates serialize file writes", ConcurrentUpdatesAreSerializedAsync),
        ("invalid JSON boots with in-memory defaults", InvalidJsonUsesDefaultsAsync),
        ("download speed maps to true 1 8 16 budgets", DownloadSpeedBudgetsAsync),
        ("video and audio telemetry reports the combined connection budget", DownloadTelemetryContractAsync),
        ("range downloader opens parallel requests and validates bodies", RangeDownloaderParallelismAsync),
        ("completed range resume reports terminal transport progress", RangeCompletedResumeTelemetryAsync),
        ("range cancellation removes unfinished temp artifacts", RangeCancellationCleanupAsync),
        ("process runner reaps child when output callback fails", ProcessRunnerCleanupAsync),
        ("owned process group reaps nested child on cancellation", OwnedProcessGroupCleanupAsync),
        ("media probe parses native preview contract", MediaProbeContractAsync),
        ("subtitle JSON exports deterministic SRT", SubtitleContractAsync),
        ("subtitle VTT and SRT normalize before export", SubtitleTimedTextContractAsync),
        ("editor filter graph preserves normalized regions", EditorFilterContractAsync),
        ("Chinese OCR validator rejects foreign scripts", ChineseOcrContractAsync),
        ("Paddle GPU wheel follows numeric CUDA compatibility", OcrGpuWheelContractAsync),
        ("OCR Auto benchmarks 1 2 4 8 16 and restores last PASS", OcrAutoBenchmarkContractAsync),
        ("OCR Auto resource guard keeps safe 8 and rejects unsafe 16", OcrAutoResourceGuardContractAsync),
        ("QR encoder produces fixed version 10 matrix", QrContractAsync),
        ("session cookie normalization matches legacy", SessionCookieContractAsync),
        ("cookie normalization rejects control-character injection", SessionCookieInjectionContractAsync),
        ("invalid encrypted session is quarantined without blocking startup", InvalidSessionContractAsync),
        ("corrupt OCR checkpoint topology is ignored safely", InvalidOcrCheckpointContractAsync),
        ("job cancellation owns immediate terminal state", JobCancellationContractAsync),
        ("pausable OCR cancellation waits for cleanup completion", PausableJobCancellationContractAsync),
        ("Windows output filename policy rejects reserved names", FileNamePolicyContractAsync),
        ("update version ordering respects prerelease identifiers", UpdateVersionContractAsync),
        ("updater rejects incomplete x64 PE headers", UpdatePeValidationContractAsync),
        ("portable update swaps runtime and preserves data roots", UpdateSwapContractAsync),
        ("portable update rolls back the previous runtime on invalid payload", UpdateRollbackContractAsync),
        ("bug reports redact secrets and Windows user paths", BugReportSanitizationContractAsync),
        ("public OCR API does not expose internal installer", OcrPublicApiContractAsync),
    ];

    private static async Task<int> Main(string[] arguments)
    {
        if (arguments is ["--fixture-hold-open"])
        {
            Console.WriteLine(Environment.ProcessId);
            await Console.Out.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
        if (arguments is ["--fixture-spawn-child"])
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing fixture process path");
            var childStart = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                childStart.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            childStart.ArgumentList.Add("--fixture-hold-open");
            using var child = System.Diagnostics.Process.Start(childStart)
                ?? throw new InvalidOperationException("could not start nested fixture child");
            var reported = await child.StandardOutput.ReadLineAsync()
                ?? throw new InvalidOperationException("nested fixture child did not report its pid");
            Console.WriteLine("nested:" + reported);
            await Console.Out.FlushAsync();
            await child.WaitForExitAsync();
            return child.ExitCode;
        }

        var failures = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}\n      {exception}");
            }
        }

        Console.WriteLine($"{Tests.Count - failures}/{Tests.Count} contract tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task DefaultConfigMatchesGoAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            using var store = new JsonConfigStore(paths);
            await store.InitializeAsync();
            var config = store.Snapshot;

            Equal("dark", config.Theme);
            Equal(paths.DefaultDownloads, config.OutputDirectory);
            Equal("srt", config.SubtitleFormat);
            Equal("fast", config.VideoSpeed);
            Equal("mp4", config.VideoContainer);
            Equal("video+audio", config.VideoMode);
            Equal(true, config.CheckUpdates);
            Equal("auto", config.OcrDevice);
            Equal(65, config.OcrTop);
            Equal(94, config.OcrBottom);
            Equal(5, config.OcrLeft);
            Equal(95, config.OcrRight);
            True(File.Exists(paths.ConfigFile), "Data/config.json was not created");

            var json = await File.ReadAllTextAsync(paths.ConfigFile);
            True(json.EndsWith('\n'), "config.json must end with a single LF");
            foreach (var name in new[]
            {
                "theme", "output_dir", "sub_format", "video_speed", "video_container",
                "video_mode", "check_updates", "ocr_device", "ocr_top", "ocr_bottom",
                "ocr_left", "ocr_right",
            })
            {
                True(json.Contains($"\"{name}\"", StringComparison.Ordinal), $"missing JSON field {name}");
            }
        });
    }

    private static async Task LegacyNormalizationMatchesGoAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            await File.WriteAllTextAsync(paths.ConfigFile, """
                {
                  "theme": "LIGHT",
                  "output_dir": "   ",
                  "sub_format": "ass",
                  "video_speed": "maximum",
                  "video_container": "avi",
                  "video_mode": "everything",
                  "check_updates": false,
                  "ocr_device": " GPU ",
                  "ocr_top": 0,
                  "ocr_bottom": -2,
                  "ocr_left": -1,
                  "ocr_right": 0
                }
                """);

            using var store = new JsonConfigStore(paths);
            await store.InitializeAsync();
            var config = store.Snapshot;
            Equal("dark", config.Theme);
            Equal(paths.DefaultDownloads, config.OutputDirectory);
            Equal("srt", config.SubtitleFormat);
            Equal("fast", config.VideoSpeed);
            Equal("mp4", config.VideoContainer);
            Equal("video+audio", config.VideoMode);
            Equal(false, config.CheckUpdates);
            Equal("gpu", config.OcrDevice);
            Equal(65, config.OcrTop);
            Equal(94, config.OcrBottom);
            Equal(5, config.OcrLeft);
            Equal(95, config.OcrRight);
        });
    }

    private static async Task PartialLegacyJsonPreservesDefaultsAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            await File.WriteAllTextAsync(paths.ConfigFile, "{\"theme\":\"light\"}\n");

            using var store = new JsonConfigStore(paths);
            await store.InitializeAsync();
            Equal("light", store.Snapshot.Theme);
            Equal("fast", store.Snapshot.VideoSpeed);
            Equal("auto", store.Snapshot.OcrDevice);
            Equal(paths.DefaultDownloads, store.Snapshot.OutputDirectory);
        });
    }

    private static async Task ConfigPersistsAndReopensAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            using (var first = new JsonConfigStore(paths))
            {
                await first.InitializeAsync();
                await first.UpdateAsync(config => config with
                {
                    Theme = "light",
                    VideoSpeed = "turbo",
                    OcrDevice = "CPU",
                });
                True(!File.Exists(paths.ConfigFile + ".tmp"), "temporary config file leaked after update");
            }

            using var second = new JsonConfigStore(paths);
            await second.InitializeAsync();
            Equal("light", second.Snapshot.Theme);
            Equal("turbo", second.Snapshot.VideoSpeed);
            Equal("cpu", second.Snapshot.OcrDevice);
        });
    }

    private static async Task ApplicationBoundaryValidatesAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            using var store = new JsonConfigStore(paths);
            var service = new SettingsApplicationService(paths, store, new StorageUsageReader());
            await service.InitializeAsync();

            var output = Path.Combine(root, "CustomOutput");
            var changed = await service.SetOutputDirectoryAsync($"  {output}  ");
            Equal(output, changed.Config.OutputDirectory);
            True(Directory.Exists(output), "application boundary did not create output directory");

            changed = await service.SetThemeAsync(" LIGHT ");
            Equal("light", changed.Config.Theme);
            changed = await service.SetUpdateCheckAsync(false);
            Equal(false, changed.Config.CheckUpdates);

            await ThrowsAsync<ArgumentException>(() => service.SetThemeAsync("system"));
            await ThrowsAsync<ArgumentException>(() => service.SetOutputDirectoryAsync("  "));
        });
    }

    private static async Task ConcurrentUpdatesAreSerializedAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            using var store = new JsonConfigStore(paths);
            await store.InitializeAsync();

            var writes = Enumerable.Range(0, 32).Select(index =>
                store.UpdateAsync(config => config with
                {
                    CheckUpdates = index % 2 == 0,
                    VideoSpeed = index % 3 == 0 ? "turbo" : "fast",
                }));
            await Task.WhenAll(writes);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.ConfigFile));
            True(document.RootElement.TryGetProperty("check_updates", out _), "final JSON is incomplete");

            var finalSnapshot = store.Snapshot;
            using var reopened = new JsonConfigStore(paths);
            await reopened.InitializeAsync();
            Equal(finalSnapshot, reopened.Snapshot);
        });
    }

    private static async Task InvalidJsonUsesDefaultsAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            await File.WriteAllTextAsync(paths.ConfigFile, "{");

            using var store = new JsonConfigStore(paths);
            await store.InitializeAsync();
            Equal("dark", store.Snapshot.Theme);
            True(store.LastLoadWarning is not null, "invalid JSON should surface a non-fatal warning");
            Equal("{", await File.ReadAllTextAsync(paths.ConfigFile));
        });
    }

    private static Task DownloadSpeedBudgetsAsync()
    {
        Equal(1, VideoDownloadService.SpeedConnections("stable"));
        Equal(8, VideoDownloadService.SpeedConnections("fast"));
        Equal(16, VideoDownloadService.SpeedConnections("turbo"));
        Equal(8, VideoDownloadService.SpeedConnections("unknown"));
        return Task.CompletedTask;
    }

    private static Task DownloadTelemetryContractAsync()
    {
        var aggregate = typeof(VideoDownloadService).GetMethod("AggregateTransport", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing transport aggregation contract");
        var statuses = new[]
        {
            new RangeDownloadStatus(true, 7, 7, 70, 100, 7_000),
            new RangeDownloadStatus(true, 1, 1, 10, 20, 1_000),
        };
        var result = (RangeDownloadStatus?)aggregate.Invoke(null, [statuses])
            ?? throw new InvalidOperationException("transport aggregation returned null");
        Equal(8, result.ActiveConnections);
        Equal(8, result.ConfiguredConnections);
        Equal(80L, result.BytesCompleted);
        Equal(120L, result.TotalBytes);
        Equal(8_000d, result.BytesPerSecond);

        var fallback = (RangeDownloadStatus?)aggregate.Invoke(null,
        [
            new[]
            {
                new RangeDownloadStatus(true, 7, 7, 70, 100, 7_000),
                new RangeDownloadStatus(false, 1, 1, 10, 20, 0),
            },
        ]) ?? throw new InvalidOperationException("fallback transport aggregation returned null");
        Equal(false, fallback.RangeSupported);
        Equal(8, fallback.ActiveConnections);
        Equal(8, fallback.ConfiguredConnections);
        return Task.CompletedTask;
    }

    private static async Task RangeDownloaderParallelismAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var payload = new byte[16 * 1024 * 1024];
            for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
            var handler = new RangeFixtureHandler(payload, delayMilliseconds: 25);
            using var client = new HttpClient(handler);
            var downloader = new RangeDownloader(client);
            var stream = FixtureStream(payload.Length);
            var statuses = new List<RangeDownloadStatus>();
            var output = await downloader.DownloadAsync(stream, root, "video", 8, null, status => { lock (statuses) statuses.Add(status); }, CancellationToken.None);
            Equal(payload.Length, checked((int)new FileInfo(output).Length));
            True(handler.PeakActive >= 4, $"expected real concurrent HTTP requests; peak={handler.PeakActive}");
            True(statuses.Count > 0 && statuses.All(x => x.BytesCompleted <= x.TotalBytes), "Range progress exceeded unique payload bytes");
            var actual = await File.ReadAllBytesAsync(output);
            True(payload.AsSpan().SequenceEqual(actual), "assembled Range file differs from source");
        });
    }

    private static async Task RangeCancellationCleanupAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var payload = new byte[8 * 1024 * 1024];
            var handler = new RangeFixtureHandler(payload, delayMilliseconds: 5_000);
            using var client = new HttpClient(handler);
            var downloader = new RangeDownloader(client);
            using var cancellation = new CancellationTokenSource(80);
            await ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(FixtureStream(payload.Length), root, "video", 8, null, null, cancellation.Token));
            True(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "cancel leaked .tmp artifacts");
            True(!Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "cancel leaked .part artifacts");
        });
    }

    private static async Task RangeCompletedResumeTelemetryAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var payload = new byte[2 * 1024 * 1024];
            var handler = new RangeFixtureHandler(payload, delayMilliseconds: 0);
            using var client = new HttpClient(handler);
            var output = Path.Combine(root, "video.stream");
            await File.WriteAllBytesAsync(output, payload);
            var statuses = new List<RangeDownloadStatus>();
            var actual = await new RangeDownloader(client).DownloadAsync(
                FixtureStream(payload.Length), root, "video", 8, null, statuses.Add, CancellationToken.None);
            Equal(output, actual);
            var terminal = statuses[^1];
            Equal((long)payload.Length, terminal.BytesCompleted);
            Equal((long)payload.Length, terminal.TotalBytes);
            Equal(0, terminal.ActiveConnections);
        });
    }

    private static async Task ProcessRunnerCleanupAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing current process path");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add("--fixture-hold-open");
        var childPid = 0;
        await ThrowsAsync<FixtureCallbackException>(() => new ProcessRunner().RunAsync(
            executable, arguments, CancellationToken.None, standardOutputLine: line =>
            {
                childPid = int.Parse(line, System.Globalization.CultureInfo.InvariantCulture);
                throw new FixtureCallbackException();
            }));
        True(childPid > 0, "fixture child did not report a process id");
        try
        {
            using var child = System.Diagnostics.Process.GetProcessById(childPid);
            True(child.HasExited, "process runner abandoned a live child after callback failure");
        }
        catch (ArgumentException)
        {
            // Reaped processes disappear from the process table on Unix.
        }
    }

    private static async Task OwnedProcessGroupCleanupAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing current process path");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add("--fixture-spawn-child");
        await using var owner = new OwnedProcessGroup();
        using var cancellation = new CancellationTokenSource();
        var nestedPid = 0;
        await ThrowsAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            executable, arguments, cancellation.Token, standardOutputLine: line =>
            {
                if (!line.StartsWith("nested:", StringComparison.Ordinal)) return;
                nestedPid = int.Parse(line["nested:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                cancellation.Cancel();
            }, owner: owner));
        await owner.StopAsync();
        Equal(0, owner.ActiveCount);
        True(nestedPid > 0, "nested fixture did not report a process id");
        try
        {
            using var nested = System.Diagnostics.Process.GetProcessById(nestedPid);
            if (!nested.HasExited)
            {
                await nested.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            True(nested.HasExited, "owned process group abandoned a live nested child");
        }
        catch (ArgumentException)
        {
            // Reaped processes disappear from the process table on Unix.
        }
    }

    private static Task MediaProbeContractAsync()
    {
        var info = MediaPreviewService.ParseProbe("""
            {"streams":[{"codec_name":"h264","codec_type":"video","width":1920,"height":1080}],"format":{"duration":"12.5","format_name":"mov,mp4"}}
            """, ".mp4");
        Equal(1920, info.Width); Equal(1080, info.Height); Equal(12.5, info.Duration); Equal(true, info.DirectCompatible);
        return Task.CompletedTask;
    }

    private static Task SubtitleContractAsync()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("""{"body":[{"from":1.0,"to":2.0,"content":"你好"},{"from":2.05,"to":3.0,"content":"你好"}]}""");
        var cues = SubtitleService.ParseCues(raw);
        Equal(1, cues.Count);
        Equal("你好", cues[0].Text);
        True(SubtitleService.RenderSrt(cues).Contains("00:00:01,000 --> 00:00:03,000", StringComparison.Ordinal), "SRT timing drift");
        return Task.CompletedTask;
    }

    private static Task SubtitleTimedTextContractAsync()
    {
        var vtt = System.Text.Encoding.UTF8.GetBytes("WEBVTT\n\n00:01.000 --> 00:02.250 position:50%\n<c.green>你好</c>\n\n00:02.300 --> 00:03.000\n世界\n");
        var cues = SubtitleService.ParseCues(vtt, "vtt");
        Equal(2, cues.Count);
        Equal("你好", cues[0].Text);
        Equal(1d, cues[0].Start);
        var srt = System.Text.Encoding.UTF8.GetBytes("1\r\n00:00:04,000 --> 00:00:05,500\r\n再见\r\n\r\n");
        cues = SubtitleService.ParseCues(srt, "srt");
        Equal(4d, cues[0].Start);
        Equal("再见", cues[0].Text);
        return Task.CompletedTask;
    }

    private static Task EditorFilterContractAsync()
    {
        var graph = VideoEditorService.BuildFilter(new VideoEditRequest("in.mp4", ".", "out.mp4", 1920, 1080, 10,
            [new EditRegion(.1, .2, .3, .25, "mosaic", 12, false, 1, 5)]));
        True(graph.Contains("crop=576:270:192:216", StringComparison.Ordinal), "editor region pixel mapping drift");
        True(graph.Contains("enable='between(t,1.000,5.000)'", StringComparison.Ordinal), "editor timing guard missing");
        return Task.CompletedTask;
    }

    private static Task ChineseOcrContractAsync()
    {
        True(ChineseSubtitleNormalizer.TryNormalize("你好， ，世界！", out var text), "valid Han subtitle rejected");
        Equal("你好，世界！", text);
        True(!ChineseSubtitleNormalizer.TryNormalize("你好 ABC", out _), "Latin garbage entered Chinese SRT");
        True(!ChineseSubtitleNormalizer.TryNormalize("123!?", out _), "non-Han sample accepted");
        return Task.CompletedTask;
    }

    private static Task OcrGpuWheelContractAsync()
    {
        var installer = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrInstaller")
            ?? throw new InvalidOperationException("missing OcrInstaller type");
        var runtimeSpec = installer.GetMethod("RuntimeSpec", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR runtime selector");
        static HardwareSnapshot Hardware(string driver) => new("fixture", 16, 16L << 30, true, "NVIDIA fixture", driver, 8L << 30);
        var cu128 = ((ValueTuple<string, string, string>)runtimeSpec.Invoke(null, ["gpu", Hardware("CUDA 12.8")])!).Item2;
        var cu125 = ((ValueTuple<string, string, string>)runtimeSpec.Invoke(null, ["gpu", Hardware("CUDA 12.5")])!).Item2;
        True(cu128.EndsWith("/cu126/", StringComparison.Ordinal), "CUDA 12.8 must select cu126 wheels");
        True(cu125.EndsWith("/cu118/", StringComparison.Ordinal), "CUDA 12.5 must select compatible cu118 wheels");
        return Task.CompletedTask;
    }

    private static async Task OcrAutoBenchmarkContractAsync()
    {
        var policy = typeof(OcrScanRequest).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrTopologyBenchmark")
            ?? throw new InvalidOperationException("missing OCR topology benchmark policy");
        var levels = policy.GetProperty("Levels", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as IReadOnlyList<int>
            ?? throw new InvalidOperationException("missing OCR topology benchmark levels");
        if (!levels.SequenceEqual([1, 2, 4, 8, 16]))
            throw new InvalidOperationException("OCR Auto ladder is not exactly 1 -> 2 -> 4 -> 8 -> 16");
        var checkpointStore = typeof(OcrScanRequest).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
            ?? throw new InvalidOperationException("missing OCR checkpoint store");
        var buildSegments = checkpointStore.GetMethod("BuildSegments", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("missing OCR checkpoint segment builder");
        var shortSegments = buildSegments.Invoke(null, [90d, 16, 1d]) as System.Collections.ICollection
            ?? throw new InvalidOperationException("OCR checkpoint segment builder returned the wrong collection type");
        if (shortSegments.Count != 16)
            throw new InvalidOperationException("short video duration silently reduced a benchmark-selected 16-pipeline topology");

        var select = policy.GetMethod("SelectAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR topology benchmark selector");
        static async Task<int> InvokeAsync(
            MethodInfo method,
            Func<int, CancellationToken, Task> probe,
            Func<int, CancellationToken, Task> restore,
            Action<int, int, Exception> rejected)
        {
            var pending = method.Invoke(null, [probe, restore, rejected, CancellationToken.None]) as Task<int>
                ?? throw new InvalidOperationException("OCR topology selector returned the wrong task type");
            return await pending;
        }

        var attempted = new List<int>();
        var restored = new List<int>();
        var rejectedLevel = 0;
        var rejectedBest = 0;
        var selected = await InvokeAsync(
            select,
            (level, _) =>
            {
                attempted.Add(level);
                return level == 8
                    ? Task.FromException(new InvalidOperationException("fixture OOM"))
                    : Task.CompletedTask;
            },
            (level, _) => { restored.Add(level); return Task.CompletedTask; },
            (failed, best, _) => { rejectedLevel = failed; rejectedBest = best; });
        if (selected != 4 || !attempted.SequenceEqual([1, 2, 4, 8]) || !restored.SequenceEqual([4]) || rejectedLevel != 8 || rejectedBest != 4)
            throw new InvalidOperationException("OCR Auto did not stop at failed level 8 and restore stable level 4");

        attempted.Clear();
        restored.Clear();
        selected = await InvokeAsync(
            select,
            (level, _) => { attempted.Add(level); return Task.CompletedTask; },
            (level, _) => { restored.Add(level); return Task.CompletedTask; },
            (_, _, _) => throw new InvalidOperationException("all-pass benchmark unexpectedly rejected a level"));
        if (selected != 16 || !attempted.SequenceEqual([1, 2, 4, 8, 16]) || restored.Count != 0)
            throw new InvalidOperationException("OCR Auto did not retain level 16 after every real probe passed");
    }

    private static Task OcrAutoResourceGuardContractAsync()
    {
        var policy = typeof(OcrScanRequest).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrAutoResourcePolicy")
            ?? throw new InvalidOperationException("missing OCR Auto resource policy");
        var evaluate = policy.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR Auto resource evaluator");
        var usefulGain = policy.GetMethod("HasUsefulThroughputGain", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing OCR throughput gain gate");

        static long GiB(double value) => checked((long)(value * 1024 * 1024 * 1024));
        static bool Allowed(object decision) => (bool)(decision.GetType().GetProperty("Allowed")?.GetValue(decision)
            ?? throw new InvalidOperationException("resource decision has no Allowed property"));
        static string Reason(object decision) => (string)(decision.GetType().GetProperty("Reason")?.GetValue(decision)
            ?? throw new InvalidOperationException("resource decision has no Reason property"));
        object Evaluate(HardwareSnapshot hardware, HardwareResourceSnapshot live, int current, int candidate) =>
            evaluate.Invoke(null, [hardware, live, "gpu", current, candidate])
            ?? throw new InvalidOperationException("resource evaluator returned null");

        var laptop = new HardwareSnapshot("fixture", 16, GiB(32), true, "RTX laptop fixture", "CUDA 12.8", GiB(3.75));
        var beforeEight = new HardwareResourceSnapshot(GiB(32), GiB(20), true, GiB(3.75), GiB(2.30));
        var eight = Evaluate(laptop, beforeEight, 4, 8);
        True(Allowed(eight), "safe eight-worker expansion was rejected: " + Reason(eight));

        var beforeSixteen = new HardwareResourceSnapshot(GiB(32), GiB(16), true, GiB(3.75), GiB(0.65));
        var sixteen = Evaluate(laptop, beforeSixteen, 8, 16);
        True(!Allowed(sixteen) && Reason(sixteen).Contains("VRAM", StringComparison.OrdinalIgnoreCase),
            "4 GB-class GPU did not reject unsafe 16-worker expansion by live VRAM headroom");

        var ramBound = new HardwareSnapshot("fixture", 32, GiB(16), true, "NVIDIA fixture", "CUDA 12.8", GiB(24));
        var lowRam = new HardwareResourceSnapshot(GiB(16), GiB(3), true, GiB(24), GiB(20));
        var ramDecision = Evaluate(ramBound, lowRam, 8, 16);
        True(!Allowed(ramDecision) && Reason(ramDecision).Contains("RAM", StringComparison.OrdinalIgnoreCase),
            "unsafe 16-worker expansion ignored live RAM headroom");

        True(!(bool)(usefulGain.Invoke(null, [100d, 105d]) ?? true),
            "five-percent throughput gain incorrectly advanced the OCR ladder");
        True((bool)(usefulGain.Invoke(null, [100d, 111d]) ?? false),
            "eleven-percent throughput gain incorrectly stopped the OCR ladder");
        return Task.CompletedTask;
    }

    private static Task QrContractAsync()
    {
        var matrix = QrMatrixEncoder.Encode("https://passport.bilibili.com/qr/test");
        Equal(57, matrix.Size);
        True(matrix.At(0, 0), "finder pattern missing");
        True(matrix.At(6, 6), "finder center missing");
        return Task.CompletedTask;
    }

    private static Task SessionCookieContractAsync()
    {
        Equal("SESSDATA=token", SessionStore.NormalizeCookie("token"));
        Equal("SESSDATA=a; bili_jct=b", SessionStore.NormalizeCookie("Cookie: SESSDATA=a; bili_jct=b; SESSDATA=duplicate"));
        return Task.CompletedTask;
    }

    private static Task SessionCookieInjectionContractAsync()
    {
        Equal("SESSDATA=good", SessionStore.NormalizeCookie("SESSDATA=good; bad\r\nHeader=value; bili_jct=bad\nvalue"));
        Equal(string.Empty, SessionStore.NormalizeCookie("bare-token\u007F"));
        return Task.CompletedTask;
    }

    private static async Task InvalidSessionContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            await File.WriteAllBytesAsync(paths.SessionFile, "not-dpapi"u8.ToArray());
            var store = new SessionStore(paths);
            await store.LoadAsync();
            True(!store.HasCookie, "invalid DPAPI payload must not create an authenticated session");
            True(store.LastLoadWarning is not null, "invalid DPAPI payload should surface a non-fatal warning");
            True(!File.Exists(paths.SessionFile), "invalid session remained active after quarantine");
            True(File.Exists(paths.SessionFile + ".invalid"), "invalid session was not quarantined");
        });
    }

    private static Task JobCancellationContractAsync()
    {
        using var job = new AppJob("video-test", "video");
        job.Cancel();
        var snapshot = job.Snapshot();
        Equal("cancelled", snapshot.Status);
        Equal(true, snapshot.Done);
        True(job.CancellationToken.IsCancellationRequested, "job token did not cancel");
        return Task.CompletedTask;
    }

    private static async Task PausableJobCancellationContractAsync()
    {
        using var job = new AppJob("ocr-test", "ocrscan", pauseSupported: true);
        job.Cancel();
        var cancelling = job.Snapshot();
        Equal("cancelling", cancelling.Status);
        Equal(false, cancelling.Done);
        True(job.CancellationToken.IsCancellationRequested, "pausable OCR token did not cancel");
        True(!job.Completion.IsCompleted, "pausable OCR became terminal before Core cleanup");
        job.PauseComplete("must not win cancellation race");
        Equal(false, job.Snapshot().Done);

        job.CancelComplete("checkpoint removed");
        await job.Completion;
        var cancelled = job.Snapshot();
        Equal("cancelled", cancelled.Status);
        Equal(true, cancelled.Done);
        Equal("checkpoint removed", cancelled.Message);
    }

    private static async Task InvalidOcrCheckpointContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var source = Path.Combine(root, "fixture.mp4");
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
            var request = new OcrScanRequest(source, new OcrRegion(.05, .65, .9, .29), "balanced", "cpu", "1", 1, 240);
            var storeType = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
                ?? throw new InvalidOperationException("missing OcrCheckpointStore");
            var store = Activator.CreateInstance(storeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [paths], culture: null) ?? throw new InvalidOperationException("cannot create checkpoint store");
            var keyMethod = storeType.GetMethod("KeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing checkpoint identity method");
            var key = await ((Task<string>?)keyMethod.Invoke(store, [request, 4, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint identity did not return a task"));
            var directory = Path.Combine(paths.Data, "OCRCheckpoints");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, key + ".json"), $$"""
                {"schema":4,"key":"{{key}}","selected_parallelism":1,"lanes":[{"segment":{"index":99,"core_start":0,"core_end":240,"scan_start":0,"scan_end":240},"media_seconds":0,"cues":[],"active":null,"frames":0,"ocr_images":0,"completed":false}]}
                """);
            var load = storeType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("missing checkpoint load method");
            var operation = (Task?)load.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint load did not return a task");
            await operation;
            var result = operation.GetType().GetProperty("Result")?.GetValue(operation);
            True(result is null, "invalid lane index was accepted from persisted OCR checkpoint");

            await File.WriteAllTextAsync(Path.Combine(directory, key + ".json"),
                $$"""{"schema":4,"key":"{{key}}","selected_parallelism":1,"lanes":null}""");
            operation = (Task?)load.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint reload did not return a task");
            await operation;
            result = operation.GetType().GetProperty("Result")?.GetValue(operation);
            True(result is null, "null checkpoint lanes were not ignored safely");

            await File.WriteAllTextAsync(Path.Combine(directory, key + ".json"), $$"""
                {"schema":4,"key":"{{key}}","selected_parallelism":1,"lanes":[{"segment":null,"media_seconds":0,"cues":[],"active":null,"frames":0,"ocr_images":0,"completed":false}]}
                """);
            operation = (Task?)load.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint null-segment reload did not return a task");
            await operation;
            result = operation.GetType().GetProperty("Result")?.GetValue(operation);
            True(result is null, "null checkpoint segment was not ignored safely");

            await File.WriteAllTextAsync(Path.Combine(directory, key + ".json"), $$"""
                {"schema":4,"key":"{{key}}","selected_parallelism":1,"lanes":[{"segment":{"index":0,"core_start":0,"core_end":240,"scan_start":0,"scan_end":240},"media_seconds":0,"cues":[null],"active":null,"frames":0,"ocr_images":0,"completed":false}]}
                """);
            operation = (Task?)load.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint null-cue reload did not return a task");
            await operation;
            result = operation.GetType().GetProperty("Result")?.GetValue(operation);
            True(result is null, "null checkpoint cue was not ignored safely");

            var remove = storeType.GetMethod("RemoveAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("missing checkpoint remove method");
            operation = (Task?)remove.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint remove did not return a task");
            await operation;
            True(!File.Exists(Path.Combine(directory, key + ".json")), "checkpoint removal reported success while schema-4 file still existed");
        });
    }

    private static Task FileNamePolicyContractAsync()
    {
        Equal("_CON.mp4", FileNamePolicy.Sanitize("CON.mp4", "fallback.mp4"));
        Equal("a_b_c.mp4", FileNamePolicy.Sanitize("a:b?c.mp4", "fallback.mp4"));
        return Task.CompletedTask;
    }

    private static Task UpdateVersionContractAsync()
    {
        True(UpdateService.IsNewerVersion("4.0.0-beta.12-csharp-p2", "4.0.0-beta.13"), "beta.13 should update beta.12 source checkpoint");
        True(UpdateService.IsNewerVersion("4.0.0-beta.12-csharp-p2", "4.0.0-beta.12-csharp-p10"), "checkpoint p10 should update p2");
        True(UpdateService.IsNewerVersion("4.0.0-beta.13", "4.0.0"), "stable should update prerelease");
        True(!UpdateService.IsNewerVersion("4.0.0", "4.0.0-beta.13"), "prerelease must not replace stable");
        True(!UpdateService.IsNewerVersion("4.0.1", "4.0.0"), "downgrade was accepted");
        True(!UpdateService.IsNewerVersion("4.0.0-beta.13+local", "4.0.0-beta.13+remote"), "build metadata changed version precedence");
        return Task.CompletedTask;
    }

    private static Task UpdatePeValidationContractAsync()
    {
        return WithTemporaryRootAsync(async root =>
        {
            var validate = typeof(UpdateService).GetMethod("ValidatePe", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing PE validator");
            var valid = Path.Combine(root, "valid.exe");
            await File.WriteAllBytesAsync(valid, CreatePeFixture("valid"));
            validate.Invoke(null, [valid]);

            var incompleteBytes = CreatePeFixture("bad");
            BitConverter.GetBytes((ushort)0).CopyTo(incompleteBytes, 0x94);
            var incomplete = Path.Combine(root, "incomplete.exe");
            await File.WriteAllBytesAsync(incomplete, incompleteBytes);
            ThrowsReflection<InvalidDataException>(() => validate.Invoke(null, [incomplete]));

            var truncatedSectionsBytes = CreatePeFixture("bad");
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(truncatedSectionsBytes, 0x86);
            var truncatedSections = Path.Combine(root, "truncated-sections.exe");
            await File.WriteAllBytesAsync(truncatedSections, truncatedSectionsBytes);
            ThrowsReflection<InvalidDataException>(() => validate.Invoke(null, [truncatedSections]));
        });
    }

    private static Task OcrPublicApiContractAsync()
    {
        var publicConstructorParameters = typeof(OcrManager)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        True(publicConstructorParameters.All(type => type.IsVisible), "public OcrManager constructor exposes an internal dependency");
        True(typeof(OcrManager).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Length > 0,
            "OcrManager composition constructor must remain internal to BiliSubStudio.Core");
        return Task.CompletedTask;
    }

    private static Task BugReportSanitizationContractAsync()
    {
        var sanitized = BugReportService.Sanitize(@"Cookie: SESSDATA=secret token=abc C:\Users\Alice\Videos\x.mp4 https://x.test/?auth=hidden&ok=1");
        True(!sanitized.Contains("secret", StringComparison.Ordinal), "SESSDATA leaked from report");
        True(!sanitized.Contains("abc", StringComparison.Ordinal), "token leaked from report");
        True(!sanitized.Contains("Alice", StringComparison.Ordinal), "Windows user name leaked from report");
        True(!sanitized.Contains("hidden", StringComparison.Ordinal), "query secret leaked from report");
        return Task.CompletedTask;
    }

    private static async Task UpdateSwapContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var target = Path.Combine(root, "install");
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(Path.Combine(target, "Data"));
            await File.WriteAllTextAsync(Path.Combine(target, "Data", "config.json"), "preserve");
            await File.WriteAllBytesAsync(Path.Combine(target, "BiliSubStudio.exe"), CreatePeFixture("old"));
            await File.WriteAllTextAsync(Path.Combine(target, "obsolete.dll"), "old");
            await File.WriteAllBytesAsync(Path.Combine(payload, "BiliSubStudio.exe"), CreatePeFixture("new"));
            await File.WriteAllTextAsync(Path.Combine(payload, "runtime.dll"), "new");

            var apply = typeof(UpdateService).GetMethod("ApplyPayloadTransactionalAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing transactional update method");
            var operation = (Task?)apply.Invoke(null, [payload, target, CancellationToken.None])
                ?? throw new InvalidOperationException("transactional update did not return a task");
            await operation;

            Equal("preserve", await File.ReadAllTextAsync(Path.Combine(target, "Data", "config.json")));
            var installed = await File.ReadAllBytesAsync(Path.Combine(target, "BiliSubStudio.exe"));
            True(installed.AsSpan(0x100, 3).SequenceEqual("new"u8), "new PE runtime was not installed");
            Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "runtime.dll")));
            True(!File.Exists(Path.Combine(target, "obsolete.dll")), "obsolete runtime survived transactional swap");
        });
    }

    private static async Task UpdateRollbackContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var target = Path.Combine(root, "install");
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(Path.Combine(target, "Data"));
            await File.WriteAllTextAsync(Path.Combine(target, "Data", "config.json"), "preserve");
            await File.WriteAllBytesAsync(Path.Combine(target, "BiliSubStudio.exe"), CreatePeFixture("old"));
            await File.WriteAllTextAsync(Path.Combine(target, "runtime.dll"), "old-runtime");
            await File.WriteAllBytesAsync(Path.Combine(payload, "BiliSubStudio.exe"), "MZ-invalid"u8.ToArray());
            await File.WriteAllTextAsync(Path.Combine(payload, "runtime.dll"), "bad-runtime");

            var apply = typeof(UpdateService).GetMethod("ApplyPayloadTransactionalAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("missing transactional update method");
            async Task InvokeAsync()
            {
                var operation = (Task?)apply.Invoke(null, [payload, target, CancellationToken.None])
                    ?? throw new InvalidOperationException("transactional update did not return a task");
                await operation;
            }
            await ThrowsAsync<InvalidDataException>(InvokeAsync);

            Equal("preserve", await File.ReadAllTextAsync(Path.Combine(target, "Data", "config.json")));
            Equal("old-runtime", await File.ReadAllTextAsync(Path.Combine(target, "runtime.dll")));
            var restored = await File.ReadAllBytesAsync(Path.Combine(target, "BiliSubStudio.exe"));
            True(restored.AsSpan(0x100, 3).SequenceEqual("old"u8), "previous PE runtime was not restored");
        });
    }

    private static byte[] CreatePeFixture(string marker)
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(bytes, 0x94);
        BitConverter.GetBytes((ushort)0x0022).CopyTo(bytes, 0x96);
        BitConverter.GetBytes((ushort)0x020B).CopyTo(bytes, 0x98);
        System.Text.Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 0x100);
        return bytes;
    }

    private static ResolvedStream FixtureStream(long size) => new(StreamKind.Video, "v1", "https://fixture.invalid/video", new Dictionary<string, string>(), size, 1080, "m4s", 1);

    private sealed class RangeFixtureHandler(byte[] payload, int delayMilliseconds) : HttpMessageHandler
    {
        private int _active;
        private int _peak;
        public int PeakActive => Volatile.Read(ref _peak);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var peak = Volatile.Read(ref _peak);
                if (active <= peak || Interlocked.CompareExchange(ref _peak, active, peak) == peak) break;
            }
            try
            {
                var range = request.Headers.Range?.Ranges.Single() ?? throw new InvalidOperationException("missing Range");
                var start = range.From ?? 0;
                var end = range.To ?? payload.Length - 1;
                var length = checked((int)(end - start + 1));
                if (!(start == 0 && end == 0) && delayMilliseconds > 0)
                    await Task.Delay(Math.Min(25, delayMilliseconds), cancellationToken);
                var bytes = payload.AsSpan(checked((int)start), length).ToArray();
                HttpContent content = start == 0 && end == 0
                    ? new ByteArrayContent(bytes)
                    : new StreamContent(new SlowReadStream(bytes, delayMilliseconds));
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
                content.Headers.ContentLength = length;
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class SlowReadStream(byte[] bytes, int delayMilliseconds) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task WithTemporaryRootAsync(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), $"bilisub-csharp-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await body(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected {expected}; got {actual}");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected exception {typeof(TException).Name}");
    }

    private static void ThrowsReflection<TException>(Action action)
        where TException : Exception
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is TException) { return; }
        throw new InvalidOperationException($"expected reflected exception {typeof(TException).Name}");
    }

    private sealed class FixtureCallbackException : Exception;
}

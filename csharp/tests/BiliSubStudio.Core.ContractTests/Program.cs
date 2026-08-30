using System.Globalization;
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
using BiliSubStudio.Core.Tools;
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
        ("editor audio modes map to exact FFmpeg policy", EditorAudioContractAsync),
        ("editor processed preview accepts arbitrary internal windows and preserves render graph and audio policy", EditorProcessedPreviewContractAsync),
        ("editor processed preview advances every segment to the full source end", EditorFullVideoPlaybackContractAsync),
        ("editor rapid preview requests serialize cleanup and run only the latest request", EditorRapidPreviewRequestContractAsync),
        ("editor preview cache removes normal and crash leftovers", EditorPreviewCacheCleanupContractAsync),
        ("Whisper word timing maps pauses and karaoke ASS", EditorSpeechTimingKaraokeContractAsync),
        ("ASR checkpoint replacement retries file locks and preserves recoverable state", EditorAsrCheckpointContract.RunAsync),
        ("local NghiTTS manifest and rhythm grouping stay pinned", LocalTtsContractAsync),
        ("voice track mixes identically for keep duck mute", EditorVoiceMixContractAsync),
        ("editor final render validates streams duration and audio policy", EditorRenderValidationContractAsync),
        ("Vietnamese TTS text normalization stays deterministic", VietnameseTtsNormalizerContractAsync),
        ("editor document preserves identity through undo redo", EditorDocumentContractAsync),
        ("editor Undo restores ordered region selection and bounded history", EditorUndoContractAsync),
        ("editor Redo restores ordered region selection and invalidates divergent history", EditorRedoContractAsync),
        ("editor Delete preserves neighboring selection and exact undo redo history", EditorDeleteContractAsync),
        ("editor region presets stay pixel valid and add exact whole-video effects", EditorRegionPresetContractAsync),
        ("editor mouse drag creates only pixel-valid regions in either direction", EditorMouseRegionGeometryContractAsync),
        ("editor region selection picks the topmost hit and synchronizes document state", EditorRegionSelectionContractAsync),
        ("editor region move clamps bounds and cancellation leaves no history", EditorRegionMoveContractAsync),
        ("editor region resize keeps all eight handles pixel-valid", EditorRegionResizeContractAsync),
        ("editor numeric X Y W H inputs require source-pixel-valid geometry", EditorRegionNumericInputsContractAsync),
        ("editor blur strength validates input and shares Preview Export radius", EditorBlurStrengthContractAsync),
        ("editor Mosaic strength drives pixelated Preview Export dimensions", EditorMosaicStrengthContractAsync),
        ("editor Cover is opaque strength-free and preserves Preview Export geometry", EditorCoverEffectContractAsync),
        ("editor whole-video scope canonicalizes state and spans Preview Export", EditorWholeVideoScopeContractAsync),
        ("editor timed range validates numeric and current-position Preview Export state", EditorTimedRangeContractAsync),
        ("editor project persists, isolates source drift and quarantines corrupt state", EditorProjectContractAsync),
        ("editor project reopen preserves exact region order geometry identity and source bytes", EditorRegionProjectReopenContractAsync),
        ("editor Preview and Export preserve exact normalized region geometry within one pixel", EditorRegionPreviewExportGeometryContractAsync),
        ("editor SRT keeps exact blocks order and timecodes", EditorSubtitleDocumentContractAsync),
        ("editor manual cue state persists locks and preserves timeline", EditorSubtitleManualContract.RunAsync),
        ("external Vietnamese SRT enables whole-cue voice and survives edit/reopen without translation", EditorVietnameseSrtContract.RunAsync),
        ("editor source selection keeps cancel/same-source transitions safe", EditorSourceSelectionContract.RunAsync),
        ("translation skill bundle is pinned and rejects path traversal", TranslationSkillBundleContractAsync),
        ("local translation manifest and resource gate stay pinned", LocalTranslationManifestContractAsync),
        ("local Chinese ASR model manifest and source SRT stay pinned", LocalAsrManifestContractAsync),
        ("ASR install manifest round-trips and rejects corrupt or mismatched runtime", EditorAsrInstallContract.RunAsync),
        ("ASR private GPU package cache and driver policy reject unsafe state", EditorAsrGpuContract.RunAsync),
        ("Chinese OCR validator rejects foreign scripts", ChineseOcrContractAsync),
        ("OCR touching fragments merge without erasing real short or repeated captions", OcrFragmentContract.RunAsync),
        ("OCR worker crash preserves diagnostics and auto-recovers one request", OcrWorkerRecoveryContract.RunAsync),
        ("Paddle GPU wheel follows numeric CUDA compatibility", OcrGpuWheelContractAsync),
        ("OCR Auto benchmarks 1 2 4 8 16 and restores last PASS", OcrAutoBenchmarkContractAsync),
        ("OCR Auto resource guard keeps safe 8 and rejects unsafe 16", OcrAutoResourceGuardContractAsync),
        ("OCR SRT export includes every final cue beyond live preview window", OcrExportAllCuesContractAsync),
        ("OCR copyts lanes require absolute stop and decoded coverage before completion", OcrLaneCoverageRegression.RunAsync),
        ("QR encoder produces fixed version 10 matrix", QrContractAsync),
        ("session cookie normalization matches legacy", SessionCookieContractAsync),
        ("cookie normalization rejects control-character injection", SessionCookieInjectionContractAsync),
        ("invalid encrypted session is quarantined without blocking startup", InvalidSessionContractAsync),
        ("fresh and corrupt OCR checkpoint state is handled safely", InvalidOcrCheckpointContractAsync),
        ("job cancellation owns immediate terminal state", JobCancellationContractAsync),
        ("pausable OCR cancellation waits for cleanup completion", PausableJobCancellationContractAsync),
        ("cleanup-aware Editor cancellation waits for FFmpeg cleanup", CleanupAwareJobCancellationContractAsync),
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
        if (arguments is ["-u", "fake-ocr-worker", ..])
            return await OcrWorkerRecoveryContract.RunFakeWorkerAsync(arguments);
        if (arguments is ["--ocr-prepare-gpu", var appRootWithImage, var imagePath])
            return await OcrGpuRuntimeContract.PrepareAsync(appRootWithImage, imagePath);
        if (arguments is ["--ocr-prepare-gpu", var appRootOnly])
            return await OcrGpuRuntimeContract.PrepareAsync(appRootOnly);
        if (arguments is ["--ocr-fragments-runtime", var ocrRoot, var ocrVideo])
            return await OcrFragmentRuntimeContract.RunAsync(ocrRoot, ocrVideo);
        if (arguments is ["--asr-voice-runtime", var asrRoot, var asrVideo, var asrSrt])
            return await EditorAsrVoiceRuntimeContract.RunAsync(asrRoot, asrVideo, asrSrt);
        if (arguments is ["--nghi-tts-runtime", var ttsRoot, var ttsVideo])
            return await EditorNghiTtsRuntimeContract.RunAsync(ttsRoot, ttsVideo);
        if (arguments is ["--ocr-lane-ffmpeg", var ffmpeg, var source, var decoder])
            return await OcrLaneCoverageRegression.RunFfmpegAsync(ffmpeg, source, decoder == "nvdec");
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
        var cue = new EditorSubtitleCue("stable-cue", "1", "00:00:01,000 --> 00:00:03,000", 1, 3, "你好", "Xin chào");
        var subtitle = new EditorSubtitleBurn([cue], new EditorSubtitlePlacement(.1, .7, .8, .2));
        var ass = VideoEditorService.BuildAss(subtitle, 1920, 1080);
        True(ass.Contains("Dialogue: 0,0:00:01.00,0:00:03.00,Vietsub", StringComparison.Ordinal), "ASS hardsub timing drift");
        True(ass.Contains("Xin chào", StringComparison.Ordinal), "ASS hardsub text missing");
        graph = VideoEditorService.BuildFilter(new VideoEditRequest("in.mp4", ".", "out.mp4", 1920, 1080, 10, [], subtitle), "C:\\Temp\\sub.ass");
        True(graph.Contains("ass=filename='C\\:/Temp/sub.ass'", StringComparison.Ordinal), "ASS filter path escaping drift");
        return Task.CompletedTask;
    }

    private static Task EditorAudioContractAsync()
    {
        var method = typeof(VideoEditorService).GetMethod("BuildAudioArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing editor audio argument policy");
        static string Arguments(MethodInfo method, EditorAudioSettings settings, bool mp4) =>
            string.Join(' ', (IReadOnlyList<string>)method.Invoke(null, [settings, mp4])!);

        var keep = Arguments(method, new EditorAudioSettings("keep", .2), mp4: false);
        True(keep.Contains("-map 0:a?", StringComparison.Ordinal), "keep mode lost optional source audio map");
        True(keep.Contains("-c:a copy", StringComparison.Ordinal), "MKV keep mode must copy source audio");
        True(!keep.Contains("-af", StringComparison.Ordinal), "keep mode unexpectedly filters audio");

        var duck = Arguments(method, new EditorAudioSettings("duck", .35), mp4: true);
        True(duck.Contains("-af volume=0.350", StringComparison.Ordinal), "duck mode gain drift");
        True(duck.Contains("-c:a aac -b:a 192k", StringComparison.Ordinal), "duck mode must encode filtered audio");

        var mute = Arguments(method, new EditorAudioSettings("mute", 1), mp4: true);
        Equal("-an", mute);
        var graph = VideoEditorService.BuildFilter(new VideoEditRequest(
            "in.mp4", ".", "out.mp4", 1920, 1080, 10, [], null, new EditorAudioSettings("mute", 0)));
        Equal("[0:v]null[vout]", graph);
        return Task.CompletedTask;
    }

    private static Task EditorProcessedPreviewContractAsync()
    {
        var windowMethod = typeof(VideoEditorService).GetMethod("PreviewWindow", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing editor processed-preview window policy");
        var sliceMethod = typeof(VideoEditorService).GetMethod("BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing editor processed-preview slice policy");
        var argumentsMethod = typeof(VideoEditorService).GetMethod("BuildPreviewArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing editor processed-preview FFmpeg policy");
        var subtitle = new EditorSubtitleBurn(
        [
            new EditorSubtitleCue("cue-a", "1", "00:01:39,000 --> 00:01:42,000", 99, 102, "你好", "Xin chào"),
            new EditorSubtitleCue("cue-b", "2", "00:01:45,000 --> 00:01:48,000", 105, 108, "再见", "Tạm biệt"),
            new EditorSubtitleCue("cue-c", "3", "00:01:52,000 --> 00:01:55,000", 112, 115, "以后", "Sau này"),
        ], new EditorSubtitlePlacement(.1, .7, .8, .2),
        [
            new EditorCueSpeechTiming("cue-a", 99, 102, 99.2, 101.8, .2, .2,
                [new EditorWordTiming("你", 99.2, 99.8, .9), new EditorWordTiming("好", 100.2, 101.8, .9)],
                [new EditorPauseTiming(99.8, 100.2)], "male_like", .8, 125),
            new EditorCueSpeechTiming("cue-b", 105, 108, 105.1, 107.7, .1, .3,
                [new EditorWordTiming("再", 105.1, 106, .9), new EditorWordTiming("见", 106.3, 107.7, .9)],
                [new EditorPauseTiming(106, 106.3)], "female_like", .85, 215),
        ], true);
        var request = new VideoEditRequest(
            "input.mp4", ".", "out.mp4", 3840, 2160, 300,
            [
                new EditRegion(.1, .2, .3, .25, "blur", 18, false, 98, 105, "crossing"),
                new EditRegion(.6, .1, .2, .1, "cover", 8, false, 130, 140, "outside"),
            ],
            subtitle,
            new EditorAudioSettings("duck", .35));
        var initialWindow = ((double Start, double Duration))windowMethod.Invoke(null, [request.Duration, 0d])!;
        Equal(0d, initialWindow.Start);
        True(initialWindow.Duration > 0, "initial processed-preview window must be playable");
        const double nearEndTarget = 299;
        var nearEndWindow = ((double Start, double Duration))windowMethod.Invoke(null, [request.Duration, nearEndTarget])!;
        True(nearEndWindow.Start < nearEndTarget, "near-end preview should expose the shifted cache-window case");
        True(nearEndTarget >= nearEndWindow.Start && nearEndTarget < nearEndWindow.Start + nearEndWindow.Duration,
            "near-end seek target must remain addressable inside the selected preview window");
        const double previewStart = 100;
        const double previewDuration = 9.5;
        var sliced = (VideoEditRequest)sliceMethod.Invoke(null, [request, previewStart, previewDuration, 1280, 720])!;
        Equal(1280, sliced.SourceWidth);
        Equal(720, sliced.SourceHeight);
        Equal(previewDuration, sliced.Duration);
        Equal(1, sliced.Regions.Count);
        Equal(0d, sliced.Regions[0].Start);
        Equal(5d, sliced.Regions[0].End);
        Equal(2, sliced.Subtitle?.Cues.Count ?? 0);
        Equal(0d, sliced.Subtitle!.Cues[0].Start);
        Equal(2d, sliced.Subtitle.Cues[0].End);
        Equal(5d, sliced.Subtitle.Cues[1].Start);
        Equal(8d, sliced.Subtitle.Cues[1].End);
        Equal(2, sliced.Subtitle.SpeechTiming?.Count ?? 0);
        Equal(0d, sliced.Subtitle.SpeechTiming![0].CueStart);
        Equal(1.8d, Math.Round(sliced.Subtitle.SpeechTiming[0].SpeechEnd, 1));
        Equal(.2d, Math.Round(sliced.Subtitle.SpeechTiming[0].Words[0].Start, 1));
        var graph = VideoEditorService.BuildFilter(sliced, "C:\\Temp\\preview.ass");
        True(graph.Contains("enable='between(t,0.000,5.000)'", StringComparison.Ordinal), "preview effect time was not shifted to proxy time");
        var ffmpeg = string.Join(' ', (IReadOnlyList<string>)argumentsMethod.Invoke(null,
            ["input.mp4", "preview.mp4", graph, sliced.Audio, previewStart, previewDuration, null])!);
        var expectedWindow = $"-ss {previewStart.ToString("0.000", CultureInfo.InvariantCulture)} -i input.mp4 " +
            $"-t {previewDuration.ToString("0.000", CultureInfo.InvariantCulture)}";
        True(ffmpeg.Contains(expectedWindow, StringComparison.Ordinal), "preview source window drift");
        True(ffmpeg.Contains("-filter_complex", StringComparison.Ordinal) && ffmpeg.Contains("-map [vout]", StringComparison.Ordinal), "preview bypassed render graph");
        True(ffmpeg.Contains("-preset ultrafast", StringComparison.Ordinal) && ffmpeg.Contains("-pix_fmt yuv420p", StringComparison.Ordinal), "preview proxy is not native-player compatible");
        True(ffmpeg.Contains("-af asetpts=PTS-STARTPTS,volume=0.350", StringComparison.Ordinal), "preview bypassed timestamp reset/source-audio duck policy");
        True(ffmpeg.Contains("-movflags +faststart", StringComparison.Ordinal), "preview MP4 faststart contract missing");
        return Task.CompletedTask;
    }

    private static Task EditorFullVideoPlaybackContractAsync()
    {
        var windowMethod = typeof(VideoEditorService).GetMethod("PreviewWindow", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing editor processed-preview window policy");
        var durations = new[] { .04, 1, 11.9, 12, 12.1, 24, 24.5, 25, 299, 300, 3_600 };
        foreach (var sourceDuration in durations)
        {
            var requestedStart = 0d;
            var windows = 0;
            while (true)
            {
                if (++windows > 1_000)
                    throw new InvalidOperationException($"preview segment sequence did not terminate for {sourceDuration}s source");
                var window = ((double Start, double Duration))windowMethod.Invoke(
                    null, [sourceDuration, requestedStart])!;
                var windowEnd = window.Start + window.Duration;
                True(requestedStart >= window.Start - .000_001 && requestedStart <= windowEnd + .000_001,
                    $"preview window skipped requested source time {requestedStart:0.###} for {sourceDuration:0.###}s source");
                var nextStart = VideoEditorService.NextPreviewStart(
                    window.Start, window.Duration, sourceDuration);
                if (nextStart is null)
                {
                    True(windowEnd >= sourceDuration - .05,
                        $"preview stopped at {windowEnd:0.###}s before full source end {sourceDuration:0.###}s");
                    break;
                }
                True(nextStart.Value > requestedStart,
                    $"preview did not advance beyond {requestedStart:0.###}s for {sourceDuration:0.###}s source");
                requestedStart = nextStart.Value;
            }
            if (sourceDuration > 12.05)
                True(windows > 1, $"long source {sourceDuration:0.###}s stopped after its first internal segment");
        }
        return Task.CompletedTask;
    }

    private static async Task EditorRapidPreviewRequestContractAsync()
    {
        var coordinator = new EditorPreviewRequestCoordinator();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeOperations = 0;
        var secondRan = false;
        var thirdRan = false;

        var first = coordinator.RunLatestAsync(async cancellationToken =>
        {
            Equal(1, Interlocked.Increment(ref activeOperations));
            firstStarted.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally
            {
                firstCleanupStarted.TrySetResult(true);
                await releaseFirstCleanup.Task;
                Interlocked.Decrement(ref activeOperations);
            }
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = coordinator.RunLatestAsync(cancellationToken =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });
        var third = coordinator.RunLatestAsync(async cancellationToken =>
        {
            Equal(1, Interlocked.Increment(ref activeOperations));
            try
            {
                thirdRan = true;
                await Task.Yield();
            }
            finally { Interlocked.Decrement(ref activeOperations); }
        });

        await firstCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        True(coordinator.IsActive, "latest preview request stopped owning the queue during prior FFmpeg cleanup");
        releaseFirstCleanup.TrySetResult(true);
        await ThrowsAsync<OperationCanceledException>(() => first);
        await ThrowsAsync<OperationCanceledException>(() => second);
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        True(!secondRan, "superseded queued preview request still started its render");
        True(thirdRan, "latest preview request did not run after prior cleanup");
        Equal(0, activeOperations);
        True(!coordinator.IsActive, "preview request coordinator stayed active after the latest request completed");

        var cancelStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCleanupFinished = false;
        var cancellable = coordinator.RunLatestAsync(async cancellationToken =>
        {
            cancelStarted.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally
            {
                await Task.Delay(20);
                cancelCleanupFinished = true;
            }
        });
        await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
        True(cancelCleanupFinished, "Cancel returned before preview cleanup completed");
        True(!coordinator.IsActive, "Cancel left an active preview request/CTS owner");
        await ThrowsAsync<OperationCanceledException>(() => cancellable);
    }

    private static Task EditorPreviewCacheCleanupContractAsync() => WithTemporaryRootAsync(async root =>
    {
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        var previewDirectory = Path.Combine(paths.Temp, "Editor", "Preview");
        Directory.CreateDirectory(previewDirectory);
        var owned = new[]
        {
            Path.Combine(previewDirectory, "active.mp4"),
            Path.Combine(previewDirectory, "crashed.rendering.mp4"),
            Path.Combine(previewDirectory, "crashed.ass"),
        };
        foreach (var path in owned) await File.WriteAllTextAsync(path, "owned preview artifact");
        var unrelated = Path.Combine(previewDirectory, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "not a managed preview artifact");

        using var http = new HttpClient();
        var service = new VideoEditorService(paths, new ToolManager(paths, http), new ProcessRunner());
        await service.CleanupPreviewCacheAsync();

        True(owned.All(path => !File.Exists(path)), "preview cache cleanup left an owned active/crash artifact");
        True(File.Exists(unrelated), "preview cache cleanup removed an unrelated file type");
    });

    private static async Task EditorSpeechTimingKaraokeContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var cue = new EditorSubtitleCue("timing-cue-0001", "1", "00:00:01,000 --> 00:00:04,000", 1, 4, "你 好", "Xin chào đạo hữu");
            var analysis = new EditorSpeechAnalysis(
                EditorSpeechAnalysisDocument.CurrentSchema,
                new string('a', 64),
                "faster-whisper-small",
                "536b0662742c02347bc0e980a01041f333bce120",
                "cpu",
                "int8",
                .8,
                [new EditorSpeechSegment(1.1, 3.7, "你 好", -.2, .01,
                    [new EditorWordTiming("你", 1.1, 1.6, .95), new EditorWordTiming("好", 2.0, 3.7, .93)],
                    "male_like", .82, 126)]);
            var path = Path.Combine(root, "speech.json");
            var sha = await EditorSpeechAnalysisDocument.SaveAsync(path, analysis, CancellationToken.None);
            var reopened = await EditorSpeechAnalysisDocument.LoadVerifiedAsync(path, sha, CancellationToken.None);
            var timing = EditorSpeechAnalysisDocument.MapToCues(reopened, [cue]);
            Equal(1, timing.Count);
            Equal(2, timing[0].Words.Count);
            Equal(1, timing[0].Pauses.Count);
            Equal(.4d, Math.Round(timing[0].Pauses[0].Duration, 1));
            Equal("male_like", timing[0].VoiceClass);
            var ass = VideoEditorService.BuildAss(new EditorSubtitleBurn([cue], EditorSubtitlePlacement.Default, timing, true), 1920, 1080);
            True(ass.Contains(@"{\kf", StringComparison.Ordinal), "karaoke ASS did not emit word highlight tags");
            True(ass.Contains("Dialogue: 0,0:00:01.10,0:00:03.70,Vietsub", StringComparison.Ordinal), "karaoke ASS ignored speech envelope");
        });
    }

    private static Task LocalTtsContractAsync()
    {
        var assembly = typeof(VideoEditorService).Assembly;
        var installer = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsInstaller")
            ?? throw new InvalidOperationException("missing LocalTtsInstaller type");
        static object? Constant(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetRawConstantValue();
        Equal("1.4.2", Constant(installer, "PiperVersion")?.ToString());
        True((Constant(installer, "PiperWheel")?.ToString() ?? string.Empty).EndsWith("#sha256=9c4a3a11f5889ea9d0df4414dce2bd9bee5ce7d9cf604c8fd5e307441d4c031f", StringComparison.Ordinal),
            "Piper Windows wheel hash drift");
        Equal("62e57b18157ed213b3863a7a8a35b14d3404554b", Constant(installer, "VoiceRevision")?.ToString());
        Equal("deepman3909", Constant(installer, "MaleVoice")?.ToString());
        Equal("calmwoman3688", Constant(installer, "FemaleVoice")?.ToString());
        Equal(63_516_050L, (long)(Constant(installer, "VoiceModelBytes") ?? 0L));
        Equal("1fb3a404e9927c87367d4175e8cad24ffc6d9959af29888c38682e5ec621056c", Constant(installer, "MaleModelSha256")?.ToString());
        Equal("8db60d8afc50dc0921fd3a1b0b942813f44cc3744dbe2534617f2b8726096e7e", Constant(installer, "FemaleModelSha256")?.ToString());

        var service = assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")
            ?? throw new InvalidOperationException("missing LocalTtsService type");
        var method = service.GetMethod("BuildRhythmGroups", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing TTS rhythm grouping policy");
        var cue = new EditorSubtitleCue("rhythm-cue-0001", "1", "00:00:01,000 --> 00:00:05,000", 1, 5, "你好", "Xin chào đạo hữu");
        var timing = new EditorCueSpeechTiming(cue.Id, 1, 5, 1.2, 4.7, .2, .3,
            [new EditorWordTiming("你", 1.2, 2, .9), new EditorWordTiming("好", 3, 4.7, .9)],
            [new EditorPauseTiming(2, 3)], "female_like", .8, 210);
        var groups = ((System.Collections.IEnumerable)method.Invoke(null, [cue, timing, "Xin chào đạo hữu", "female"])!).Cast<object>().ToArray();
        Equal(2, groups.Length);
        var firstType = groups[0].GetType();
        Equal(1.2d, (double)(firstType.GetProperty("Start")?.GetValue(groups[0]) ?? 0d));
        Equal(2d, (double)(firstType.GetProperty("End")?.GetValue(groups[0]) ?? 0d));
        True(!string.IsNullOrWhiteSpace(firstType.GetProperty("Text")?.GetValue(groups[0])?.ToString()), "TTS first rhythm group lost Vietnamese text");
        return Task.CompletedTask;
    }

    private static Task EditorVoiceMixContractAsync()
    {
        var method = typeof(VideoEditorService).GetMethod("BuildVoiceAudioFilter", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing unified voice audio graph");
        var track = new EditorVoiceTrack("voice.flac", 0, 60, .9);
        string Graph(EditorAudioSettings audio) => (string)method.Invoke(null, [audio, track, 1, 0d])!;
        var keep = Graph(new EditorAudioSettings("keep", 1));
        True(keep.Contains("[0:a]asetpts=PTS-STARTPTS[sourcea]", StringComparison.Ordinal), "keep+voice lost source audio");
        True(keep.Contains("[sourcea][voicea]amix=inputs=2", StringComparison.Ordinal), "keep+voice did not mix source and TTS");
        var duck = Graph(new EditorAudioSettings("duck", .35));
        True(duck.Contains("volume=0.350", StringComparison.Ordinal) && duck.Contains("amix=inputs=2", StringComparison.Ordinal), "duck+voice graph drift");
        var mute = Graph(new EditorAudioSettings("mute", 0));
        True(!mute.Contains("[0:a]", StringComparison.Ordinal), "mute+voice unexpectedly kept source audio");
        True(mute.Contains("[voicea]anull[aout]", StringComparison.Ordinal), "mute+voice did not route TTS to output");
        return Task.CompletedTask;
    }

    private static Task EditorRenderValidationContractAsync()
    {
        var method = typeof(VideoEditorService).GetMethod("ValidateRenderedProbe", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Editor final-render validation policy");
        const string withAudio = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080},{"codec_type":"audio"}],"format":{"duration":"120.040","size":"1000000"}}
            """;
        const string withoutAudio = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080}],"format":{"duration":"120.010","size":"900000"}}
            """;
        method.Invoke(null, [withAudio, 120d, true]);
        method.Invoke(null, [withoutAudio, 120d, false]);

        void Invalid(string json, double duration, bool expectAudio, string label)
        {
            try
            {
                method.Invoke(null, [json, duration, expectAudio]);
                throw new InvalidOperationException(label + " was accepted");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            {
            }
        }

        Invalid(withoutAudio, 120d, true, "missing required audio");
        Invalid(withAudio, 120d, false, "unexpected muted audio");
        Invalid(withAudio, 100d, true, "duration drift");
        Invalid("""{"streams":[{"codec_type":"audio"}],"format":{"duration":"120","size":"1000"}}""", 120d, true, "missing video");
        return Task.CompletedTask;
    }

    private static Task VietnameseTtsNormalizerContractAsync()
    {
        Equal("Hôm nay ngày hai tháng ba năm hai nghìn không trăm hai mươi sáu lúc chín giờ năm phút",
            VietnameseTtsTextNormalizer.Normalize("Hôm nay 02/03/2026 lúc 09:05"));
        Equal("ba phẩy hai phần trăm", VietnameseTtsTextNormalizer.Normalize("3,2%"));
        Equal("một trăm hai mươi ba", VietnameseTtsTextNormalizer.Normalize("123"));
        return Task.CompletedTask;
    }

    private static Task EditorDocumentContractAsync()
    {
        var document = new EditorRegionDocument();
        document.Reset([]);
        document.Add(new EditRegion(.1, .2, .3, .25, "blur", 18, true, 0, 10));
        var identity = document.Selected?.Id ?? throw new InvalidOperationException("editor region did not receive an identity");
        True(identity.Length > 0, "editor region identity is empty");
        document.ReplaceSelected(document.Selected! with { X = .2 });
        Equal(.2, document.Selected!.X);
        True(document.Undo(), "editor edit could not be undone");
        Equal(.1, document.Selected!.X);
        Equal(identity, document.Selected.Id);
        True(document.Redo(), "editor edit could not be redone");
        Equal(.2, document.Selected!.X);
        Equal(identity, document.Selected.Id);
        True(document.RemoveSelected(), "editor region could not be removed");
        Equal(0, document.Regions.Count);
        True(document.Undo(), "editor removal could not be undone");
        Equal(identity, document.Selected!.Id);
        return Task.CompletedTask;
    }

    private static Task EditorUndoContractAsync()
    {
        var document = new EditorRegionDocument();
        document.Reset([]);
        True(!document.Undo(), "empty document unexpectedly exposed Undo");

        document.Add(new EditRegion(.1, .2, .3, .25, "blur", 18, true, 0, 10, "first"));
        document.Add(new EditRegion(.5, .1, .2, .2, "mosaic", 12, false, 2, 8, "second"));
        var second = document.Selected ?? throw new InvalidOperationException("second Undo fixture region was not selected");
        True(document.ReplaceSelected(second with { X = .55, Strength = 20 }), "Undo fixture edit was rejected");

        True(document.Undo(), "latest region edit could not be undone");
        Equal(second, document.Selected);
        Equal(1, document.SelectedIndex);
        True(document.Undo(), "second region add could not be undone");
        Equal(1, document.Regions.Count);
        Equal("first", document.Selected?.Id);
        Equal(0, document.SelectedIndex);
        True(document.Undo(), "first region add could not be undone");
        Equal(0, document.Regions.Count);
        Equal(-1, document.SelectedIndex);
        True(document.Selected is null && !document.CanUndo, "Undo did not restore the exact empty document state");

        document.Reset([new EditRegion(.1, .2, .3, .25, "blur", 0, true, 0, 10, "bounded")]);
        True(!document.ReplaceSelected(document.Selected!), "no-op replacement entered Undo history");
        True(!document.CanUndo, "no-op replacement enabled Undo");
        for (var strength = 1; strength <= 55; strength++)
            True(document.ReplaceSelected(document.Selected! with { Strength = strength }), "bounded Undo fixture edit was rejected");
        var undoCount = 0;
        while (document.Undo()) undoCount++;
        Equal(50, undoCount);
        Equal(5, document.Selected?.Strength);
        Equal("bounded", document.Selected?.Id);
        return Task.CompletedTask;
    }

    private static Task EditorRedoContractAsync()
    {
        var document = new EditorRegionDocument();
        document.Reset([]);
        True(!document.Redo(), "empty document unexpectedly exposed Redo");

        var first = new EditRegion(.1, .2, .3, .25, "blur", 18, true, 0, 10, "first-redo");
        var second = new EditRegion(.5, .1, .2, .2, "mosaic", 12, false, 2, 8, "second-redo");
        var editedSecond = second with { X = .55, Strength = 20 };
        document.Add(first);
        document.Add(second);
        True(document.ReplaceSelected(editedSecond), "Redo fixture edit was rejected");

        True(document.Undo() && document.Undo() && document.Undo(), "Redo fixture could not rewind to empty state");
        Equal(0, document.Regions.Count);
        Equal(-1, document.SelectedIndex);
        True(document.CanRedo, "rewound document did not expose Redo");

        True(document.Redo(), "first region add could not be redone");
        Equal(first, document.Selected);
        Equal(0, document.SelectedIndex);
        True(document.Redo(), "second region add could not be redone");
        Equal(2, document.Regions.Count);
        Equal(second, document.Selected);
        Equal(1, document.SelectedIndex);
        True(document.Redo(), "latest region edit could not be redone");
        Equal(editedSecond, document.Selected);
        Equal("second-redo", document.Selected?.Id);
        True(!document.CanRedo, "completed Redo sequence left stale history");

        True(document.Undo(), "Redo no-op fixture could not be rewound");
        True(!document.ReplaceSelected(document.Selected!), "no-op replacement unexpectedly changed the document");
        True(document.CanRedo, "no-op replacement cleared valid Redo history");
        True(document.Redo(), "Redo failed after a no-op replacement");
        Equal(editedSecond, document.Selected);

        True(document.Undo(), "divergent Redo fixture could not be rewound");
        True(document.ReplaceSelected(document.Selected! with { Strength = 30 }), "divergent Redo edit was rejected");
        True(!document.CanRedo && !document.Redo(), "divergent edit did not invalidate stale Redo history");
        Equal(30, document.Selected?.Strength);

        document.Reset([first]);
        True(!document.CanUndo && !document.CanRedo, "document reset retained Undo or Redo history");
        return Task.CompletedTask;
    }

    private static Task EditorDeleteContractAsync()
    {
        var document = new EditorRegionDocument();
        document.Reset([]);
        True(!document.RemoveSelected(), "empty document unexpectedly allowed Delete");
        True(!document.CanUndo && !document.CanRedo, "empty Delete changed history");

        var first = new EditRegion(.1, .1, .2, .2, "blur", 18, true, 0, 10, "delete-first");
        var middle = new EditRegion(.4, .1, .2, .2, "mosaic", 12, true, 0, 10, "delete-middle");
        var last = new EditRegion(.7, .1, .2, .2, "cover", 0, true, 0, 10, "delete-last");
        document.Reset([first, middle, last]);
        document.Select(1);
        True(document.RemoveSelected(), "selected middle region could not be deleted");
        Equal(2, document.Regions.Count);
        Equal(last, document.Selected);
        Equal(1, document.SelectedIndex);
        True(document.CanUndo && !document.CanRedo, "Delete did not create exactly one Undo branch");

        True(document.Undo(), "middle Delete could not be undone");
        Equal(3, document.Regions.Count);
        Equal(middle, document.Selected);
        Equal(1, document.SelectedIndex);
        True(document.Redo(), "middle Delete could not be redone");
        Equal(last, document.Selected);
        Equal(1, document.SelectedIndex);

        True(document.Undo(), "last-selection fixture could not restore the middle region");
        document.Select(2);
        True(document.RemoveSelected(), "selected last region could not be deleted");
        Equal(2, document.Regions.Count);
        Equal(middle, document.Selected);
        Equal(1, document.SelectedIndex);
        True(!document.CanRedo, "new Delete did not invalidate the previous Redo branch");
        True(document.Undo(), "last Delete could not be undone");
        Equal(last, document.Selected);
        Equal(2, document.SelectedIndex);

        document.Reset([first]);
        True(document.RemoveSelected(), "only region could not be deleted");
        Equal(0, document.Regions.Count);
        Equal(-1, document.SelectedIndex);
        True(document.Selected is null, "Delete left a stale selection in an empty document");
        True(document.Undo(), "empty-state Delete could not be undone");
        Equal(first, document.Selected);
        Equal(0, document.SelectedIndex);
        True(document.Redo(), "empty-state Delete could not be redone");
        True(document.Selected is null && document.Regions.Count == 0, "Redo did not restore the exact empty Delete state");
        return Task.CompletedTask;
    }

    private static Task EditorRegionPresetContractAsync()
    {
        const double duration = 123.45;
        var subtitle = EditorRegionGeometry.CreatePreset(
            EditorRegionPresetKind.SubtitleBottom, 1920, 1080, duration)
            ?? throw new InvalidOperationException("bottom-subtitle preset was rejected");
        Equal(.08, subtitle.X);
        Equal(.72, subtitle.Y);
        Equal(.84, subtitle.Width);
        Equal(.18, subtitle.Height);
        Equal("blur", subtitle.Effect);
        Equal(EditorBlurStrength.Default, subtitle.Strength);
        True(subtitle.WholeVideo && subtitle.Start == 0 && subtitle.End == duration,
            "bottom-subtitle preset did not own the exact whole-video range");

        var watermark = EditorRegionGeometry.CreatePreset(
            EditorRegionPresetKind.WatermarkTopRight, 1920, 1080, duration)
            ?? throw new InvalidOperationException("top-right watermark preset was rejected");
        Equal(.78, watermark.X);
        Equal(.04, watermark.Y);
        Equal(.18, watermark.Width);
        Equal(.10, watermark.Height);
        Equal("mosaic", watermark.Effect);
        Equal(EditorMosaicStrength.Default, watermark.Strength);
        True(watermark.WholeVideo && watermark.Start == 0 && watermark.End == duration,
            "top-right watermark preset did not own the exact whole-video range");

        _ = VideoEditorService.BuildFilter(
            new VideoEditRequest("input.mp4", ".", "output.mp4", 1920, 1080, duration, [subtitle, watermark]));
        True(EditorRegionGeometry.CreatePreset(EditorRegionPresetKind.SubtitleBottom, 1, 1, duration) is null,
            "source-pixel-invalid preset was accepted");
        True(EditorRegionGeometry.CreatePreset(EditorRegionPresetKind.WatermarkTopRight, 1920, 1080, double.NaN) is null,
            "non-finite preset duration was accepted");
        True(EditorRegionGeometry.CreatePreset((EditorRegionPresetKind)999, 1920, 1080, duration) is null,
            "unknown region preset was accepted");

        var document = new EditorRegionDocument();
        document.Reset([]);
        document.Add(subtitle);
        var subtitleId = document.Selected?.Id ?? throw new InvalidOperationException("subtitle preset received no identity");
        document.Add(watermark);
        var watermarkId = document.Selected?.Id ?? throw new InvalidOperationException("watermark preset received no identity");
        True(subtitleId != watermarkId, "preset additions shared one region identity");
        True(document.Undo(), "watermark preset addition could not be undone");
        Equal(subtitleId, document.Selected?.Id);
        True(document.Redo(), "watermark preset addition could not be redone");
        Equal(watermarkId, document.Selected?.Id);
        return Task.CompletedTask;
    }

    private static Task EditorMouseRegionGeometryContractAsync()
    {
        var settings = new EditRegion(0, 0, 0, 0, "mosaic", 12, false, 2, 8);
        var forward = EditorRegionGeometry.FromNormalizedDrag(settings, .2, .1, .8, .7, 1000, 500)
            ?? throw new InvalidOperationException("forward mouse drag did not create a region");
        var reverse = EditorRegionGeometry.FromNormalizedDrag(settings, .8, .7, .2, .1, 1000, 500)
            ?? throw new InvalidOperationException("reverse mouse drag did not create a region");
        True(Math.Abs(forward.X - .2) < .000_001 && Math.Abs(forward.Y - .1) < .000_001,
            "mouse drag origin was not normalized");
        True(Math.Abs(forward.Width - .6) < .000_001 && Math.Abs(forward.Height - .6) < .000_001,
            "mouse drag size was not normalized");
        Equal(forward, reverse);
        Equal("mosaic", forward.Effect);
        Equal(12, forward.Strength);
        Equal(2d, forward.Start);
        Equal(8d, forward.End);

        var clamped = EditorRegionGeometry.FromNormalizedDrag(settings, -.2, 1.2, .5, .5, 100, 100)
            ?? throw new InvalidOperationException("bounded mouse drag was rejected");
        True(clamped.X == 0 && clamped.Y == .5 && clamped.Width == .5 && clamped.Height == .5,
            "mouse drag did not clamp to source bounds");
        True(EditorRegionGeometry.FromNormalizedDrag(settings, .1, .1, .109, .109, 100, 100) is null,
            "sub-two-pixel mouse drag entered the document");
        True(EditorRegionGeometry.FromNormalizedDrag(settings, double.NaN, .1, .5, .5, 100, 100) is null,
            "non-finite mouse drag entered the document");
        return Task.CompletedTask;
    }

    private static Task EditorRegionSelectionContractAsync()
    {
        EditRegion[] regions =
        [
            new(.1, .1, .5, .5, "blur", 18, true, 0, 10, "lower"),
            new(.2, .2, .2, .2, "mosaic", 12, true, 0, 10, "upper"),
        ];
        Equal(1, EditorRegionGeometry.FindTopmostContaining(regions, .25, .25));
        Equal(0, EditorRegionGeometry.FindTopmostContaining(regions, .15, .15));
        Equal(1, EditorRegionGeometry.FindTopmostContaining(regions, .2, .2));
        Equal(-1, EditorRegionGeometry.FindTopmostContaining(regions, .9, .9));
        Equal(-1, EditorRegionGeometry.FindTopmostContaining(regions, double.NaN, .25));
        Equal(-1, EditorRegionGeometry.FindTopmostContaining(regions, 1.1, .25));

        var document = new EditorRegionDocument();
        document.Reset(regions);
        document.Select(EditorRegionGeometry.FindTopmostContaining(document.Regions, .25, .25));
        Equal("upper", document.Selected?.Id);
        Equal(2, document.Regions.Count);
        document.Select(-1);
        Equal(-1, document.SelectedIndex);
        True(document.Selected is null, "clearing region selection left stale document state");
        return Task.CompletedTask;
    }

    private static Task EditorRegionMoveContractAsync()
    {
        var original = new EditRegion(.1, .2, .3, .25, "mosaic", 12, false, 2, 8, "moving");
        var moved = EditorRegionGeometry.MoveBy(original, .25, -.1);
        Equal(.35, moved.X);
        Equal(.1, moved.Y);
        Equal(original.Width, moved.Width);
        Equal(original.Height, moved.Height);
        Equal(original.Effect, moved.Effect);
        Equal(original.Strength, moved.Strength);
        Equal(original.WholeVideo, moved.WholeVideo);
        Equal(original.Start, moved.Start);
        Equal(original.End, moved.End);
        Equal(original.Id, moved.Id);

        var clamped = EditorRegionGeometry.MoveBy(original, 2, -2);
        Equal(.7, clamped.X);
        Equal(0d, clamped.Y);
        var oppositeEdges = EditorRegionGeometry.MoveBy(original, -2, 2);
        Equal(0d, oppositeEdges.X);
        Equal(.75, oppositeEdges.Y);
        Equal(original, EditorRegionGeometry.MoveBy(original, double.NaN, 0));

        var document = new EditorRegionDocument();
        document.Reset([original]);
        document.BeginChange();
        True(document.ReplaceSelected(moved, capture: false), "live move did not update the selected region");
        True(document.CancelChange(), "canceled move did not restore its transaction");
        Equal(original, document.Selected);
        True(!document.CanUndo && !document.CanRedo, "canceled move leaked into undo or redo history");

        document.BeginChange();
        True(document.ReplaceSelected(moved, capture: false), "committed move did not update the selected region");
        True(document.Undo(), "committed move could not be undone");
        Equal(original, document.Selected);
        True(document.Redo(), "committed move could not be redone");
        Equal(moved, document.Selected);
        return Task.CompletedTask;
    }

    private static Task EditorRegionResizeContractAsync()
    {
        var original = new EditRegion(.2, .2, .4, .4, "blur", 18, false, 2, 8, "resizing");
        var cases = new[]
        {
            (EditorRegionResizeHandle.North, 0d, -.1, .2, .1, .4, .5),
            (EditorRegionResizeHandle.South, 0d, .1, .2, .2, .4, .5),
            (EditorRegionResizeHandle.East, .1, 0d, .2, .2, .5, .4),
            (EditorRegionResizeHandle.West, -.1, 0d, .1, .2, .5, .4),
            (EditorRegionResizeHandle.NorthEast, .1, -.1, .2, .1, .5, .5),
            (EditorRegionResizeHandle.NorthWest, -.1, -.1, .1, .1, .5, .5),
            (EditorRegionResizeHandle.SouthEast, .1, .1, .2, .2, .5, .5),
            (EditorRegionResizeHandle.SouthWest, -.1, .1, .1, .2, .5, .5),
        };
        Equal(8, Enum.GetValues<EditorRegionResizeHandle>().Length);
        foreach (var (handle, deltaX, deltaY, x, y, width, height) in cases)
        {
            var resized = EditorRegionGeometry.ResizeBy(original, deltaX, deltaY, handle, 100, 100);
            True(Math.Abs(resized.X - x) < .000_001 && Math.Abs(resized.Y - y) < .000_001
                && Math.Abs(resized.Width - width) < .000_001 && Math.Abs(resized.Height - height) < .000_001,
                $"{handle} produced wrong geometry");
            Equal(original.Effect, resized.Effect);
            Equal(original.Strength, resized.Strength);
            Equal(original.WholeVideo, resized.WholeVideo);
            Equal(original.Start, resized.Start);
            Equal(original.End, resized.End);
            Equal(original.Id, resized.Id);
            _ = VideoEditorService.BuildFilter(new VideoEditRequest("input.mp4", ".", "output.mp4", 100, 100, 10, [resized]));
        }

        var minimumCases = new[]
        {
            (EditorRegionResizeHandle.North, 0d, 2d),
            (EditorRegionResizeHandle.South, 0d, -2d),
            (EditorRegionResizeHandle.East, -2d, 0d),
            (EditorRegionResizeHandle.West, 2d, 0d),
            (EditorRegionResizeHandle.NorthEast, -2d, 2d),
            (EditorRegionResizeHandle.NorthWest, 2d, 2d),
            (EditorRegionResizeHandle.SouthEast, -2d, -2d),
            (EditorRegionResizeHandle.SouthWest, 2d, -2d),
        };
        foreach (var (handle, deltaX, deltaY) in minimumCases)
        {
            var minimum = EditorRegionGeometry.ResizeBy(original, deltaX, deltaY, handle, 640, 360);
            True(minimum != original, $"{handle} minimum resize was ignored");
            var pixelWidth = (int)((minimum.X + minimum.Width) * 640) - (int)(minimum.X * 640);
            var pixelHeight = (int)((minimum.Y + minimum.Height) * 360) - (int)(minimum.Y * 360);
            if (handle is EditorRegionResizeHandle.East or EditorRegionResizeHandle.West
                or EditorRegionResizeHandle.NorthEast or EditorRegionResizeHandle.NorthWest
                or EditorRegionResizeHandle.SouthEast or EditorRegionResizeHandle.SouthWest)
                True(pixelWidth is >= 2 and <= 3, $"{handle} did not stop at the source-pixel width minimum");
            if (handle is EditorRegionResizeHandle.North or EditorRegionResizeHandle.South
                or EditorRegionResizeHandle.NorthEast or EditorRegionResizeHandle.NorthWest
                or EditorRegionResizeHandle.SouthEast or EditorRegionResizeHandle.SouthWest)
                True(pixelHeight is >= 2 and <= 3, $"{handle} did not stop at the source-pixel height minimum");
            _ = VideoEditorService.BuildFilter(new VideoEditRequest("input.mp4", ".", "output.mp4", 640, 360, 10, [minimum]));
        }
        var fullBounds = EditorRegionGeometry.ResizeBy(original, 2, 2, EditorRegionResizeHandle.SouthEast, 640, 360);
        True(fullBounds.X + fullBounds.Width <= 1 && fullBounds.Y + fullBounds.Height <= 1,
            "resize escaped source bounds");
        var oppositeBounds = EditorRegionGeometry.ResizeBy(original, -2, -2, EditorRegionResizeHandle.NorthWest, 640, 360);
        True(oppositeBounds.X >= 0 && oppositeBounds.Y >= 0, "resize escaped the top-left source bounds");
        Equal(original, EditorRegionGeometry.ResizeBy(original, double.NaN, 0, EditorRegionResizeHandle.East, 640, 360));
        return Task.CompletedTask;
    }

    private static Task EditorRegionNumericInputsContractAsync()
    {
        var settings = new EditRegion(0, 0, 0, 0, "mosaic", 12, false, 2, 8, "numeric");
        var region = EditorRegionGeometry.FromPercentInputs(settings, 20, 10, 60, 70, 1000, 500)
            ?? throw new InvalidOperationException("valid numeric geometry was rejected");
        True(Math.Abs(region.X - .2) < .000_001 && Math.Abs(region.Y - .1) < .000_001
            && Math.Abs(region.Width - .6) < .000_001 && Math.Abs(region.Height - .7) < .000_001,
            "numeric percentages were not normalized");
        Equal(settings.Effect, region.Effect);
        Equal(settings.Strength, region.Strength);
        Equal(settings.WholeVideo, region.WholeVideo);
        Equal(settings.Start, region.Start);
        Equal(settings.End, region.End);
        Equal(settings.Id, region.Id);
        _ = VideoEditorService.BuildFilter(new VideoEditRequest("input.mp4", ".", "output.mp4", 1000, 500, 10, [region]));

        var edge = EditorRegionGeometry.FromPercentInputs(settings, 99, 98, 1, 2, 1000, 500);
        True(edge is not null, "numeric geometry ending at the source edge was rejected");
        True(EditorRegionGeometry.FromPercentInputs(settings, double.NaN, 0, 10, 10, 640, 360) is null,
            "non-finite numeric geometry was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, -1, 0, 10, 10, 640, 360) is null,
            "negative numeric geometry was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 0, 0, 0, 10, 640, 360) is null,
            "zero-width numeric geometry was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 95, 0, 6, 10, 640, 360) is null,
            "numeric geometry outside the right source bound was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 0, 95, 10, 6, 640, 360) is null,
            "numeric geometry outside the bottom source bound was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 0, 0, .3, 10, 640, 360) is null,
            "sub-two-pixel numeric width was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 0, 0, 10, .5, 640, 360) is null,
            "sub-two-pixel numeric height was accepted");
        True(EditorRegionGeometry.FromPercentInputs(settings, 0, 0, .32, .56, 640, 360) is not null,
            "source-pixel-valid numeric minimum was rejected");
        return Task.CompletedTask;
    }

    private static Task EditorBlurStrengthContractAsync()
    {
        True(EditorBlurStrength.TryFromInput(2, out var minimum) && minimum == 2,
            "minimum blur strength was rejected");
        True(EditorBlurStrength.TryFromInput(40, out var maximum) && maximum == 40,
            "maximum blur strength was rejected");
        True(EditorBlurStrength.TryFromInput(18.4, out var roundedDown) && roundedDown == 18,
            "fractional blur strength did not normalize down");
        True(EditorBlurStrength.TryFromInput(18.5, out var roundedUp) && roundedUp == 19,
            "fractional blur strength did not normalize up");
        True(!EditorBlurStrength.TryFromInput(double.NaN, out _), "NaN blur strength entered editor state");
        True(!EditorBlurStrength.TryFromInput(double.PositiveInfinity, out _), "infinite blur strength entered editor state");
        True(!EditorBlurStrength.TryFromInput(1, out _), "below-minimum blur strength entered editor state");
        True(!EditorBlurStrength.TryFromInput(41, out _), "above-maximum blur strength entered editor state");
        Equal(2, EditorBlurStrength.NormalizeStored(-10));
        Equal(40, EditorBlurStrength.NormalizeStored(99));
        Equal(27, EditorBlurStrength.EffectiveRadius(27, 400, 300));
        Equal(9, EditorBlurStrength.EffectiveRadius(40, 30, 20));
        Equal(0, EditorBlurStrength.EffectiveRadius(40, 2, 2));

        var region = new EditRegion(.1, .2, .3, .25, "blur", 27, true, 0, 10, "blur-strength");
        var graph = VideoEditorService.BuildFilter(
            new VideoEditRequest("input.mp4", ".", "output.mp4", 1000, 500, 10, [region]));
        True(graph.Contains("boxblur=luma_radius=27:luma_power=1:chroma_radius='min(27,floor((min(cw,ch)-1)/2))':chroma_power=1", StringComparison.Ordinal),
            "Preview/Export filter lost the selected blur strength");

        var tiny = region with { X = 0, Y = 0, Width = .02, Height = .02, Strength = 40 };
        graph = VideoEditorService.BuildFilter(
            new VideoEditRequest("input.mp4", ".", "output.mp4", 100, 100, 10, [tiny]));
        True(graph.Contains("boxblur=luma_radius=0:luma_power=1:chroma_radius='min(0,floor((min(cw,ch)-1)/2))':chroma_power=1", StringComparison.Ordinal),
            "small region blur radius exceeded FFmpeg's pixel bound");
        return Task.CompletedTask;
    }

    private static Task EditorMosaicStrengthContractAsync()
    {
        True(EditorMosaicStrength.TryFromInput(4, out var minimum) && minimum == 4,
            "minimum Mosaic strength was rejected");
        True(EditorMosaicStrength.TryFromInput(64, out var maximum) && maximum == 64,
            "maximum Mosaic strength was rejected");
        True(EditorMosaicStrength.TryFromInput(12.4, out var roundedDown) && roundedDown == 12,
            "fractional Mosaic strength did not normalize down");
        True(EditorMosaicStrength.TryFromInput(12.5, out var roundedUp) && roundedUp == 13,
            "fractional Mosaic strength did not normalize up");
        True(!EditorMosaicStrength.TryFromInput(double.NaN, out _), "NaN Mosaic strength entered editor state");
        True(!EditorMosaicStrength.TryFromInput(3, out _), "below-minimum Mosaic strength entered editor state");
        True(!EditorMosaicStrength.TryFromInput(65, out _), "above-maximum Mosaic strength entered editor state");
        Equal(4, EditorMosaicStrength.NormalizeInput(2));
        Equal(64, EditorMosaicStrength.NormalizeInput(100));
        Equal(50, EditorMosaicStrength.NormalizeInput(double.NaN, 50));
        Equal(new EditorMosaicDimensions(48, 22), EditorMosaicStrength.DownsampleDimensions(12, 576, 270));
        Equal(new EditorMosaicDimensions(1, 1), EditorMosaicStrength.DownsampleDimensions(64, 30, 20));
        Equal(new EditorMosaicDimensions(1, 1), EditorMosaicStrength.DownsampleDimensions(4, 2, 2));
        Equal(new EditorMosaicDimensions(96, 45), EditorMosaicStrength.DownsampleDimensions(12, 384, 180, 1d / 3, 1d / 3));

        var region = new EditRegion(.1, .2, .3, .25, "mosaic", 12, true, 0, 10, "mosaic-strength");
        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 3840, 2160, 10, [region]);
        var graph = VideoEditorService.BuildFilter(request);
        True(graph.Contains("crop=1152:540:384:432,scale=96:45:flags=neighbor,scale=1152:540:flags=neighbor", StringComparison.Ordinal),
            "Preview/Export Mosaic filter lost the selected pixelation strength");

        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Mosaic processed-preview slice policy");
        var preview = (VideoEditRequest)sliceMethod.Invoke(null, [request, 0d, 10d, 1280, 720])!;
        Equal(1d / 3, preview.MosaicScaleX);
        Equal(1d / 3, preview.MosaicScaleY);
        graph = VideoEditorService.BuildFilter(preview);
        True(graph.Contains("crop=384:180:128:144,scale=96:45:flags=neighbor,scale=384:180:flags=neighbor", StringComparison.Ordinal),
            "processed Preview changed Mosaic block density from Export");

        var tiny = region with { X = 0, Y = 0, Width = .02, Height = .02, Strength = 64 };
        graph = VideoEditorService.BuildFilter(
            new VideoEditRequest("input.mp4", ".", "output.mp4", 100, 100, 10, [tiny]));
        True(graph.Contains("crop=2:2:0:0,scale=1:1:flags=neighbor,scale=2:2:flags=neighbor", StringComparison.Ordinal),
            "tiny Mosaic region did not retain a valid pixelation chain");
        return Task.CompletedTask;
    }

    private static Task EditorCoverEffectContractAsync()
    {
        Equal(EditorCoverEffect.StoredStrength, EditorCoverEffect.NormalizeStored(-100));
        Equal(EditorCoverEffect.StoredStrength, EditorCoverEffect.NormalizeStored(100));

        var normalizeMethod = typeof(EditorProjectStore).GetMethod(
            "NormalizeRegions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Cover persistence normalization policy");
        var normalized = (IReadOnlyList<EditRegion>)normalizeMethod.Invoke(null,
            [new[] { new EditRegion(.1, .2, .3, .25, "cover", 37, true, 0, 10, "cover-persisted") }, 10d, false])!;
        Equal(EditorCoverEffect.StoredStrength, normalized[0].Strength);

        var region = new EditRegion(.1, .2, .3, .25, "cover", EditorCoverEffect.StoredStrength, true, 0, 10, "cover");
        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 3840, 2160, 10, [region]);
        var exportGraph = VideoEditorService.BuildFilter(request);
        True(exportGraph.Contains("drawbox=x=384:y=432:w=1152:h=540:color=black@1:t=fill", StringComparison.Ordinal),
            "Export Cover lost opaque black fill or normalized source geometry");
        Equal(exportGraph, VideoEditorService.BuildFilter(request with { Regions = [region with { Strength = 63 }] }));

        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing Cover processed-preview slice policy");
        var preview = (VideoEditRequest)sliceMethod.Invoke(null, [request, 0d, 10d, 1280, 720])!;
        var previewGraph = VideoEditorService.BuildFilter(preview);
        True(previewGraph.Contains("drawbox=x=128:y=144:w=384:h=180:color=black@1:t=fill", StringComparison.Ordinal),
            "processed Preview changed Cover opacity or normalized geometry from Export");
        return Task.CompletedTask;
    }

    private static Task EditorWholeVideoScopeContractAsync()
    {
        var stale = new EditRegion(.1, .2, .3, .25, "cover", EditorCoverEffect.StoredStrength, true, 37, 42, "whole");
        var whole = EditorRegionTimeScope.NormalizeWholeVideo(stale, 120);
        Equal(0d, whole.Start);
        Equal(120d, whole.End);
        True(VideoEditorService.IsActiveAt(whole, 0) && VideoEditorService.IsActiveAt(whole, 120),
            "whole-video region was not active across the source duration");

        var exportRequest = new VideoEditRequest("input.mp4", ".", "output.mp4", 1920, 1080, 120, [whole]);
        var exportGraph = VideoEditorService.BuildFilter(exportRequest);
        True(!exportGraph.Contains("enable='between", StringComparison.Ordinal),
            "whole-video Export unexpectedly retained a timed enable guard");

        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing whole-video processed-preview slice policy");
        var preview = (VideoEditRequest)sliceMethod.Invoke(null, [exportRequest, 80d, 9.5d, 1280, 720])!;
        Equal(1, preview.Regions.Count);
        Equal(0d, preview.Regions[0].Start);
        Equal(9.5d, preview.Regions[0].End);
        True(!VideoEditorService.BuildFilter(preview).Contains("enable='between", StringComparison.Ordinal),
            "whole-video processed Preview unexpectedly retained a timed enable guard");

        var timed = stale with { WholeVideo = false, Start = 37, End = 42 };
        Equal(timed, EditorRegionTimeScope.NormalizeWholeVideo(timed, 120));
        return Task.CompletedTask;
    }

    private static Task EditorTimedRangeContractAsync()
    {
        var timed = new EditRegion(.1, .2, .3, .25, "blur", 18, false, 37, 42, "timed");
        Equal(timed, EditorRegionTimeScope.Normalize(timed, 120));
        Throws<ArgumentException>(() => EditorRegionTimeScope.Normalize(timed with { Start = double.NaN }, 120));
        Throws<ArgumentException>(() => EditorRegionTimeScope.Normalize(timed with { Start = -1 }, 120));
        Throws<ArgumentException>(() => EditorRegionTimeScope.Normalize(timed with { End = 121 }, 120));
        Throws<ArgumentException>(() => EditorRegionTimeScope.Normalize(timed with { Start = 42 }, 120));

        var migrated = EditorRegionTimeScope.NormalizeStored(timed with { Start = -2, End = 125 }, 120);
        Equal(0d, migrated.Start);
        Equal(120d, migrated.End);
        Throws<InvalidDataException>(() => EditorRegionTimeScope.NormalizeStored(timed with { Start = 125, End = 130 }, 120));

        Equal(new EditorRegionTimeRange(37, 42), EditorRegionTimeScope.CreateDefaultTimedRange(37, 120));
        Equal(new EditorRegionTimeRange(115, 120), EditorRegionTimeScope.CreateDefaultTimedRange(120, 120));

        var request = new VideoEditRequest("input.mp4", ".", "output.mp4", 1920, 1080, 120, [timed]);
        var exportGraph = VideoEditorService.BuildFilter(request);
        True(exportGraph.Contains("enable='between(t,37.000,42.000)'", StringComparison.Ordinal),
            "Export changed the validated numeric time range");

        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing timed processed-preview slice policy");
        var preview = (VideoEditRequest)sliceMethod.Invoke(null, [request, 40d, 5d, 1280, 720])!;
        Equal(1, preview.Regions.Count);
        Equal(0d, preview.Regions[0].Start);
        Equal(2d, preview.Regions[0].End);
        True(VideoEditorService.BuildFilter(preview).Contains("enable='between(t,0.000,2.000)'", StringComparison.Ordinal),
            "processed Preview did not shift and clip the validated source time range");
        var outside = (VideoEditRequest)sliceMethod.Invoke(null, [request, 50d, 5d, 1280, 720])!;
        Equal(0, outside.Regions.Count);
        return Task.CompletedTask;
    }

    private static async Task EditorProjectContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var video = Path.Combine(root, "source.mp4");
            await File.WriteAllBytesAsync(video, [1, 2, 3]);
            var store = new EditorProjectStore(paths);
            var created = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(0, created.Regions.Count);
            var region = new EditRegion(.1, .2, .3, .25, "blur", 99, false, 1, 5, "stable-region");
            var srt = Path.Combine(root, "source.zh.srt");
            await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,000\n你好\n");
            var subtitle = await EditorSubtitleDocument.LoadAsync(srt, CancellationToken.None);
            var subtitleProject = new EditorSubtitleProject(
                subtitle.Path,
                subtitle.Size,
                subtitle.LastWriteUtcTicks,
                subtitle.Sha256,
                [subtitle.Cues[0] with { VietnameseText = "Xin chào" }],
                new EditorSubtitlePlacement(.1, .72, .8, .18),
                "Dịch Trung Tu Tiên",
                TranslationSkillBundle.BuiltInSha256,
                Path.Combine(root, "source.vi.srt"),
                TranslationPolicyKey: LocalSubtitleTranslationService.TranslationPolicyKey);
            var speechPath = Path.Combine(root, "speech.json");
            var speechAnalysis = new EditorSpeechAnalysis(
                EditorSpeechAnalysisDocument.CurrentSchema, new string('b', 64), "fixture Whisper",
                "536b0662742c02347bc0e980a01041f333bce120", "cpu", "int8", .75, []);
            var speechSha = await EditorSpeechAnalysisDocument.SaveAsync(speechPath, speechAnalysis, CancellationToken.None);
            var voicePath = Path.Combine(root, "voice.flac");
            await File.WriteAllBytesAsync(voicePath, Enumerable.Repeat((byte)1, 128).ToArray());
            var ttsManifest = Path.Combine(root, "tts-result.json");
            await File.WriteAllTextAsync(ttsManifest, "{\"schema\":1}");
            var ttsManifestSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(ttsManifest)));
            var mosaicRegion = new EditRegion(.55, .15, .2, .2, "mosaic", 99, true, 55, 56, "stable-mosaic");
            await store.SaveAsync(created with
            {
                FileName = "episode-edited.mp4",
                Regions = [region, mosaicRegion],
                Subtitle = subtitleProject,
                Audio = new EditorAudioSettings("duck", .35),
                Asr = new EditorAsrProject("complete", "fixture ASR", "536b0662742c02347bc0e980a01041f333bce120",
                    "cpu", "int8", srt, 1, .75),
                Speech = new EditorSpeechProject("complete", "fixture Whisper", "536b0662742c02347bc0e980a01041f333bce120",
                    "cpu", "int8", speechPath, speechSha, 1, 2, .75),
                Tts = new EditorTtsProject("complete", "piper-nghitts", "1.4.2", "deepman3909", "calmwoman3688",
                    ttsManifest, ttsManifestSha, new EditorVoiceTrack(voicePath, 0, 120), 1, 0),
                VoiceOverrides = new Dictionary<string, string> { [subtitle.Cues[0].Id] = "female" },
            }, CancellationToken.None);

            var reopened = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("episode-edited.mp4", reopened.FileName);
            Equal(2, reopened.Regions.Count);
            Equal("stable-region", reopened.Regions[0].Id);
            Equal(.1, reopened.Regions[0].X);
            Equal(LocalSubtitleTranslationService.TranslationPolicyKey, reopened.Subtitle?.TranslationPolicyKey);
            Equal(EditorBlurStrength.Maximum, reopened.Regions[0].Strength);
            Equal("stable-mosaic", reopened.Regions[1].Id);
            Equal(EditorMosaicStrength.Maximum, reopened.Regions[1].Strength);
            Equal(0d, reopened.Regions[1].Start);
            Equal(120d, reopened.Regions[1].End);
            Equal("Xin chào", reopened.Subtitle!.Cues[0].VietnameseText);
            Equal(.72, reopened.Subtitle.Placement.Y);
            Equal("duck", reopened.Audio!.SourceMode);
            Equal(.35, reopened.Audio.SourceGain);
            Equal("complete", reopened.Asr!.Status);
            Equal("cpu", reopened.Asr.Device);
            Equal(.75, reopened.Asr.ProbeRealtimeFactor);
            Equal("complete", reopened.Speech!.Status);
            Equal(speechSha, reopened.Speech.AnalysisSha256);
            Equal("complete", reopened.Tts!.Status);
            Equal("female", reopened.VoiceOverrides![subtitle.Cues[0].Id]);

            File.Delete(voicePath);
            var selectivelyRecovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(selectivelyRecovered.Tts is null, "missing TTS cache should invalidate only TTS state");
            Equal("complete", selectivelyRecovered.Speech!.Status);
            Equal("episode-edited.mp4", selectivelyRecovered.FileName);

            await File.WriteAllBytesAsync(voicePath, Enumerable.Repeat((byte)1, 128).ToArray());
            await store.SaveAsync(reopened, CancellationToken.None);
            var projectPath = store.GetProjectPath(video);
            await File.AppendAllTextAsync(video, "changed-source");
            var sourceChanged = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("episode-edited.mp4", sourceChanged.FileName);
            Equal(0, sourceChanged.Regions.Count);
            True(sourceChanged.Subtitle is null, "changed source reused old subtitle state");
            True(sourceChanged.Asr is null, "changed source reused old ASR state");
            True(sourceChanged.Speech is null, "changed source reused old Whisper timing");
            True(sourceChanged.Tts is null, "changed source reused old TTS state");
            Equal(0, sourceChanged.VoiceOverrides?.Count ?? 0);
            Equal("keep", sourceChanged.Audio!.SourceMode);
            True(Directory.GetFiles(Path.GetDirectoryName(projectPath)!, Path.GetFileName(projectPath) + ".source-changed-*").Length == 1,
                "source-changed Editor project was not archived");

            await File.WriteAllTextAsync(projectPath, "{broken-json");
            var recovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(0, recovered.Regions.Count);
            True(Directory.GetFiles(Path.GetDirectoryName(projectPath)!, Path.GetFileName(projectPath) + ".corrupt-*").Length == 1,
                "corrupt editor project was not quarantined");
        });
    }

    private static async Task EditorRegionProjectReopenContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var video = Path.Combine(root, "region-reopen-source.mp4");
            var sourceBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(video, sourceBytes);
            const int width = 1920;
            const int height = 1080;
            const double duration = 120;

            var firstStore = new EditorProjectStore(paths);
            var created = await firstStore.LoadOrCreateAsync(video, width, height, duration, CancellationToken.None);
            var editedDocument = new EditorRegionDocument();
            editedDocument.Reset([]);
            EditRegion[] expected =
            [
                new(.101, .202, .303, .204, "blur", 18, false, 1.25, 5.75, "reopen-blur"),
                new(.55, .15, .2, .2, "mosaic", 12, true, 0, duration, "reopen-mosaic"),
                new(.02, .03, .1, .08, "cover", 0, false, 10, 20, "reopen-cover"),
            ];
            foreach (var region in expected) editedDocument.Add(region);
            await firstStore.SaveAsync(created with { Regions = editedDocument.Regions.ToArray() }, CancellationToken.None);
            var sourceAfterFirstSave = await File.ReadAllBytesAsync(video);
            True(sourceBytes.SequenceEqual(sourceAfterFirstSave),
                "saving region project overwrote source media bytes");

            var reopened = await new EditorProjectStore(paths)
                .LoadOrCreateAsync(video, width, height, duration, CancellationToken.None);
            Equal(expected.Length, reopened.Regions.Count);
            for (var index = 0; index < expected.Length; index++)
                Equal(expected[index], reopened.Regions[index]);

            var reopenedDocument = new EditorRegionDocument();
            reopenedDocument.Reset(reopened.Regions);
            Equal(0, reopenedDocument.SelectedIndex);
            Equal(expected[0], reopenedDocument.Selected);
            True(!reopenedDocument.CanUndo && !reopenedDocument.CanRedo,
                "project reopen leaked prior-session region history");

            var secondStore = new EditorProjectStore(paths);
            await secondStore.SaveAsync(reopened with { Regions = reopenedDocument.Regions.ToArray() }, CancellationToken.None);
            var reopenedAgain = await new EditorProjectStore(paths)
                .LoadOrCreateAsync(video, width, height, duration, CancellationToken.None);
            Equal(expected.Length, reopenedAgain.Regions.Count);
            for (var index = 0; index < expected.Length; index++)
                Equal(expected[index], reopenedAgain.Regions[index]);
            var sourceAfterSecondSave = await File.ReadAllBytesAsync(video);
            True(sourceBytes.SequenceEqual(sourceAfterSecondSave),
                "repeated region project reopen/save changed source media bytes");
        });
    }

    private static Task EditorRegionPreviewExportGeometryContractAsync()
    {
        var sliceMethod = typeof(VideoEditorService).GetMethod(
            "BuildPreviewSlice", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing processed-preview slice geometry owner");
        var pixelsMethod = typeof(VideoEditorService).GetMethod(
            "RegionPixels", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing shared Preview Export pixel geometry owner");
        EditRegion[] regions =
        [
            new(.1234, .2345, .4567, .3456, "blur", 18, true, 0, 60, "geometry-blur"),
            new(.3333, .1111, .4444, .5555, "mosaic", 12, true, 0, 60, "geometry-mosaic"),
            new(.9375, .85, .0625, .15, "cover", EditorCoverEffect.StoredStrength, true, 0, 60, "geometry-cover"),
        ];
        var dimensions = new[]
        {
            (SourceWidth: 3840, SourceHeight: 2160, PreviewWidth: 1280, PreviewHeight: 720),
            (SourceWidth: 4096, SourceHeight: 2160, PreviewWidth: 1280, PreviewHeight: 676),
        };

        foreach (var size in dimensions)
        {
            var exportRequest = new VideoEditRequest(
                "input.mp4", ".", "output.mp4", size.SourceWidth, size.SourceHeight, 60, regions);
            var previewRequest = (VideoEditRequest)sliceMethod.Invoke(null,
                [exportRequest, 24d, 12d, size.PreviewWidth, size.PreviewHeight])!;
            Equal(regions.Length, previewRequest.Regions.Count);
            var exportGraph = VideoEditorService.BuildFilter(exportRequest);
            var previewGraph = VideoEditorService.BuildFilter(previewRequest);

            for (var index = 0; index < regions.Length; index++)
            {
                var region = regions[index];
                var previewRegion = previewRequest.Regions[index];
                Equal(region.X, previewRegion.X);
                Equal(region.Y, previewRegion.Y);
                Equal(region.Width, previewRegion.Width);
                Equal(region.Height, previewRegion.Height);
                Equal(region.Effect, previewRegion.Effect);
                Equal(region.Id, previewRegion.Id);

                var sourcePixels = ((int X, int Y, int Width, int Height))pixelsMethod.Invoke(
                    null, [region, size.SourceWidth, size.SourceHeight])!;
                var previewPixels = ((int X, int Y, int Width, int Height))pixelsMethod.Invoke(
                    null, [previewRegion, size.PreviewWidth, size.PreviewHeight])!;
                AssertProjectedBoundary(sourcePixels.X, size.SourceWidth, previewPixels.X, size.PreviewWidth, "left");
                AssertProjectedBoundary(sourcePixels.Y, size.SourceHeight, previewPixels.Y, size.PreviewHeight, "top");
                AssertProjectedBoundary(sourcePixels.X + sourcePixels.Width, size.SourceWidth,
                    previewPixels.X + previewPixels.Width, size.PreviewWidth, "right");
                AssertProjectedBoundary(sourcePixels.Y + sourcePixels.Height, size.SourceHeight,
                    previewPixels.Y + previewPixels.Height, size.PreviewHeight, "bottom");

                AssertGraphGeometry(exportGraph, region.Effect, sourcePixels);
                AssertGraphGeometry(previewGraph, region.Effect, previewPixels);
            }
        }
        return Task.CompletedTask;

        static void AssertProjectedBoundary(int source, int sourceSize, int preview, int previewSize, string edge)
        {
            var projected = source * previewSize / (double)sourceSize;
            True(Math.Abs(preview - projected) < 1,
                $"processed Preview {edge} edge diverged from scaled Export geometry by one pixel or more");
        }

        static void AssertGraphGeometry(
            string graph,
            string effect,
            (int X, int Y, int Width, int Height) pixels)
        {
            var rectangle = $"{pixels.Width}:{pixels.Height}:{pixels.X}:{pixels.Y}";
            if (effect == "cover")
            {
                True(graph.Contains(
                    $"drawbox=x={pixels.X}:y={pixels.Y}:w={pixels.Width}:h={pixels.Height}",
                    StringComparison.Ordinal), "Cover graph bypassed shared pixel geometry");
                return;
            }
            True(graph.Contains("crop=" + rectangle, StringComparison.Ordinal),
                $"{effect} crop bypassed shared pixel geometry");
            True(graph.Contains($"overlay={pixels.X}:{pixels.Y}", StringComparison.Ordinal),
                $"{effect} overlay bypassed shared pixel geometry");
        }
    }

    private static Task EditorSubtitleDocumentContractAsync()
    {
        var raw = "10\r\n00:00:01,250 --> 00:00:02,900 position:50%\r\n你是谁？\r\n\r\n20\r\n00:00:03,000 --> 00:00:04,500\r\n本座乃青云宗长老。\r\n";
        var cues = EditorSubtitleDocument.Parse(raw);
        Equal(2, cues.Count);
        Equal("10", cues[0].Number);
        Equal("00:00:01,250 --> 00:00:02,900 position:50%", cues[0].Timing);
        Equal("20", cues[1].Number);
        var translated = new[] { cues[0] with { VietnameseText = "Ngươi là ai?" }, cues[1] with { VietnameseText = "Bổn tọa là trưởng lão Thanh Vân Tông." } };
        var rendered = EditorSubtitleDocument.RenderVietnamese(translated);
        True(rendered.Contains("10\r\n00:00:01,250 --> 00:00:02,900 position:50%", StringComparison.Ordinal), "editor SRT changed original numbering/timing");
        True(rendered.Contains("20\r\n00:00:03,000 --> 00:00:04,500", StringComparison.Ordinal), "editor SRT changed second timing");
        EditorSubtitleDocument.ValidateUnchangedTimeline(cues, translated);
        var imported = EditorSubtitleDocument.AttachVietnameseSrt(
            new EditorSubtitleSource("C:\\fixture\\source.srt", 1, 1, new string('a', 64), cues),
            new EditorSubtitleSource("C:\\fixture\\source.vi.srt", 1, 1, new string('b', 64), EditorSubtitleDocument.Parse(rendered)));
        Equal("Ngươi là ai?", imported.Cues[0].VietnameseText);
        var mismatchedTimeline = EditorSubtitleDocument.Parse(rendered.Replace("00:00:03,000 --> 00:00:04,500", "00:00:03,100 --> 00:00:04,500", StringComparison.Ordinal));
        var rejectedImportedTimeline = false;
        try
        {
            _ = EditorSubtitleDocument.AttachVietnameseSrt(
                new EditorSubtitleSource("C:\\fixture\\source.srt", 1, 1, new string('a', 64), cues),
                new EditorSubtitleSource("C:\\fixture\\source.vi.srt", 1, 1, new string('b', 64), mismatchedTimeline));
        }
        catch (InvalidDataException) { rejectedImportedTimeline = true; }
        True(rejectedImportedTimeline, "pretranslated SRT with mismatched timecode was accepted");
        var sourceRendered = EditorSubtitleDocument.RenderSource(cues);
        True(sourceRendered.Contains("你是谁？", StringComparison.Ordinal), "ASR/source SRT renderer lost Chinese text");
        Equal(2, EditorSubtitleDocument.Parse(sourceRendered).Count);
        foreach (var invalid in new[]
        {
            string.Empty,
            "1\nnot-a-timecode\n你好\n",
            "1\n00:00:02,000 --> 00:00:01,000\n你好\n",
            "1\n00:00:01,000 --> 00:00:02,000\n\n",
        })
        {
            var rejected = false;
            try { _ = EditorSubtitleDocument.Parse(invalid); }
            catch (InvalidDataException) { rejected = true; }
            True(rejected, "invalid Editor SRT was accepted");
        }
        return Task.CompletedTask;
    }

    private static async Task TranslationSkillBundleContractAsync()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dich-trung-tu-tien.zip");
        var bundle = TranslationSkillBundle.Load(fixture, requireBuiltInHash: true);
        Equal("Dịch Trung Tu Tiên", bundle.Info.Name);
        Equal(TranslationSkillBundle.BuiltInSha256, bundle.Info.Sha256);
        var prompt = bundle.BuildInstructions(["青云宗长老突破金丹境"], 56_000);
        True(prompt.Contains("QUY TẮC SKILL BẮT BUỘC", StringComparison.Ordinal), "skill core rules missing from prompt");
        True(prompt.Contains("青云宗", StringComparison.Ordinal) || prompt.Contains("金丹", StringComparison.Ordinal), "relevant glossary was not retrieved");

        await WithTemporaryRootAsync(async root =>
        {
            var malicious = Path.Combine(root, "bad.zip");
            await using (var stream = File.Create(malicious))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("dich-trung-tu-tien/../escape/SKILL.md");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("bad");
            }
            try { _ = TranslationSkillBundle.Load(malicious); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("translation skill ZIP path traversal was accepted");
        });
    }

    private static Task LocalTranslationManifestContractAsync()
    {
        var service = typeof(LocalSubtitleTranslationService);
        static object? Constant(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
        Equal(5_027_783_488L, (long)(Constant(service, "ModelBytes") ?? 0L));
        Equal("d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785", Constant(service, "ModelSha256")?.ToString());
        Equal(34_937_857L, (long)(Constant(service, "RuntimeArchiveBytes") ?? 0L));
        Equal("68e15a0a0d07df55a695ec4d81465cf57400431d54ae19fadcb51dc919724042", Constant(service, "RuntimeArchiveSha256")?.ToString());
        var recommend = service.GetMethod("RecommendedGpuLayers", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing local translation resource gate");
        var extract = service.GetMethod("ExtractJson", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("missing local translation JSON parser");
        var low = new HardwareResourceSnapshot(16L << 30, 10L << 30, true, 8L << 30, 2L << 30);
        var safe = new HardwareResourceSnapshot(32L << 30, 20L << 30, true, 8L << 30, 7L << 30);
        Equal(0, (int)recommend.Invoke(null, [low])!);
        Equal(99, (int)recommend.Invoke(null, [safe])!);
        var parsed = (JsonElement)extract.Invoke(null, ["echo prompt {\"id\":\"source\"}\nanswer: {\"bible\":\"Thanh Vân Tông\"}\n[end]"])!;
        Equal("Thanh Vân Tông", parsed.GetProperty("bible").GetString());
        return Task.CompletedTask;
    }

    private static Task LocalAsrManifestContractAsync()
    {
        var installer = typeof(LocalSubtitleTranslationService).Assembly.GetType("BiliSubStudio.Core.Editor.LocalAsrInstaller")
            ?? throw new InvalidOperationException("missing LocalAsrInstaller type");
        static object? Constant(Type type, string name) => type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
        Equal("1.2.1", Constant(installer, "FasterWhisperVersion")?.ToString());
        Equal("4.8.1", Constant(installer, "CTranslate2Version")?.ToString());
        True((Constant(installer, "FasterWhisperWheel")?.ToString() ?? string.Empty).EndsWith("#sha256=79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7", StringComparison.Ordinal),
            "faster-whisper wheel hash drift");
        True((Constant(installer, "CTranslate2Wheel")?.ToString() ?? string.Empty).EndsWith("#sha256=49f96e861b57301f0b76a082109bde2cac8204a6b4fedc870883008271e82251", StringComparison.Ordinal),
            "CTranslate2 Windows wheel hash drift");
        Equal("536b0662742c02347bc0e980a01041f333bce120", Constant(installer, "ModelRevision")?.ToString());
        var files = (Array?)(installer.GetField("ModelFiles", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null))
            ?? throw new InvalidOperationException("missing ASR model file manifest");
        Equal(4, files.Length);
        long total = 0;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var type = file!.GetType();
            total += (long)(type.GetProperty("Size")?.GetValue(file) ?? 0L);
            hashes.Add(type.GetProperty("Sha256")?.GetValue(file)?.ToString() ?? string.Empty);
        }
        Equal(486_212_372L, total);
        True(hashes.Contains("3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"), "ASR model.bin SHA-256 drift");
        Equal(5, EditorProjectStore.CurrentSchema);
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
        var cu133 = ((ValueTuple<string, string, string>)runtimeSpec.Invoke(null, ["gpu", Hardware("CUDA 13.3")])!).Item2;
        var cu128 = ((ValueTuple<string, string, string>)runtimeSpec.Invoke(null, ["gpu", Hardware("CUDA 12.8")])!).Item2;
        var cu125 = ((ValueTuple<string, string, string>)runtimeSpec.Invoke(null, ["gpu", Hardware("CUDA 12.5")])!).Item2;
        True(cu133.EndsWith("/cu129/", StringComparison.Ordinal), "CUDA 13.3 must select matching cu129/cuDNN 9.9 wheels");
        True(cu128.EndsWith("/cu118/", StringComparison.Ordinal), "CUDA 12.8 must avoid the mismatched cu126 Windows wheel");
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
        var rejected = new List<(int Failed, int Best)>();
        var selected = await InvokeAsync(
            select,
            (level, _) =>
            {
                attempted.Add(level);
                return level is 8 or 7
                    ? Task.FromException(new InvalidOperationException("fixture OOM"))
                    : Task.CompletedTask;
            },
            (level, _) => { restored.Add(level); return Task.CompletedTask; },
            (failed, best, _) => rejected.Add((failed, best)));
        if (selected != 6 || !attempted.SequenceEqual([1, 2, 4, 8, 7, 6]) || !restored.SequenceEqual([4, 4]) ||
            !rejected.SequenceEqual([(8, 4), (7, 4)]))
            throw new InvalidOperationException("OCR Auto did not restore 4 and select the highest viable fallback below failed level 8");

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

    private static async Task CleanupAwareJobCancellationContractAsync()
    {
        using var job = new AppJob("editor-test", "editor", cleanupAwareCancel: true);
        job.Cancel();
        var cancelling = job.Snapshot();
        Equal("cancelling", cancelling.Status);
        Equal(false, cancelling.Done);
        True(job.CancellationToken.IsCancellationRequested, "Editor token did not cancel");
        True(!job.Completion.IsCompleted, "Editor became terminal before FFmpeg cleanup");
        job.CancelComplete("FFmpeg stopped and partial render removed");
        await job.Completion;
        Equal("cancelled", job.Snapshot().Status);
        Equal("FFmpeg stopped and partial render removed", job.Snapshot().Message);
    }

    private static async Task InvalidOcrCheckpointContractAsync()
    {
        await WithTemporaryRootAsync(async root =>
        {
            var paths = AppPaths.FromRoot(root);
            var source = Path.Combine(root, "fixture.mp4");
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
            var request = new OcrScanRequest(source, new OcrRegion(.05, .65, .9, .29), "balanced", "cpu", "1", 1, 240);
            var storeType = typeof(OcrManager).Assembly.GetType("BiliSubStudio.Core.Ocr.OcrCheckpointStore")
                ?? throw new InvalidOperationException("missing OcrCheckpointStore");
            var store = Activator.CreateInstance(storeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [paths], culture: null) ?? throw new InvalidOperationException("cannot create checkpoint store");
            var remove = storeType.GetMethod("RemoveAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("missing checkpoint remove method");
            var operation = (Task?)remove.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("fresh checkpoint remove did not return a task");
            await operation;
            True(!Directory.Exists(Path.Combine(paths.Data, "OCRCheckpoints")),
                "fresh checkpoint removal created storage instead of tolerating its absence");

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
            operation = (Task?)load.Invoke(store, [request, CancellationToken.None])
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

            operation = (Task?)remove.Invoke(store, [request, CancellationToken.None])
                ?? throw new InvalidOperationException("checkpoint remove did not return a task");
            await operation;
            True(!File.Exists(Path.Combine(directory, key + ".json")), "checkpoint removal reported success while schema-4 file still existed");
        });
    }

    private static Task OcrExportAllCuesContractAsync() => WithTemporaryRootAsync(async root =>
    {
        var application = new BiliSubApplication(AppPaths.FromRoot(root));
        try
        {
            var cues = Enumerable.Range(0, 150)
                .Select(index => new OcrCue(index * .5, index * .5 + .4, $"第{index + 1}句", .99))
                .ToArray();
            var path = await application.ExportOcrAsync(cues, root, "all-cues.srt", CancellationToken.None);
            var blocks = (await File.ReadAllTextAsync(path))
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            Equal(cues.Length, blocks.Length);
            for (var index = 0; index < cues.Length; index++)
            {
                True(blocks[index].StartsWith($"{index + 1}\n", StringComparison.Ordinal),
                    $"OCR SRT block {index + 1} lost its independent sequence number");
                True(blocks[index].EndsWith(cues[index].Text, StringComparison.Ordinal),
                    $"OCR SRT block {index + 1} lost or merged its cue text");
            }
        }
        finally
        {
            var containmentField = typeof(BiliSubApplication).GetField("_containment", BindingFlags.Instance | BindingFlags.NonPublic);
            var containment = containmentField?.GetValue(application);
            if (containment is not null)
            {
                var jobField = containment.GetType().GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic);
                jobField?.SetValue(containment, IntPtr.Zero);
            }
            await application.DisposeAsync();
        }
    });

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

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
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

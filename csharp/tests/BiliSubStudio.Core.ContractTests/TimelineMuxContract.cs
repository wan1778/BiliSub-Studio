using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineMuxContract
{
    public static async Task RunAsync()
    {
        SegmentArgumentsAreExact();
        ConcatAndProbeArgumentsAreExact();
        CapabilityRejectsNonVideoAudioPlans();
        MuxValidationMathIsExact();
        await FieldMuxJoinsMixedFormatSegmentsAsync();
    }

    private static void SegmentArgumentsAreExact()
    {
        var (plan, assets) = CleanPlan();
        var clip = plan.Clips[0];
        var segment = new TimelineRenderSegment(
            0, clip, assets[0], plan.Audio.SourceAudio[0], plan.Audio, plan,
            clip.SequenceStartUs, clip.SequenceDurationUs);
        Check(TimelineFfmpegArguments.BuildSegment(segment, "C:/t/seg.rendering.mp4").SequenceEqual<string>(
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", "C:/video/mix-high.mp4",
            "-filter_complex",
            "[0:v]trim=start=0.000000:duration=10.000000,setpts=(PTS-STARTPTS)/1[sv];" +
            "[sv]scale=1920:1080,setsar=1,fps=30,format=yuv420p[vout];" +
            "[0:a]atrim=start=0.000000:duration=10.000000,asetpts=PTS-STARTPTS," +
            "aresample=48000,atempo=1,volume=1[aout]",
            "-map", "[vout]", "-map", "[aout]",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
            "-movflags", "+faststart", "C:/t/seg.rendering.mp4",
        ]), "1x audible segment argv drifted");

        var silent = plan.Clips[1];
        var silentSegment = new TimelineRenderSegment(
            1, silent, assets[1], plan.Audio.SourceAudio[1], plan.Audio, plan,
            silent.SequenceStartUs, silent.SequenceDurationUs);
        var silentArgs = string.Join(" ",
            TimelineFfmpegArguments.BuildSegment(silentSegment, "C:/t/seg.rendering.mp4"));
        Check(silentArgs.Contains("anullsrc=r=48000:cl=stereo", StringComparison.Ordinal)
            && silentArgs.Contains(
                "[1:a]atrim=duration=5.000000,asetpts=PTS-STARTPTS,aresample=48000,atempo=1,volume=1[aout]",
                StringComparison.Ordinal),
            "silent segment did not switch to bounded generated silence with matching normalization");

        var (ratePlan, rateAssets) = CleanPlan(rate: 2, gain: 0.5);
        var rateClip = ratePlan.Clips[0];
        var rateSegment = new TimelineRenderSegment(
            0, rateClip, rateAssets[0], ratePlan.Audio.SourceAudio[0],
            ratePlan.Audio, ratePlan, rateClip.SequenceStartUs, rateClip.SequenceDurationUs);
        var rateArgs = string.Join(" ",
            TimelineFfmpegArguments.BuildSegment(rateSegment, "C:/t/seg.rendering.mp4"));
        Check(rateArgs.Contains("setpts=(PTS-STARTPTS)/2")
            && rateArgs.Contains("atempo=2") && rateArgs.Contains("volume=0.5"),
            "2x segment did not carry rate-aware setpts/atempo and gain");

        var quarterArgs = string.Join(" ", TimelineFfmpegArguments.BuildSegment(
            rateSegment with
            {
                Clip = rateClip with { PlaybackRate = 0.25 },
            },
            "C:/t/seg.rendering.mp4"));
        Check(quarterArgs.Contains("atempo=0.5,atempo=0.5"),
            "sub-0.5x rate did not chain atempo filters");

        var allSilent = CleanPlan(allSilent: true);
        var muted = new TimelineRenderSegment(
            0, allSilent.Plan.Clips[0], allSilent.Assets[0],
            allSilent.Plan.Audio.SourceAudio[0], allSilent.Plan.Audio,
            allSilent.Plan, 0, allSilent.Plan.Clips[0].SequenceDurationUs);
        var mutedArgs = string.Join(" ",
            TimelineFfmpegArguments.BuildSegment(muted, "C:/t/seg.rendering.mp4"));
        Check(mutedArgs.Contains(" -an ", StringComparison.Ordinal)
            && !mutedArgs.Contains("[aout]", StringComparison.Ordinal),
            "all-silent plan segment must drop audio instead of generating silence");
    }

    private static void ConcatAndProbeArgumentsAreExact()
    {
        Check(TimelineFfmpegArguments.BuildConcat("C:/t/list.txt", "C:/t/out.mp4").SequenceEqual<string>(
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "concat", "-safe", "0", "-i", "C:/t/list.txt",
            "-c", "copy", "-movflags", "+faststart", "C:/t/out.mp4",
        ]), "concat demuxer argv drifted");
        Check(TimelineFfmpegArguments.BuildProbe("C:/t/out.mp4").SequenceEqual<string>(
        [
            "-v", "error",
            "-show_entries",
            "stream=index,codec_type,codec_name,width,height,sample_rate,avg_frame_rate,r_frame_rate,duration:format=duration",
            "-of", "json", "C:/t/out.mp4",
        ]), "mux probe argv drifted");
    }

    private static void CapabilityRejectsNonVideoAudioPlans()
    {
        var clean = CleanPlan().Plan;
        TimelineRenderCapability.EnsureVideoAudioOnly(clean);

        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var voiced = TimelineCompiler.Compile(
            TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets));
        Throws<InvalidOperationException>(
            () => TimelineRenderCapability.EnsureVideoAudioOnly(voiced),
            "voice/music plan passed the video-audio-only capability gate");

        var itemPlan = clean with
        {
            Items = [new TimelineResolvedItem(
                TimelineItem.NewId(), TimelineItemKind.Subtitle,
                clean.Clips[0].ClipId, 0, 1_000_000, "x.srt")],
        };
        Throws<InvalidOperationException>(
            () => TimelineRenderCapability.EnsureVideoAudioOnly(itemPlan),
            "plan with clip items passed the video-audio-only capability gate");

        var cue = new TimelineResolvedSubtitleCue(
            TimelineSubtitleCue.NewId(), TimelineItem.NewId(), clean.Clips[0].ClipId,
            0, 1_000_000, "text", new string('a', 64));
        Throws<InvalidOperationException>(
            () => TimelineRenderCapability.EnsureVideoAudioOnly(clean with { SubtitleCues = [cue] }),
            "plan with subtitle cues passed the video-audio-only capability gate");

        var voice = new TimelineResolvedVoiceCue(
            TimelineVoiceCue.NewId(), cue.CueId, TimelineAudioAsset.NewId(),
            "C:/audio/voice.flac", new string('b', 64),
            0, 1_000_000, 0, 22_050, 22_050, 1, 1);
        Throws<InvalidOperationException>(
            () => TimelineRenderCapability.EnsureVideoAudioOnly(clean with
            {
                Audio = clean.Audio with { VoiceCues = [voice] },
            }),
            "plan with voice cues passed the video-audio-only capability gate");

        var music = new TimelineResolvedMusic(
            TimelineMusicTrack.NewId(), TimelineAudioAsset.NewId(), "C:/audio/music.flac",
            new string('c', 64), 0, 1_000_000, 0, 48_000, 48_000, 2, 1, true);
        Throws<InvalidOperationException>(
            () => TimelineRenderCapability.EnsureVideoAudioOnly(clean with
            {
                Audio = clean.Audio with { Music = music },
            }),
            "plan with music passed the video-audio-only capability gate");

        Throws<InvalidOperationException>(
            () => TimelineFfmpegArguments.BuildSegment(
                new TimelineRenderSegment(0, voiced.Clips[0], fixture.MediaAssets[0],
                    voiced.Audio.SourceAudio[0], voiced.Audio, voiced, 0, 1_000_000),
                "C:/t/seg.rendering.mp4"),
            "segment builder rendered a voice/music plan instead of rejecting it");
    }

    private static void MuxValidationMathIsExact()
    {
        Check(TimelineMuxValidation.FieldDriftToleranceUs(16_667, 30) == 66_668,
            "G12 field drift tolerance drifted");
        var expectation = new TimelineMuxExpectation(1920, 1080, 30, true, 10_000_000, 16_667);
        TimelineMuxValidation.ValidateProbe(PassingProbeJson(), expectation);

        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("1920", "1280"), expectation),
            "mux validation accepted wrong geometry");
        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("\"h264\"", "\"hevc\""), expectation),
            "mux validation accepted a non-H.264 codec");
        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("30/1", "25/1"), expectation),
            "mux validation accepted wrong FPS");
        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("\"aac\"", "\"mp3\""), expectation),
            "mux validation accepted non-AAC audio");
        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("10.010000", "10.500000"), expectation),
            "mux validation accepted half-second duration drift");
        Throws<InvalidDataException>(
            () => TimelineMuxValidation.ValidateProbe(
                PassingProbeJson().Replace("10.010000", "10.100000"), expectation),
            "mux validation accepted 90ms A/V drift beyond DEC-019");
        var noStreamDurations = PassingProbeJson()
            .Replace(",\"duration\":\"10.000000\"", "")
            .Replace(",\"duration\":\"10.010000\"", "");
        TimelineMuxValidation.ValidateProbe(noStreamDurations, expectation);
    }

    private static string PassingProbeJson() =>
        """
        {"streams": [
          {"index": 0, "codec_type": "video", "codec_name": "h264",
           "width": 1920, "height": 1080,
           "avg_frame_rate": "30/1", "r_frame_rate": "30/1",
           "duration": "10.000000"},
          {"index": 1, "codec_type": "audio", "codec_name": "aac",
           "sample_rate": "48000", "duration": "10.010000"}
        ], "format": {"duration": "10.005000"}}
        """;

    private static async Task FieldMuxJoinsMixedFormatSegmentsAsync()
    {
        var ffmpeg = FindOnPath("ffmpeg");
        var ffprobe = FindOnPath("ffprobe");
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var redPath = Path.Combine(root, "red.mp4");
            var bluePath = Path.Combine(root, "blue.mp4");
            await RunToolAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=red:size=320x240:rate=30:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
                "-shortest", redPath,
            ]);
            await RunToolAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=blue:size=160x120:rate=25:duration=6",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                "-pix_fmt", "yuv420p", "-an", bluePath,
            ]);

            var red = TimelineMediaAsset.Create(redPath, 320, 240, 4_000_000, 30, hasAudio: true);
            var blue = TimelineMediaAsset.Create(bluePath, 160, 120, 6_000_000, 25, hasAudio: false);
            var first = new TimelineClip(TimelineClip.NewId(), red.Id, 0, 0, 3_000_000, 1);
            var second = new TimelineClip(TimelineClip.NewId(), blue.Id, 3_000_000, 1_000_000, 5_000_000, 2);
            var project = new TimelineProjectV2(
                TimelineProjectV2.NewId(),
                "G12 field mux",
                DateTimeOffset.UtcNow,
                [red, blue],
                new TimelineSequence([first, second], 320, 240, 30),
                [], [], [],
                TimelineSourceAudioState.Defaults([first, second]),
                [], null);
            var plan = TimelineCompiler.Compile(project);
            Check(plan.TotalDurationUs == 5_000_000, "field plan duration drifted");

            var paths = AppPaths.FromRoot(root);
            var service = new TimelineExportService(
                paths,
                new TimelineFfmpegSegmentRenderer(ffmpeg),
                new TimelineExportConcatList(paths),
                new TimelineFfmpegConcatenator(ffmpeg, ffprobe));
            using var jobs = new JobManager();
            var job = jobs.Create("timeline-export", cleanupAwareCancel: true);
            var exportTask = service.ExportAsync(
                job, plan, project, Path.Combine(root, "out"), "field.mp4", CancellationToken.None);
            var result = await exportTask.WaitAsync(TimeSpan.FromMinutes(5));
            if (!job.Snapshot().Done) job.Finish(null, "field export done");
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Check(result.SegmentCount == 2 && result.OutputDurationUs == 5_000_000,
                "field export result lost segment count or duration");
            Check(File.Exists(result.OutputPath) && new FileInfo(result.OutputPath).Length > 0,
                "field export published no output");

            var probe = await RunToolCaptureAsync(
                ffprobe, TimelineFfmpegArguments.BuildProbe(result.OutputPath));
            TimelineMuxValidation.ValidateProbe(
                probe, TimelineMuxValidation.BuildExpectation(plan));

            var firstFrame = await ExtractRawFrameAsync(ffmpeg, result.OutputPath, 1.0);
            var lastFrame = await ExtractRawFrameAsync(ffmpeg, result.OutputPath, 4.0);
            Check(ColorDominance(firstFrame).red - ColorDominance(firstFrame).blue > 40,
                "concat order drifted: expected red segment first");
            Check(ColorDominance(lastFrame).blue - ColorDominance(lastFrame).red > 40,
                "concat order drifted: expected blue segment last");

            var leftovers = Directory.EnumerateFiles(service.ExportDirectory)
                .Where(path => Path.GetFileName(path).StartsWith(
                    TimelineExportConcatList.ManagedSegmentPrefix + job.Id + "-",
                    StringComparison.Ordinal))
                .ToArray();
            Check(leftovers.Length == 0, "successful field export left owned segment artifacts");
            Check(job.Snapshot().Status == "done", "field export job did not finish done");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (int red, int blue) ColorDominance(byte[] rgb24)
    {
        long red = 0, blue = 0;
        for (var index = 0; index + 2 < rgb24.Length; index += 3)
        {
            red += rgb24[index];
            blue += rgb24[index + 2];
        }
        var pixels = Math.Max(1, rgb24.Length / 3);
        return ((int)(red / pixels), (int)(blue / pixels));
    }

    private static async Task<byte[]> ExtractRawFrameAsync(string ffmpeg, string input, double seconds)
    {
        var frame = await new ProcessRunner().CaptureBytesAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "-i", input, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
        ], CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        Check(frame.Length == 320 * 240 * 3, "field frame extraction returned unexpected bytes");
        return frame;
    }

    private static string FindOnPath(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), file);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException($"Field FFmpeg binary missing from PATH: {file}");
    }

    private static async Task RunToolAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await new ProcessRunner().RunAsync(
            executable, arguments, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)}: {result.StandardError.Trim()}");
    }

    private static async Task<string> RunToolCaptureAsync(string executable, IReadOnlyList<string> arguments)
    {
        string? captured = null;
        var result = await new ProcessRunner().RunAsync(
            executable, arguments, CancellationToken.None,
            standardOutputLine: line => captured = (captured ?? string.Empty) + line + "\n")
            .WaitAsync(TimeSpan.FromMinutes(2));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)}: {result.StandardError.Trim()}");
        return captured ?? result.StandardOutput;
    }

    private static (TimelineRenderPlan Plan, IReadOnlyList<TimelineMediaAsset> Assets) CleanPlan(
        double rate = 1, double gain = 1, bool allSilent = false)
    {
        var audible = TimelineMediaAsset.Create(
            "C:/video/mix-high.mp4", 1920, 1080, 60_000_000, 30, hasAudio: !allSilent);
        var silent = TimelineMediaAsset.Create(
            "C:/video/mix-low.mp4", 1280, 720, 30_000_000, 24, hasAudio: false);
        var first = new TimelineClip(TimelineClip.NewId(), audible.Id, 0, 0, 10_000_000, rate);
        var second = new TimelineClip(TimelineClip.NewId(), silent.Id, 10_000_000, 0, 5_000_000, 1);
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            "G12 mux",
            DateTimeOffset.UtcNow,
            [audible, silent],
            new TimelineSequence([first, second], 1920, 1080, 30),
            [], [], [],
            [new TimelineSourceAudioState(first.Id, gain),
             new TimelineSourceAudioState(second.Id, 1)],
            [], null);
        return (TimelineCompiler.Compile(project), [audible, silent]);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}

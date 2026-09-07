using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Processes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineFidelityContract
{
    public static async Task RunAsync()
    {
        EffectPayloadParsesExplicitly();
        EffectMappingUsesSourceTime();
        AssTextMatchesClassicBurn();
        FinalMixArgumentsAreExact();
        ConcatTimeCollapsesGaps();
        LogoPayloadParsesExplicitly();
        LogoMappingUsesSourceGeometry();
        await FieldFidelityExportAsync();
        await FieldLogoExportAsync();
    }

    private static async Task FieldLogoExportAsync()
    {
        var ffmpeg = FindOnPath("ffmpeg");
        var ffprobe = FindOnPath("ffprobe");
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var redPath = Path.Combine(root, "red.mp4");
            var bluePath = Path.Combine(root, "blue.mp4");
            var logoPath = Path.Combine(root, "mark.png");
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
            await RunToolAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=white:size=64x64:duration=1",
                "-frames:v", "1", logoPath,
            ]);

            var red = TimelineMediaAsset.Create(redPath, 320, 240, 4_000_000, 30, hasAudio: true);
            var blue = TimelineMediaAsset.Create(bluePath, 160, 120, 6_000_000, 25, hasAudio: false);
            var first = new TimelineClip(TimelineClip.NewId(), red.Id, 0, 0, 3_000_000, 1);
            var second = new TimelineClip(TimelineClip.NewId(), blue.Id, 3_000_000, 1_000_000, 5_000_000, 2);
            var logo = new TimelineItem(
                TimelineItem.NewId(), TimelineItemKind.Logo, null, 0, 0,
                0, 5_000_000, $"logo|{logoPath}|0.7,0.7,0.2,0.2|1");
            var project = new TimelineProjectV2(
                TimelineProjectV2.NewId(),
                "G16 logo",
                DateTimeOffset.UtcNow,
                [red, blue],
                new TimelineSequence([first, second], 320, 240, 30),
                [logo],
                [], [],
                TimelineSourceAudioState.Defaults([first, second]),
                [], null);
            var plan = TimelineCompiler.Compile(project);

            var paths = AppPaths.FromRoot(root);
            var service = new TimelineExportService(
                paths,
                new TimelineFfmpegSegmentRenderer(ffmpeg),
                new TimelineExportConcatList(paths),
                new TimelineFfmpegConcatenator(ffmpeg, ffprobe),
                new TimelineFfmpegFinalMixer(ffmpeg, ffprobe));
            using var jobs = new JobManager();
            var job = jobs.Create("timeline-export", cleanupAwareCancel: true);
            var exportTask = service.ExportAsync(
                job, plan, project, Path.Combine(root, "out"), "logo.mp4",
                CancellationToken.None);
            var result = await exportTask.WaitAsync(TimeSpan.FromMinutes(5));
            if (!job.Snapshot().Done) job.Finish(null, "logo export done");
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Check(result.SegmentCount == 2, "logo export lost segment count");
            var probe = await RunToolCaptureAsync(
                ffprobe, TimelineFfmpegArguments.BuildProbe(result.OutputPath));
            TimelineMuxValidation.ValidateProbe(
                probe, TimelineMuxValidation.BuildExpectation(plan));

            var frame = await ExtractRawFrameAsync(ffmpeg, result.OutputPath, 4.0, 320, 240);
            var (logoRed, _) = RectMean(frame, 320, 224, 168, 64, 48);
            var (bgRed, _) = RectMean(frame, 320, 0, 0, 64, 48);
            Check(logoRed > 150 && bgRed < 80,
                $"logo overlay did not land in its rect (logo R={logoRed}, bg R={bgRed})");
            Check(job.Snapshot().Status == "done", "logo export job did not finish done");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<byte[]> ExtractRawFrameAsync(
        string ffmpeg, string input, double seconds, int width, int height)
    {
        var frame = await new ProcessRunner().CaptureBytesAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "-i", input, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
        ], CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        Check(frame.Length == width * height * 3, "field frame extraction returned unexpected bytes");
        return frame;
    }

    private static (int red, int blue) RectMean(byte[] rgb24, int stride, int x, int y, int w, int h)
    {
        long red = 0, blue = 0;
        var pixels = 0;
        for (var row = y; row < y + h; row++)
        {
            for (var col = x; col < x + w; col++)
            {
                var offset = (row * stride + col) * 3;
                if (offset + 2 >= rgb24.Length) continue;
                red += rgb24[offset];
                blue += rgb24[offset + 2];
                pixels++;
            }
        }
        pixels = Math.Max(1, pixels);
        return ((int)(red / pixels), (int)(blue / pixels));
    }

    private static void EffectPayloadParsesExplicitly()
    {
        var blur = TimelineEffectSpec.Parse("blur:0.1,0.2,0.3,0.25:18", new string('a', 32));
        Check(blur.Effect == "blur" && blur.Strength == 18
            && Math.Abs(blur.X - 0.1) < 1e-9 && Math.Abs(blur.Height - 0.25) < 1e-9,
            "effect payload parse drifted");
        Check(TimelineEffectSpec.Parse("Blur:0,0,1,1:0", new string('a', 32)).Effect == "blur",
            "effect name must normalize case");
        foreach (var bad in new[]
        {
            "", "blur", "logo.png", "C:/subs/x.srt",
            "explode:0.1,0.1,0.2,0.2:18", "blur:0.1,0.2:18",
            "blur:a,b,c,d:18", "blur:0.1,0.1,2,2:18",
            "blur:0.1,0.1,0.2,0.2:-1", "blur:0.1,0.1,0.2,0.2:x",
            "blur:1,0,0.2,0.2:18",
        })
        {
            Throws<InvalidOperationException>(
                () => TimelineEffectSpec.Parse(bad, new string('a', 32)),
                $"opaque effect payload was accepted: '{bad}'");
        }
    }

    private static void EffectMappingUsesSourceTime()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/a.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 10_000_000, 20_000_000, 30_000_000, 1);
        var plan = SingleClipPlan(asset, clip).Plan;
        var item = new TimelineResolvedItem(
            TimelineItem.NewId(), TimelineItemKind.Effect, clip.Id,
            12_000_000, 14_000_000, "blur:0.1,0.2,0.3,0.25:18");
        var whole = new TimelineResolvedItem(
            TimelineItem.NewId(), TimelineItemKind.Effect, clip.Id,
            10_000_000, 20_000_000, "cover:0,0,1,1:0");
        var mappedPlan = plan with { Items = [item, whole] };
        var segment = new TimelineRenderSegment(
            0, mappedPlan.Clips[0], asset, mappedPlan.Audio.SourceAudio[0],
            mappedPlan.Audio, mappedPlan,
            mappedPlan.Clips[0].SequenceStartUs, mappedPlan.Clips[0].SequenceDurationUs);
        var regions = TimelineEffectGraph.MapEffectsForSegment(segment);
        Check(regions.Count == 2, "effect mapping lost a clip-local item");
        var timed = regions[0];
        Check(timed.Effect == "blur" && !timed.WholeVideo
            && Math.Abs(timed.Start - 22) < 1e-9 && Math.Abs(timed.End - 24) < 1e-9,
            "timed effect did not map clip-local time into source seconds");
        Check(regions[1].WholeVideo && regions[1].Effect == "cover",
            "full-clip effect did not derive whole-video coverage");

        var filter = TimelineEffectGraph.BuildComposedVideoFilter(segment, null);
        Check(filter is not null
            && filter.Contains("boxblur", StringComparison.Ordinal)
            && filter.Contains("crop=576:270:192:216", StringComparison.Ordinal)
            && filter.Contains("overlay=192:216:enable='between(t,22.000,24.000)'", StringComparison.Ordinal)
            && filter.Contains("drawbox=x=0:y=0:w=1920:h=1080", StringComparison.Ordinal),
            "composed filter lost classic blur pixels or time scope");
    }

    private static void AssTextMatchesClassicBurn()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/a.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
        var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 60_000_000, 1);
        var plan = SingleClipPlan(asset, clip).Plan;
        var cue = TimelineSubtitleCue.Create(
            TimelineSubtitleCue.NewId(), 11_000_000, 14_000_000, "Xin chào đạo hữu");
        var resolved = new TimelineResolvedSubtitleCue(
            cue.Id, cue.SubtitleItemId, clip.Id,
            11_000_000, 14_000_000, cue.SpokenText, cue.SpeechRevisionSha256);
        var mappedPlan = plan with { SubtitleCues = [resolved] };
        var segment = new TimelineRenderSegment(
            0, mappedPlan.Clips[0], asset, mappedPlan.Audio.SourceAudio[0],
            mappedPlan.Audio, mappedPlan, 0, mappedPlan.Clips[0].SequenceDurationUs);
        var burn = TimelineEffectGraph.MapBurnForSegment(segment);
        Check(burn is not null && burn.Cues.Count == 1
            && Math.Abs(burn.Cues[0].Start - 11) < 1e-9
            && Math.Abs(burn.Cues[0].End - 14) < 1e-9
            && burn.Cues[0].VietnameseText == "Xin chào đạo hữu",
            "subtitle burn mapping drifted cue time or text");
        var classic = new EditorSubtitleBurn(
            [new EditorSubtitleCue(cue.Id, "1", "t", 11, 14, cue.SpokenText, cue.SpokenText)],
            EditorSubtitlePlacement.Default, null, false, null);
        Check(TimelineEffectGraph.BuildAssText(burn!, 1920, 1080)
            == VideoEditorService.BuildAss(classic, 1920, 1080),
            "V2 ASS output diverged from the classic builder for identical cues");
    }

    private static void FinalMixArgumentsAreExact()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g13-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var masterPath = Path.Combine(root, "master.wav");
            File.WriteAllBytes(masterPath, [1]);
            var concatPath = Path.Combine(root, "concat.mp4");
            File.WriteAllBytes(concatPath, [2]);
            var outPath = Path.Combine(root, "final.mp4");
            var asset = TimelineMediaAsset.Create(
                "C:/video/a.mp4", 320, 240, 10_000_000, 30, hasAudio: true);
            var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 1);
            var plan = SingleClipPlan(asset, clip).Plan;
            var voiceAssetId = TimelineAudioAsset.NewId();
            var voice = new TimelineResolvedVoiceCue(
                TimelineVoiceCue.NewId(), TimelineSubtitleCue.NewId(), voiceAssetId,
                masterPath, new string('a', 64), 500_000, 2_500_000,
                0, 96_000, 48_000, 1, 1);
            var mixed = plan with
            {
                Audio = plan.Audio with { VoiceCues = [voice] },
            };
            var args = string.Join(" ",
                TimelineFfmpegFinalMixer.BuildFinalMixArguments(concatPath, mixed, outPath));
            Check(args.Contains($"-i {concatPath}", StringComparison.Ordinal)
                && args.Contains($"-i {masterPath}", StringComparison.Ordinal)
                && args.Contains(
                    "[1:a]atrim=start=0.000000:duration=2.000000,asetpts=PTS-STARTPTS," +
                    "adelay=500:all=1,volume=1[vc1_0]", StringComparison.Ordinal)
                && args.Contains(
                    "[base][vc1_0]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[aout]",
                    StringComparison.Ordinal)
                && args.Contains("-map 0:v -map [aout] -c:v copy -c:a aac",
                    StringComparison.Ordinal),
                "final mix argv drifted: " + args);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void LogoPayloadParsesExplicitly()
    {
        var logo = TimelineLogoSpec.Parse(
            @"logo|C:/img/My Logo.png|0.1,0.2,0.3,0.25|0.5", new string('a', 32));
        Check(logo.Path == @"C:/img/My Logo.png" && Math.Abs(logo.Opacity - 0.5) < 1e-9
            && Math.Abs(logo.X - 0.1) < 1e-9 && Math.Abs(logo.Height - 0.25) < 1e-9,
            "logo payload parse drifted");
        Check(TimelineLogoSpec.Parse(@"LOGO|D:\pics\l.JPG|0,0,1,1|1", new string('a', 32)).Path
                == @"D:\pics\l.JPG",
            "logo payload must survive drive-letter paths and case variation");
        foreach (var bad in new[]
        {
            "", "logo.png", "clip-logo",
            "logo|C:/a.png|0.1,0.2|0.5",
            "logo|C:/a.bmp|0.1,0.1,0.2,0.2|0.5",
            "logo|C:/a.png|0.1,0.1,0.2,0.2|0.04",
            "logo|C:/a.png|0.1,0.1,0.2,0.2|1.5",
            "logo|C:/a.png|0.9,0.9,0.2,0.2|1",
            "logo||0.1,0.1,0.2,0.2|1",
            "blur:0.1,0.1,0.2,0.2:18",
        })
        {
            Throws<InvalidOperationException>(
                () => TimelineLogoSpec.Parse(bad, new string('a', 32)),
                $"opaque logo payload was accepted: '{bad}'");
        }
    }

    private static void LogoMappingUsesSourceGeometry()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g16-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pngPath = Path.Combine(root, "mark.png");
            File.WriteAllBytes(pngPath, [137, 80, 78, 71]);
            var asset = TimelineMediaAsset.Create(
                "C:/video/a.mp4", 1920, 1080, 60_000_000, 30, hasAudio: true);
            var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 10_000_000, 20_000_000, 30_000_000, 1);
            var plan = SingleClipPlan(asset, clip).Plan;
            var local = new TimelineResolvedItem(
                TimelineItem.NewId(), TimelineItemKind.Logo, clip.Id,
                12_000_000, 14_000_000, $"logo|{pngPath}|0.1,0.2,0.3,0.25|0.5");
            var global = new TimelineResolvedItem(
                TimelineItem.NewId(), TimelineItemKind.Logo, null,
                0, 30_000_000, $"logo|{pngPath}|0,0,1,1|1");
            var mappedPlan = plan with { Items = [local, global] };
            var segment = new TimelineRenderSegment(
                0, mappedPlan.Clips[0], asset, mappedPlan.Audio.SourceAudio[0],
                mappedPlan.Audio, mappedPlan,
                mappedPlan.Clips[0].SequenceStartUs, mappedPlan.Clips[0].SequenceDurationUs);
            var overlays = TimelineEffectGraph.MapLogosForSegment(segment);
            Check(overlays.Count == 2, "logo mapping lost a clip-local or global item");
            var timed = overlays.Single(overlay => overlay.ItemId == local.ItemId);
            Check(timed.X == 192 && timed.Y == 216 && timed.Width == 576 && timed.Height == 270
                && Math.Abs(timed.Opacity - 0.5) < 1e-9
                && Math.Abs(timed.StartSeconds - 22) < 1e-9
                && Math.Abs(timed.EndSeconds - 24) < 1e-9
                && !timed.WholeVideo,
                "clip-local logo did not map to source pixels and seconds");
            var covering = overlays.Single(overlay => overlay.ItemId == global.ItemId);
            Check(covering.WholeVideo
                && covering.X == 0 && covering.Y == 0
                && covering.Width == 1920 && covering.Height == 1080,
                "window-covering logo did not derive whole-video coverage");

            var args = string.Join(" ",
                TimelineFfmpegArguments.BuildSegment(segment, "C:/t/seg.rendering.mp4"));
            Check(args.Contains("-loop 1 -framerate 1", StringComparison.Ordinal)
                && args.Contains(
                    "[1:v]trim=duration=10.000000,format=rgba,scale=1920:1080:flags=lanczos," +
                    "colorchannelmixer=aa=1.000[l0]",
                    StringComparison.Ordinal)
                && args.Contains("[nv][l0]overlay=0:0:eof_action=repeat:shortest=0[o0]",
                    StringComparison.Ordinal)
                && args.Contains(
                    "[2:v]trim=duration=10.000000,format=rgba,scale=576:270:flags=lanczos," +
                    "colorchannelmixer=aa=0.500[l1]",
                    StringComparison.Ordinal)
                && args.Contains(
                    "[o0][l1]overlay=192:216:eof_action=repeat:shortest=0:enable='between(t,22.000,24.000)'[vout]",
                    StringComparison.Ordinal),
                "logo overlay argv drifted: " + args);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void ConcatTimeCollapsesGaps()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/video/a.mp4", 320, 240, 60_000_000, 30, hasAudio: true);
        var first = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 2_000_000, 1);
        var second = new TimelineClip(TimelineClip.NewId(), asset.Id, 5_000_000, 10_000_000, 12_000_000, 1);
        var clips = new[]
        {
            new TimelineResolvedClip(first.Id, asset.Id, 0, 2_000_000, 0, 2_000_000, 1),
            new TimelineResolvedClip(second.Id, asset.Id, 5_000_000, 7_000_000, 10_000_000, 12_000_000, 1),
        };
        Check(TimelineEffectGraph.ConcatTimeUs(1_000_000, clips) == 1_000_000,
            "concat time drifted inside the first clip");
        Check(TimelineEffectGraph.ConcatTimeUs(4_000_000, clips) == 2_000_000,
            "concat time did not collapse the inter-clip gap");
        Check(TimelineEffectGraph.ConcatTimeUs(6_000_000, clips) == 3_000_000,
            "concat time drifted inside the second clip");
        Check(TimelineEffectGraph.ConcatTimeUs(99_000_000, clips) == 4_000_000,
            "concat time did not clamp past the last clip");
    }

    private static async Task FieldFidelityExportAsync()
    {
        var ffmpeg = FindOnPath("ffmpeg");
        var ffprobe = FindOnPath("ffprobe");
        var root = Path.Combine(Path.GetTempPath(), "bilisub-g13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var redPath = Path.Combine(root, "red.mp4");
            var bluePath = Path.Combine(root, "blue.mp4");
            var masterPath = Path.Combine(root, "master.wav");
            var musicPath = Path.Combine(root, "music.wav");
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
            await RunToolAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "sine=frequency=660:duration=10:sample_rate=48000",
                "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1", masterPath,
            ]);
            await RunToolAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "sine=frequency=880:duration=20:sample_rate=48000",
                "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1", musicPath,
            ]);

            var red = TimelineMediaAsset.Create(redPath, 320, 240, 4_000_000, 30, hasAudio: true);
            var blue = TimelineMediaAsset.Create(bluePath, 160, 120, 6_000_000, 25, hasAudio: false);
            var first = new TimelineClip(TimelineClip.NewId(), red.Id, 0, 0, 3_000_000, 1);
            var second = new TimelineClip(TimelineClip.NewId(), blue.Id, 3_000_000, 1_000_000, 5_000_000, 2);
            var subtitle = TimelineItemOps.AttachSubtitleToClip(
                first.Id, 0, first.DurationUs, Path.Combine(root, " Việt.srt"));
            var effect = new TimelineItem(
                TimelineItem.NewId(), TimelineItemKind.Effect, first.Id,
                0, first.DurationUs, 0, 0, "blur:0.1,0.1,0.3,0.25:18");
            var cue = TimelineSubtitleCue.Create(
                subtitle.Id, 500_000, 2_500_000, "Xin chào trường quay");
            var voiceAsset = new TimelineAudioAsset(
                TimelineAudioAsset.NewId(), TimelineAudioAssetKind.VoiceMaster,
                masterPath, 480_000, 48_000, 1,
                new FileInfo(masterPath).Length, 1, new string('d', 64));
            var musicAsset = new TimelineAudioAsset(
                TimelineAudioAsset.NewId(), TimelineAudioAssetKind.Music,
                musicPath, 960_000, 48_000, 1,
                new FileInfo(musicPath).Length, 2, new string('e', 64));
            var voice = new TimelineVoiceCue(
                TimelineVoiceCue.NewId(), cue.Id, voiceAsset.Id,
                0, 96_000, 1, cue.SpeechRevisionSha256);
            var music = new TimelineMusicTrack(
                TimelineMusicTrack.NewId(), musicAsset.Id, 1_000_000, 0, 144_000, .5);
            var timeline = TimelineEditState.Create([first, second], [subtitle, effect]);
            var state = TimelineAudioEditState.Create(
                timeline, [cue], [voiceAsset, musicAsset],
                TimelineSourceAudioState.Defaults([first, second]),
                [voice], music);
            var project = new TimelineProjectV2(
                TimelineProjectV2.NewId(),
                "G13 fidelity",
                DateTimeOffset.UtcNow,
                [red, blue],
                new TimelineSequence([first, second], 320, 240, 30),
                state.Timeline.Items,
                state.SubtitleCues,
                state.AudioAssets,
                state.SourceAudio,
                state.VoiceCues,
                state.Music);
            var plan = TimelineCompiler.Compile(project);
            Check(plan.TotalDurationUs == 5_000_000, "fidelity plan duration drifted");

            var paths = AppPaths.FromRoot(root);
            var service = new TimelineExportService(
                paths,
                new TimelineFfmpegSegmentRenderer(ffmpeg),
                new TimelineExportConcatList(paths),
                new TimelineFfmpegConcatenator(ffmpeg, ffprobe),
                new TimelineFfmpegFinalMixer(ffmpeg, ffprobe));
            using var jobs = new JobManager();
            var job = jobs.Create("timeline-export", cleanupAwareCancel: true);
            var exportTask = service.ExportAsync(
                job, plan, project, Path.Combine(root, "out"), "fidelity.mp4",
                CancellationToken.None);
            var result = await exportTask.WaitAsync(TimeSpan.FromMinutes(5));
            if (!job.Snapshot().Done) job.Finish(null, "fidelity export done");
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Check(result.SegmentCount == 2 && result.OutputDurationUs == 5_000_000,
                "fidelity export result lost segment count or duration");
            Check(File.Exists(result.OutputPath), "fidelity export published no output");
            var probe = await RunToolCaptureAsync(
                ffprobe, TimelineFfmpegArguments.BuildProbe(result.OutputPath));
            TimelineMuxValidation.ValidateProbe(
                probe,
                TimelineMuxValidation.BuildExpectation(plan) with { ExpectAudio = true });

            var leftovers = Directory.EnumerateFiles(service.ExportDirectory)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return (name.StartsWith(
                            TimelineExportConcatList.ManagedSegmentPrefix,
                            StringComparison.Ordinal)
                        || (name.EndsWith(".ass", StringComparison.OrdinalIgnoreCase)
                            && name.Contains(job.Id, StringComparison.Ordinal)))
                        && name.Contains(job.Id, StringComparison.Ordinal);
                })
                .ToArray();
            Check(leftovers.Length == 0, "fidelity export left owned temp artifacts");
            Check(job.Snapshot().Status == "done", "fidelity export job did not finish done");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (TimelineRenderPlan Plan, IReadOnlyList<TimelineMediaAsset> Assets) SingleClipPlan(
        TimelineMediaAsset asset, TimelineClip clip)
    {
        var project = new TimelineProjectV2(
            TimelineProjectV2.NewId(),
            "G13 fidelity unit",
            DateTimeOffset.UtcNow,
            [asset],
            new TimelineSequence([clip], asset.Width, asset.Height, 30),
            [], [], [],
            TimelineSourceAudioState.Defaults([clip]),
            [], null);
        return (TimelineCompiler.Compile(project), [asset]);
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

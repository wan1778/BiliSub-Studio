using System.Globalization;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.ContractTests;

// Exporting a multi-segment timeline: each segment is rendered separately and then concatenated
// into one file. The default part checks the parameter shape (no FFmpeg needed); the real runtime
// part runs FFmpeg to prove the output file holds both segments in the right order.
internal static class EditorComposedExportContract
{
    public static Task RunAsync()
    {
        var segment = new VideoEditSegment("E:\\video\\a.mp4", 30, 4, 10, 1920, 1080, []);
        Require(segment.Duration == 6, "a composed segment must expose its trimmed duration");
        var clipped = new VideoEditSegment("E:\\video\\a.mp4", 30, 20, 40, 1920, 1080, []);
        Require(clipped.Duration == 20, "a composed segment duration must never be negative");

        var export = new EditorExportSettings("h264", "standard", null, 25, PreferHardwareAcceleration: false, AudioBitrateKbps: 128);
        var encoder = new EditorResolvedVideoEncoder("libx264", false);
        var arguments = VideoEditorService.BuildComposedSegmentArguments(
            "E:\\video\\a.mp4", "E:\\out\\segment-00.mp4",
            "[0:v]setpts=PTS-STARTPTS[renderbase];[vout]scale=320:240[exportv]",
            EditorAudioSettings.Default, null, 4, 6, sourceHasAudio: true, outputAudio: true, export, encoder);
        var text = string.Join(" ", arguments);
        Require(arguments.Contains("-ss") && arguments.Contains("4.000"), "a composed segment must seek to its source start");
        Require(arguments.Contains("-t") && arguments.Contains("6.000"), "a composed segment must be limited to its own duration");
        Require(arguments.Contains("[exportv]"), "a composed segment must map its normalized video label");
        Require(arguments.Contains("[aout]"), "a composed segment must map its normalized audio label");
        Require(arguments.Contains("-ar") && arguments.Contains("48000"), "a composed segment must normalize the audio sample rate");
        Require(arguments.Contains("libx264"), "a composed segment must use the resolved export encoder");
        Require(!arguments.Contains("-an"), "a composed segment with audio must not drop the audio stream");

        var silent = VideoEditorService.BuildComposedSegmentArguments(
            "E:\\video\\b.mp4", "E:\\out\\segment-01.mp4",
            "[0:v]setpts=PTS-STARTPTS[renderbase];[vout]scale=320:240[exportv]",
            EditorAudioSettings.Default, null, 0, 3, sourceHasAudio: false, outputAudio: false, export, encoder);
        Require(silent.Contains("-an"), "a composed segment without audio must drop the audio stream instead of failing");

        var list = VideoEditorService.BuildConcatList(
            ["C:\\work\\segment-00.mp4", "C:\\work\\segment-01.mp4"], [2, 3]);
        Require(list.StartsWith("ffconcat version 1.0", StringComparison.Ordinal),
            "the concat list must declare the FFmpeg concat format");
        Require(list.Contains("file 'C:/work/segment-00.mp4'"), "the concat list must use forward slashes for FFmpeg");
        Require(list.Contains("file 'C:/work/segment-01.mp4'"), "the concat list must keep every composed segment");
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(lines.Length == 5,
            "the concat list must carry one file line and one duration directive per segment");
        Require(lines[2].StartsWith("duration ", StringComparison.Ordinal) && lines[4].StartsWith("duration ", StringComparison.Ordinal),
            "the concat list must carry the duration of every segment");
        Require(list.Split('\n').Count(line => line.StartsWith("file ", StringComparison.Ordinal)) == 2,
            "the concat list must contain exactly one file line per segment");

        var concat = VideoEditorService.BuildConcatArguments("C:\\work\\concat.txt", "E:\\out\\final.mp4", ".mp4");
        Require(concat.Contains("concat") && concat.Contains("-safe") && concat.Contains("0"),
            "the composed concat must use the concat demuxer with local paths");
        Require(concat.Contains("-c") && concat.Contains("copy"),
            "the composed concat must copy the normalized streams instead of encoding twice");
        Require(concat.Contains("-video_track_timescale"),
            "the composed concat must keep one time base for every joined segment");

        return Task.CompletedTask;
    }

    /// <summary>Real test: build two source segments, export the composed timeline, measure the output file.</summary>
    public static async Task<int> RunRuntimeAsync(string root, string toolsDirectory)
    {
        try
        {
            await RunRuntimeCoreAsync(root, toolsDirectory);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }

    private static async Task RunRuntimeCoreAsync(string root, string toolsDirectory)
    {
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        Directory.CreateDirectory(paths.Tools);
        foreach (var tool in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var source = Path.Combine(toolsDirectory, tool);
            if (!File.Exists(source)) throw new FileNotFoundException("Thiếu FFmpeg để chạy test runtime.", source);
            File.Copy(source, Path.Combine(paths.Tools, tool), overwrite: true);
        }

        var app = new BiliSubApplication(paths);
        var ffmpeg = await app.Tools.EnsureFfmpegAsync(CancellationToken.None);
        var ffprobe = await app.Tools.EnsureFfprobeAsync(CancellationToken.None);
        var workDirectory = Path.Combine(paths.Temp, "ComposedExportContract");
        Directory.CreateDirectory(workDirectory);
        var red = Path.Combine(workDirectory, "red.mp4");
        var blue = Path.Combine(workDirectory, "blue.mp4");
        var outputDirectory = Path.Combine(workDirectory, "out");
        Directory.CreateDirectory(outputDirectory);

        await RunFfmpegAsync(app, ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "color=c=red:s=320x240:d=2:r=25",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", red,
        ]);
        // The second segment has no audio: the export path must insert silence itself.
        await RunFfmpegAsync(app, ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "color=c=blue:s=640x360:d=3:r=25",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", blue,
        ]);
        // A separate voice track (simulated TTS) to check the voice-mixing path of the original segment.
        var voice = Path.Combine(workDirectory, "voice.wav");
        await RunFfmpegAsync(app, ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=2", voice,
        ]);

        var service = new VideoEditorService(paths, app.Tools, app.Processes);
        var job = app.Jobs.Create("editor", cleanupAwareCancel: true);
        var request = new VideoEditRequest(
            red,
            outputDirectory,
            "composed.mp4",
            320,
            240,
            2,
            [],
            Export: new EditorExportSettings("h264", "standard", null, null, PreferHardwareAcceleration: false, AudioBitrateKbps: 128),
            Segments:
            [
                new VideoEditSegment(red, 2, 0, 2, 320, 240, [], VoiceTrack: new EditorVoiceTrack(voice, 0, 2)),
                new VideoEditSegment(blue, 3, 0, 3, 640, 360, []),
            ]);
        var result = await service.RunAsync(job, request);
        Require(File.Exists(result.OutputPath), "the composed export must produce an output file");
        var probe = await app.Processes.RunAsync(ffprobe,
        [
            "-v", "error",
            "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration",
            "-of", "json", result.OutputPath,
        ], CancellationToken.None);
        Require(probe.ExitCode == 0, "ffprobe must read the composed output");
        var json = probe.StandardOutput;
        Require(json.Contains("\"codec_type\": \"video\""), "the composed output must keep a video stream");
        Require(json.Contains("\"codec_type\": \"audio\""), "the composed output must keep an audio stream even for silent clips");
        Require(json.Contains("\"width\": 320") && json.Contains("\"height\": 240"),
            "every composed segment must share the primary frame size");
        var duration = ReadDuration(json);
        Require(Math.Abs(duration - 5) < .6, $"the composed output must be as long as its segments (got {duration:0.000}s)");

        var first = await ReadCenterPixelAsync(app, ffmpeg, result.OutputPath, .5, 320, 240);
        var last = await ReadCenterPixelAsync(app, ffmpeg, result.OutputPath, 4.5, 320, 240);
        Require(first[0] > 180 && first[1] < 90 && first[2] < 90, "the first composed segment must keep the red frames");
        Require(last[2] > 180 && last[0] < 90 && last[1] < 90, "the second composed segment must keep the blue frames");
        Console.WriteLine("CHECK OK: composed export joined both segments in order");
    }

    private static async Task RunFfmpegAsync(BiliSubApplication app, string ffmpeg, IReadOnlyList<string> arguments)
    {
        var result = await app.Processes.RunAsync(ffmpeg, arguments, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException("FFmpeg runtime contract failed: " + result.StandardError.Trim());
    }

    private static async Task<byte[]> ReadCenterPixelAsync(
        BiliSubApplication app,
        string ffmpeg,
        string video,
        double at,
        int width,
        int height)
    {
        var raw = Path.Combine(Path.GetTempPath(), "bilisub-composed-pixel-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            var result = await app.Processes.RunAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
                "-ss", at.ToString("0.000", CultureInfo.InvariantCulture),
                "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw,
            ], CancellationToken.None);
            if (result.ExitCode != 0) throw new InvalidOperationException("FFmpeg frame read failed: " + result.StandardError.Trim());
            var bytes = await File.ReadAllBytesAsync(raw);
            var offset = ((height / 2) * width + (width / 2)) * 3;
            Require(bytes.Length >= offset + 3, "the extracted frame must contain pixel data");
            return bytes[offset..(offset + 3)];
        }
        finally
        {
            try { if (File.Exists(raw)) File.Delete(raw); } catch (IOException) { }
        }
    }

    private static double ReadDuration(string json)
    {
        var marker = "\"duration\": \"";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("ffprobe did not report the composed duration");
        start += marker.Length;
        var end = json.IndexOf('"', start);
        return double.Parse(json[start..end], CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

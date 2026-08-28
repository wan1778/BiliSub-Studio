using System.Security.Cryptography;
using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Opt-in: use an isolated data root with real installed ASR/TTS tools and a short
// real video plus Vietnamese SRT. Exercise the public ASR -> TTS job path, not
// the sample API or a fabricated speech-analysis document.
internal static class EditorAsrVoiceRuntimeContract
{
    public static async Task<int> RunAsync(string root, string video, string srt)
    {
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        // The application's Windows job owns this test process too; disposing it
        // during failure unwinding would terminate us before reporting the error.
        var app = new BiliSubApplication(paths);
        var report = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            report.Add(message);
            Console.WriteLine("CHECK OK: " + message);
        }
        static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        async Task<T> Wait<T>(string id)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            var last = "";
            while (true)
            {
                var job = app.Jobs.GetSnapshot(id);
                if (last != job.Message) { Console.WriteLine(job.Message); last = job.Message; }
                if (job.Done) return job.Result is T result ? result : throw new InvalidOperationException(job.Error ?? job.Message);
                if (DateTime.UtcNow > deadline)
                {
                    app.Jobs.Cancel(id);
                    while (!app.Jobs.GetSnapshot(id).Done) await Task.Delay(100);
                    throw new TimeoutException("ASR/Voice real runtime deadline");
                }
                await Task.Delay(100);
            }
        }

        Check(app.LocalAsrStatus.Ready, "existing real ASR runtime/model accepted before preparation");
        var install = Path.Combine(paths.Tools, "ASR", "runtime", "install.json");
        var installHash = Hash(install);
        var installTime = File.GetLastWriteTimeUtc(install);
        var media = await app.Media.ProbeAsync(video, CancellationToken.None);
        Check(media.Duration is > 0 and <= 90, "bounded real video clip (at most 90 seconds)");
        var source = EditorSubtitleDocument.UseVietnameseSrt(await EditorSubtitleDocument.LoadAsync(srt, CancellationToken.None));
        Check(source.Cues.Count >= 4 && source.Cues.All(cue => cue.End <= media.Duration), "at least four Vietnamese cues fit the real clip");
        var sourceHash = Hash(srt);
        var id = "asr-voice-" + Guid.NewGuid().ToString("N");
        var asr = await Wait<EditorAsrResult>(app.StartEditorAsr(new(id, video, media.Duration)));
        Check(asr.WordCount > 0 && asr.SegmentCount > 0, "real Whisper produced word timing through public ASR job");
        Check(Hash(install) == installHash && File.GetLastWriteTimeUtc(install) == installTime,
            "ASR preparation reused installed runtime without rewriting/reinstalling");
        var request = new EditorTtsRequest(id, video, media.Duration, source, asr.AnalysisPath, asr.AnalysisSha256);
        var tts = await Wait<EditorTtsResult>(app.StartEditorTts(request));
        Check(tts.Cues.Count == source.Cues.Count && File.Exists(tts.VoiceTrack.Path), "public TTS job accepts real ASR provenance and produces a master");
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(tts.ManifestPath)))
            Check(manifest.RootElement.GetProperty("cues").EnumerateArray().All(cue => !cue.GetProperty("cache_hit").GetBoolean()),
                "first public Voice run synthesized every whole cue with real Piper");
        var warm = await Wait<EditorTtsResult>(app.StartEditorTts(request));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(warm.ManifestPath)))
            Check(manifest.RootElement.GetProperty("cues").EnumerateArray().All(cue => cue.GetProperty("cache_hit").GetBoolean()),
                "second public Voice run reuses every verified cue cache");
        Check(Hash(tts.VoiceTrack.Path) == Hash(warm.VoiceTrack.Path), "warm Voice run preserves byte-identical master");
        var preview = await app.CreateEditorPreviewSegmentAsync(new VideoEditRequest(video, paths.DefaultDownloads,
            "asr-voice-preview.mp4", media.Width, media.Height, media.Duration, [],
            Audio: new EditorAudioSettings("mute", 0), VoiceTrack: warm.VoiceTrack), 0, CancellationToken.None);
        var ffmpeg = await app.Tools.EnsureFfmpegAsync(CancellationToken.None);
        var decode = await app.Processes.RunAsync(ffmpeg, ["-v", "error", "-i", preview.Path, "-map", "0:a:0", "-f", "null", "-"], CancellationToken.None);
        Check(decode.ExitCode == 0, "processed preview contains decodable generated voice");
        Check(Hash(srt) == sourceHash, "input Vietnamese SRT bytes remain unchanged");
        await File.WriteAllTextAsync(Path.Combine(root, "asr-voice-checks.json"), JsonSerializer.Serialize(new
        {
            checks = report, asr, tts, preview = preview.Path,
            core_sha256 = Hash(typeof(BiliSubApplication).Assembly.Location),
            voice_quality = "WAITING FOR USER FIELD TEST",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

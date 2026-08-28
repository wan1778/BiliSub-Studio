using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.Core.ContractTests;

// Opt-in real Windows integration: downloads pinned NGHI/Piper, synthesizes speech,
// and retains evidence. No mock model, synthetic oscillator, or fake timing input.
internal static class EditorNghiTtsRuntimeContract
{
    private static readonly string[] Sentences =
    [
        "Xin chào, tôi đang kiểm tra giọng đọc tiếng Việt của Ngọc Huyền.",
        "Hôm nay trời trong xanh, những hàng cây khẽ đung đưa trước gió.",
        "Đạo hữu hãy bình tĩnh, chúng ta vẫn còn cơ hội trở về nhà.",
        "Cảm ơn bạn đã lắng nghe. Chúc bạn một ngày thật bình an.",
    ];

    internal static async Task<int> RunAsync(string root, string video)
    {
        var paths = AppPaths.FromRoot(root);
        paths.EnsureBootstrapDirectories();
        // The application job object contains this test process itself. Do not close
        // it during exception unwinding, which would hide a failure behind exit 0.
        var app = new BiliSubApplication(paths);
        var service = typeof(BiliSubApplication).GetField("_tts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        var type = service.GetType();
        var generate = type.GetMethod("GenerateCuesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cueType = type.GetNestedType("TtsCueManifest", BindingFlags.NonPublic)!;
        var projectId = "runtime-" + Guid.NewGuid().ToString("N");
        var cacheRoot = Path.Combine(paths.Cache, "Editor", "TTS", projectId);
        var report = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            report.Add(message);
            Console.WriteLine("CHECK OK: " + message);
        }

        Array Cues(bool changed = false)
        {
            var array = Array.CreateInstance(cueType, Sentences.Length);
            for (var index = 0; index < Sentences.Length; index++)
                array.SetValue(Activator.CreateInstance(cueType,
                    $"real-cue-{index + 1}", index * 10d + .25, index * 10d + 9.75, "ngoc_huyen",
                    Sentences[index] + (changed ? " Đây là lượt kiểm tra hủy." : "")), index);
            return array;
        }

        async Task<EditorTtsResult?> Run(Array cues, double duration = 40, double? cancelAt = null)
        {
            var job = app.Jobs.Create("editor-tts", cleanupAwareCancel: true);
            var task = (Task<EditorTtsResult>)generate.Invoke(service,
                [job, projectId, duration, "ngoc_huyen", cues, new Dictionary<string, EditorCueSpeechTiming>()])!;
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
            var cancelled = false;
            var lastMessage = "";
            while (!task.IsCompleted)
            {
                var snapshot = job.Snapshot();
                if (snapshot.Message != lastMessage)
                {
                    Console.WriteLine(snapshot.Message);
                    lastMessage = snapshot.Message;
                }
                if (cancelAt.HasValue && snapshot.Progress >= cancelAt && !cancelled)
                {
                    job.Cancel();
                    cancelled = true;
                }
                if (DateTime.UtcNow > deadline) { job.Cancel(); throw new TimeoutException("NGHI integration deadline"); }
                await Task.Delay(cancelAt.HasValue ? 2 : 100);
            }
            try
            {
                var result = await task;
                job.Finish(null, "runtime evidence generated", result);
                if (cancelAt.HasValue) throw new InvalidOperationException("Cancellation stage was not observed");
                return result;
            }
            catch (OperationCanceledException) when (cancelled)
            {
                job.CancelComplete();
                Check(job.Snapshot().Done && job.Snapshot().Status == "cancelled", "cancel terminal only after service cleanup");
                return null;
            }
        }

        static JsonDocument Manifest(EditorTtsResult result) => JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        static int CacheHits(EditorTtsResult result)
        {
            using var json = Manifest(result);
            return json.RootElement.GetProperty("cues").EnumerateArray().Count(cue => cue.GetProperty("cache_hit").GetBoolean());
        }
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
        void CleanRunDirectories() => Check(!Directory.EnumerateDirectories(Path.Combine(cacheRoot, "runs")).Any(), "run-owned temporary directory removed");
        void NoChildren()
        {
            var owned = Process.GetProcesses().Where(process =>
            {
                try { return process.MainModule?.FileName?.StartsWith(paths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true; }
                catch { return false; }
                finally { process.Dispose(); }
            }).Count();
            Check(owned == 0, "no Python/FFmpeg child remains under isolated runtime root");
        }

        var first = (await Run(Cues()))!;
        Check(first.Cues.Count == 4 && CacheHits(first) == 0, "four Vietnamese cues synthesized without cache");
        Check(app.LocalTtsStatus.Ready, "installer manifest round-trip and pinned model/config validation");
        var firstHash = Hash(first.VoiceTrack.Path);
        using (var manifest = Manifest(first))
        {
            var cues = manifest.RootElement.GetProperty("cues").EnumerateArray().ToArray();
            for (var index = 0; index < cues.Length; index++)
            {
                var clip = cues[index].GetProperty("clip_path").GetString()!;
                File.Copy(clip, Path.Combine(paths.Root, $"sentence-{index + 1}.wav"), overwrite: true);
            }
        }
        File.Copy(first.VoiceTrack.Path, Path.Combine(paths.Root, "four-sentences.flac"), overwrite: true);
        CleanRunDirectories();
        var second = (await Run(Cues()))!;
        Check(CacheHits(second) == 4, "second run reuses all four verified whole-cue clips");
        Check(Hash(second.VoiceTrack.Path) == firstHash, "cache run preserves byte-identical master");
        Check(Hash(first.VoiceTrack.Path) == firstHash, "old master preserved after successful regeneration");

        using (var manifest = Manifest(first))
        {
            var clip = manifest.RootElement.GetProperty("cues")[0].GetProperty("clip_path").GetString()!;
            using var corrupt = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite);
            corrupt.Position = corrupt.Length - 2;
            var oldByte = corrupt.ReadByte();
            corrupt.Position--;
            corrupt.WriteByte((byte)(oldByte ^ 0x7f));
        }
        var repaired = (await Run(Cues()))!;
        Check(CacheHits(repaired) == 3, "same-size corrupted clip rejected and regenerated");

        await Run(Cues(changed: true), cancelAt: 51);
        CleanRunDirectories();
        NoChildren();
        Check(Hash(first.VoiceTrack.Path) == firstHash, "inference cancellation preserves previous master");
        var restarted = (await Run(Cues(changed: true)))!;
        Check(CacheHits(restarted) >= 1, "cancel/retry retains completed cue cache");

        await Run(Cues(), duration: 7200, cancelAt: 91.01);
        CleanRunDirectories();
        NoChildren();
        Check(Hash(first.VoiceTrack.Path) == firstHash, "master cancellation preserves previous complete track");
        var sampleId = app.StartEditorTtsSample("ngoc_huyen");
        while (!app.Jobs.GetSnapshot(sampleId).Done) await Task.Delay(100);
        Check(app.Jobs.GetSnapshot(sampleId).Result is EditorTtsResult, "real sample API succeeds without video/SRT/fabricated timing");

        var media = await app.Media.ProbeAsync(video, CancellationToken.None);
        var editor = new VideoEditorService(paths, app.Tools, app.Processes);
        var preview = await editor.CreatePreviewSegmentAsync(new VideoEditRequest(video, paths.DefaultDownloads,
            "voice-preview.mp4", media.Width, media.Height, 40, [], Audio: new EditorAudioSettings("mute", 0),
            VoiceTrack: first.VoiceTrack), 0, CancellationToken.None);
        File.Copy(preview.Path, Path.Combine(paths.Root, "voice-preview.mp4"), overwrite: true);
        var ffmpeg = await app.Tools.EnsureFfmpegAsync(CancellationToken.None);
        var decode = await app.Processes.RunAsync(ffmpeg,
            ["-v", "error", "-i", preview.Path, "-map", "0:a:0", "-f", "null", "-"], CancellationToken.None);
        Check(decode.ExitCode == 0, "real Editor processed preview contains decodable generated voice");
        await editor.DeletePreviewSegmentAsync(preview.Path);
        var store = new EditorProjectStore(paths);
        var project = await store.LoadOrCreateAsync(video, media.Width, media.Height, media.Duration, CancellationToken.None);
        var subtitlePath = Path.Combine(paths.Root, "field.vi.srt");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,250 --> 00:00:09,750\n" + Sentences[0] + "\n");
        var subtitle = await EditorSubtitleDocument.LoadAsync(subtitlePath, CancellationToken.None);
        project = project with
        {
            Subtitle = new EditorSubtitleProject(subtitle.Path, subtitle.Size, subtitle.LastWriteUtcTicks, subtitle.Sha256,
                [subtitle.Cues[0] with { VietnameseText = Sentences[0] }], new EditorSubtitlePlacement(.1, .72, .8, .18),
                "Dịch Trung Tu Tiên", TranslationSkillBundle.BuiltInSha256, subtitlePath),
            Tts = new EditorTtsProject("complete", first.Engine, first.EngineVersion, first.Voice, first.Voice,
                first.ManifestPath, first.ManifestSha256, first.VoiceTrack, first.Cues.Count, first.ReviewCount),
        };
        await store.SaveAsync(project, CancellationToken.None);
        var reopened = await store.LoadOrCreateAsync(video, media.Width, media.Height, media.Duration, CancellationToken.None);
        Check(reopened.Tts?.VoiceTrack.Path == first.VoiceTrack.Path, "project reopen retains verified NGHI master");
        NoChildren();
        await File.WriteAllTextAsync(Path.Combine(paths.Root, "runtime-checks.json"), JsonSerializer.Serialize(new
        {
            sentences = Sentences, checks = report, first.ManifestPath, master_sha256 = firstHash,
            listening = "NOT VERIFIED BY AUTOMATED CHECKS",
            voice_quality = "WAITING FOR USER FIELD TEST",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("AUTOMATED CHECKS COMPLETE. Technical/runtime PASS requires listening confirmation; voice quality WAITING FOR USER FIELD TEST.");
        return 0;
    }
}

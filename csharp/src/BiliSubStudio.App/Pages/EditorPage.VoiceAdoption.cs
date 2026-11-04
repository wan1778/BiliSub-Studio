using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.App.Pages;

// Voice self-recovery 08/10 (user: leaving a project and coming back lost the
// voice and it had to be regenerated — the root cause was the old NormalizeTts
// discarding the vieneu engine on every save).
// When opening a project whose Tts is empty but whose cache still holds a valid
// result, re-adopt the newest result (without forcing a regeneration). Guarding
// against adopting the wrong (stale) copy: only adopt a result that matches all
// current cue IDs plus passes full validation through
// EditorProjectStore.NormalizeTts (manifest/master sha, engine revision,
// sample-exact windows), and honor the revocation marker voice-revoked.json
// written by the subtitle-edit and ASR-rerun flows (editing text after voice
// generation means no adoption).
public sealed partial class EditorPage
{
    private const string VoiceRevokedFileName = "voice-revoked.json";

    private async Task<bool> TryAdoptCachedVoiceAsync()
    {
        var project = _project;
        var media = _media;
        var subtitle = _subtitleSource;
        if (project is null || media is null || subtitle is null
            || project.Tts is { Status: "complete" }
            || subtitle.Cues.Count == 0
            || subtitle.Cues.Any(x => string.IsNullOrWhiteSpace(x.VietnameseText)))
            return false;
        string outputRoot;
        try { outputRoot = Path.Combine(_application.Paths.Cache, "Editor", "TTS", project.Id); }
        catch { return false; }
        List<FileInfo> results;
        try
        {
            results = new DirectoryInfo(outputRoot).GetFiles("result-*.json")
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc).Take(3).ToList();
        }
        catch { return false; }
        if (results.Count == 0) return false;
        DateTime revokeUtc;
        try { revokeUtc = File.GetLastWriteTimeUtc(Path.Combine(outputRoot, VoiceRevokedFileName)); }
        catch { revokeUtc = DateTime.MinValue; }
        foreach (var result in results)
        {
            EditorTtsProject? normalized = null;
            try
            {
                if (result.LastWriteTimeUtc <= revokeUtc) continue;
                var candidate = BuildAdoptedTts(result.FullName, subtitle.Cues, media.Duration);
                if (candidate is null) continue;
                normalized = EditorProjectStore.NormalizeTts(candidate);
            }
            catch { continue; }
            if (normalized is null) continue;
            _voiceTrack = normalized.VoiceTrack with { Gain = _voiceGain };
            _voiceCueWindows = (normalized.CueWindows ?? [])
                .ToDictionary(window => window.Id, StringComparer.Ordinal);
            _project = project with
            {
                Tts = normalized with { VoiceGain = _voiceGain, VoiceTrack = _voiceTrack },
            };
            await SaveProjectNowAsync();
            VoiceStatusText.Text =
                $"Đã khôi phục voice đã tạo · {normalized.CueCount} câu · không cần chạy lại.";
            RefreshEditorActions();
            return true;
        }
        return false;
    }

    private static EditorTtsProject? BuildAdoptedTts(
        string resultPath, IReadOnlyList<EditorSubtitleCue> cues, double duration)
    {
        var bytes = File.ReadAllBytes(resultPath);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = document.RootElement;
        var resultCues = root.GetProperty("cues").EnumerateArray().ToArray();
        if (resultCues.Length != cues.Count) return null;
        var windows = new EditorTtsCueWindow[resultCues.Length];
        for (var index = 0; index < resultCues.Length; index++)
        {
            var cue = resultCues[index];
            if (!string.Equals(cue.GetProperty("id").GetString(), cues[index].Id, StringComparison.Ordinal))
                return null;
            var startSample = cue.GetProperty("clip_start_sample").GetInt64();
            var targetFrames = cue.GetProperty("target_frames").GetInt64();
            windows[index] = new EditorTtsCueWindow(
                cues[index].Id,
                startSample / 22050d,
                (startSample + targetFrames) / 22050d,
                cue.GetProperty("timing_source").GetString() ?? string.Empty,
                cue.GetProperty("status").GetString() ?? string.Empty);
        }
        var masterPath = root.GetProperty("master").GetProperty("path").GetString() ?? string.Empty;
        var voice = root.GetProperty("voice").GetString() ?? string.Empty;
        return new EditorTtsProject(
            "complete",
            root.GetProperty("engine").GetString() ?? string.Empty,
            root.GetProperty("engine_version").GetString() ?? string.Empty,
            voice,
            voice,
            resultPath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            new EditorVoiceTrack(masterPath, 0, duration),
            resultCues.Length,
            root.GetProperty("review_count").GetInt32(),
            windows,
            1);
    }

    private void WriteVoiceRevokedMarker(string reason)
    {
        try
        {
            if (_project is null) return;
            var root = Path.Combine(_application.Paths.Cache, "Editor", "TTS", _project.Id);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, VoiceRevokedFileName),
                "{\"utc\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\",\"reason\":\"" + reason + "\"}");
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("editor-voice-revoked-marker: " + error);
        }
    }
}

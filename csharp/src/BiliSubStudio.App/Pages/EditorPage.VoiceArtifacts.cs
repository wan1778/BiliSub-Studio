using System.IO;
using Microsoft.UI.Dispatching;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private DispatcherQueueTimer? _voiceArtifactMonitorTimer;
    private FileSystemWatcher? _voiceArtifactWatcher;
    private bool _voiceArtifactRecoveryRunning;

    private void StartVoiceArtifactMonitor()
    {
        if (_voiceArtifactMonitorTimer is null)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += VoiceArtifactMonitor_Tick;
            _voiceArtifactMonitorTimer = timer;
        }
        _voiceArtifactMonitorTimer.Start();

        if (_voiceArtifactWatcher is null)
        {
            try
            {
                Directory.CreateDirectory(_application.Paths.Cache);
                var watcher = new FileSystemWatcher(_application.Paths.Cache)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true,
                };
                watcher.Deleted += VoiceArtifactWatcher_Deleted;
                watcher.Renamed += VoiceArtifactWatcher_Renamed;
                _voiceArtifactWatcher = watcher;
            }
            catch
            {
                _voiceArtifactWatcher?.Dispose();
                _voiceArtifactWatcher = null;
            }
        }

        ReconcileMissingVoiceArtifacts();
    }

    private void StopVoiceArtifactMonitor()
    {
        _voiceArtifactMonitorTimer?.Stop();
        if (_voiceArtifactWatcher is not null)
        {
            _voiceArtifactWatcher.EnableRaisingEvents = false;
            _voiceArtifactWatcher.Deleted -= VoiceArtifactWatcher_Deleted;
            _voiceArtifactWatcher.Renamed -= VoiceArtifactWatcher_Renamed;
            _voiceArtifactWatcher.Dispose();
            _voiceArtifactWatcher = null;
        }
        _voiceArtifactRecoveryRunning = false;
    }

    private void VoiceArtifactMonitor_Tick(DispatcherQueueTimer sender, object args) =>
        ReconcileMissingVoiceArtifacts();

    private void VoiceArtifactWatcher_Deleted(object sender, FileSystemEventArgs e) =>
        QueueVoiceArtifactReconcile(e.FullPath);

    private void VoiceArtifactWatcher_Renamed(object sender, RenamedEventArgs e) =>
        QueueVoiceArtifactReconcile(e.OldFullPath);

    private void QueueVoiceArtifactReconcile(string changedPath)
    {
        if (!IsReferencedVoiceArtifact(changedPath)) return;
        DispatcherQueue.TryEnqueue(ReconcileMissingVoiceArtifacts);
    }

    private bool IsReferencedVoiceArtifact(string path)
    {
        var project = _project;
        if (project is null || string.IsNullOrWhiteSpace(path)) return false;
        return SamePath(project.Speech?.AnalysisPath, path)
            || SamePath(project.Tts?.ManifestPath, path)
            || SamePath(project.Tts?.VoiceTrack?.Path, path)
            || SamePath(_voiceTrack?.Path, path);
    }

    private void ReconcileMissingVoiceArtifacts()
    {
        if (_voiceArtifactRecoveryRunning || _project is not { } project) return;
        var speech = project.Speech;
        var tts = project.Tts;
        var speechCacheMissing = speech is { Status: "complete" }
            && MissingFile(speech.AnalysisPath);
        var ttsLostOwner = tts is not null && speech is not { Status: "complete" };
        var ttsCacheMissing = tts is { Status: "complete" }
            && (MissingFile(tts.ManifestPath) || MissingFile(tts.VoiceTrack?.Path));
        var runtimeVoiceMissing = _voiceTrack is not null && MissingFile(_voiceTrack.Path);
        if (!speechCacheMissing && !ttsLostOwner && !ttsCacheMissing && !runtimeVoiceMissing) return;

        _voiceArtifactRecoveryRunning = true;
        try
        {
            ClearVoiceTrackState();
            if (speechCacheMissing)
            {
                _cueSpeechTiming = [];
                _project = project with { Speech = null, Tts = null };
                AsrStatusText.Text = "Whisper timing cache đã bị mất; đã reset phân tích nhịp và voice phụ thuộc.";
                VoiceStatusText.Text = "Cache Whisper bị mất; voice Việt cũ đã được gỡ. Hãy phân tích nhịp lại trước khi tạo voice.";
            }
            else
            {
                _project = project with { Tts = null };
                VoiceStatusText.Text = ttsLostOwner
                    ? "Voice cũ không còn Whisper timing owner hợp lệ; đã gỡ TTS nhưng giữ các state không phụ thuộc."
                    : "File voice/cache đã bị mất; đã gỡ voice cũ. Whisper timing vẫn được giữ để có thể tạo lại voice.";
            }
            QueueProjectSave();
            RefreshEditorActions();
            if (_playback.IsPreviewMode && !_playback.IsRendering)
                _ = ExitPreviewAfterVoiceArtifactLossAsync(project.Id);
        }
        finally
        {
            _voiceArtifactRecoveryRunning = false;
        }
    }

    private async Task ExitPreviewAfterVoiceArtifactLossAsync(string projectId)
    {
        try
        {
            if (!string.Equals(_project?.Id, projectId, StringComparison.Ordinal) || !_playback.IsPreviewMode) return;
            await _playback.SetModeAsync(enabled: false, play: false);
            if (string.Equals(_project?.Id, projectId, StringComparison.Ordinal))
                StatusText.Text = "Đã dừng bản xem trước cũ vì file voice/cache không còn tồn tại.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (string.Equals(_project?.Id, projectId, StringComparison.Ordinal))
                StatusText.Text = "Không dừng được preview sau khi mất voice/cache: " + error.Message;
        }
    }

    private static bool MissingFile(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) || !File.Exists(Path.GetFullPath(path.Trim())); }
        catch { return true; }
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left.Trim()), Path.GetFullPath(right.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// Per-clip Vietnamese SRT and the voice generated from it. The project keeps
/// one SRT for the primary video (the existing Subtitle/Tts fields); every
/// other timeline clip binds its own SRT here, keyed by the clip id, so a
/// composed timeline with 3-4 videos can burn and voice each one separately.
/// </summary>
public sealed partial class EditorPage
{
    private readonly Dictionary<string, EditorSubtitleSource> _clipSubtitleSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorTtsProject> _clipTts = new(StringComparer.Ordinal);
    private readonly Queue<string> _clipTtsQueue = new();
    private string? _ttsTargetClipId;
    private bool _ttsLastJobApplied;
    private int _clipTtsTotal;

    /// <summary>Clip selected on the timeline, or null when nothing is selected.</summary>
    private EditorTimelineClip? SelectedTimelineClip()
        => _timelineSelectedClipIndex >= 0 && _timelineSelectedClipIndex < _timelineClips.Count
            ? _timelineClips[_timelineSelectedClipIndex]
            : null;

    private bool HasClipSubtitle(string clipId) => _clipSubtitleSources.ContainsKey(clipId);

    private bool ClipSubtitleFingerprintMatches(string clipId)
        => _clipSubtitleSources.TryGetValue(clipId, out var source)
            && EditorSubtitleDocument.SourceFingerprintMatchesCurrent(source);

    private void EnsureClipSubtitleFingerprint(EditorTimelineClip clip)
    {
        if (HasClipSubtitle(clip.Id) && !ClipSubtitleFingerprintMatches(clip.Id))
            throw new InvalidDataException(
                $"SRT của đoạn “{clip.Label}” đã thay đổi ngoài ứng dụng; hãy gắn lại SRT cho đoạn này trước khi tiếp tục.");
    }

    /// <summary>Preview subtitle burn of one clip: missing Vietnamese falls back to the SRT text.</summary>
    private EditorSubtitleBurn? ClipPreviewSubtitleBurn(string clipId)
    {
        if (!_subtitleVisible || !_clipSubtitleSources.TryGetValue(clipId, out var source) || source.Cues.Count == 0)
            return null;
        var cues = source.Cues.Select(cue => cue with
        {
            VietnameseText = string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText,
        }).ToArray();
        return new EditorSubtitleBurn(cues, _subtitlePlacement, [], KaraokeToggle.IsOn, _subtitleStyle);
    }

    /// <summary>Export subtitle burn of one clip: only a fully translated SRT may be burned.</summary>
    private EditorSubtitleBurn? ClipCompletedSubtitleBurn(string clipId)
    {
        if (!_subtitleVisible || !_clipSubtitleSources.TryGetValue(clipId, out var source) || source.Cues.Count == 0
            || source.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.VietnameseText)))
            return null;
        return new EditorSubtitleBurn(source.Cues, _subtitlePlacement, [], KaraokeToggle.IsOn, _subtitleStyle);
    }

    /// <summary>Subtitle visible at a source-file time inside one clip (static frame preview).</summary>
    private EditorSubtitleBurn? ClipFrameSubtitleBurn(string clipId, double sourceTime)
    {
        if (!_subtitleVisible || !_clipSubtitleSources.TryGetValue(clipId, out var source)) return null;
        var cues = source.Cues
            .Where(cue => sourceTime >= cue.Start && sourceTime < cue.End)
            .Select(cue => cue with { VietnameseText = SubtitlePreviewText(cue) })
            .Where(cue => !string.IsNullOrWhiteSpace(cue.VietnameseText))
            .ToArray();
        if (cues.Length == 0) return null;
        return new EditorSubtitleBurn(cues, _subtitlePlacement, [], KaraokeToggle.IsOn, _subtitleStyle);
    }

    /// <summary>Voice master of one clip, using the current global voice gain.</summary>
    private EditorVoiceTrack? ClipVoiceTrack(string clipId)
    {
        if (!_clipTts.TryGetValue(clipId, out var tts) || tts.Status != "complete"
            || !File.Exists(tts.VoiceTrack.Path))
            return null;
        return tts.VoiceTrack with { Gain = _voiceGain };
    }

    private bool NeedsClipEnrichment() => _clipSubtitleSources.Count > 0 || _clipTts.Count > 0;

    // Attaching a new SRT to one clip invalidates exactly that clip's voice:
    // every other clip keeps its own SRT/voice.
    private async void TimelineClipSubtitleButton_Click(object sender, RoutedEventArgs args)
    {
        if (!TimelineEditingAllowed()) return;
        var clip = SelectedTimelineClip();
        if (clip is null)
        {
            StatusText.Text = "Hãy bấm vào một đoạn video trên dải thời gian để gắn SRT cho đoạn đó.";
            return;
        }
        if (IsPrimaryTimelineClip(clip))
        {
            // The project's own video keeps using the main SRT panel, so there
            // is only one owner for the primary subtitle/voice state.
            try { await ImportSubtitleAsync(); }
            catch (OperationCanceledException) { }
            catch (InvalidDataException error)
            {
                SubtitleStatusText.Text = "SRT không hợp lệ: " + error.Message;
                RefreshEditorActions();
            }
            catch (Exception error)
            {
                SubtitleStatusText.Text = "Không nhập được SRT: " + error.Message;
                RefreshEditorActions();
            }
            return;
        }
        try
        {
            var path = await _picker.PickSubtitleAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            await AttachClipSubtitleAsync(clip, path);
            await SaveProjectNowAsync();
            QueueTimelineRender();
            if (!_playback.IsPreviewMode) QueuePreviewRefresh();
            StatusText.Text =
                $"Đã gắn SRT Việt riêng cho đoạn “{clip.Label}” · {_clipSubtitleSources[clip.Id].Cues.Count} câu. "
                + "Bấm «Tạo voice» để đọc tất cả các đoạn có SRT.";
            NotifyEditorCompositeChanged();
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException error)
        {
            StatusText.Text = "SRT của đoạn không hợp lệ: " + error.Message;
        }
        catch (Exception error)
        {
            StatusText.Text = "Không gắn được SRT cho đoạn: " + error.Message;
        }
        RefreshEditorActions();
    }

    /// <summary>Loads and binds one SRT to one clip; the caller owns the save/render.</summary>
    internal async Task AttachClipSubtitleAsync(EditorTimelineClip clip, string path)
    {
        var candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);
        candidate = EditorSubtitleDocument.UseVietnameseSrt(candidate);
        _clipSubtitleSources[clip.Id] = candidate;
        _clipTts.Remove(clip.Id);
    }

    /// <summary>
    /// Imports one or more videos the way an editor imports a media batch: every
    /// video is appended in one operation and its Vietnamese sidecar SRT is found
    /// by name/episode and attached without another dialog. Explicit SRTs from a
    /// drop win over the folder scan. Returns the complete clip list, or null
    /// when nothing was added; the caller commits it through the timeline owner.
    /// </summary>
    internal async Task<IReadOnlyList<EditorTimelineClip>?> ImportVideosAsync(
        IReadOnlyList<string> requested, IReadOnlyList<string>? droppedSubtitles = null)
    {
        if (_project is null || requested.Count == 0) return null;
        var known = _timelineClips
            .Select(clip => Path.GetFullPath(clip.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subtitles = (droppedSubtitles ?? [])
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var usedSubtitles = _clipSubtitleSources.Values
            .Select(source => Path.GetFullPath(source.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The primary clip owns EditorProject.Subtitle; its SRT must never be
        // bound a second time to an appended clip.
        if (!string.IsNullOrWhiteSpace(_subtitleSource?.Path))
            usedSubtitles.Add(Path.GetFullPath(_subtitleSource.Path));
        var clips = new List<EditorTimelineClip>(_timelineClips);
        var added = new List<EditorTimelineClip>();
        var rejected = 0;
        foreach (var requestedPath in requested)
        {
            string path;
            try { path = EditorSourceSelection.NormalizeCandidatePath(requestedPath); }
            catch (Exception) { rejected++; continue; }
            if (!known.Add(path)) continue;
            try
            {
                var media = await _application.Media.ProbeAsync(path, CancellationToken.None);
                if (media.Duration <= 0) { rejected++; continue; }
                var clip = new EditorTimelineClip(
                    "clip-" + Guid.NewGuid().ToString("N")[..8],
                    path,
                    0,
                    media.Duration,
                    Path.GetFileName(path));
                var subtitlePath = subtitles.FirstOrDefault(
                        candidate => !usedSubtitles.Contains(Path.GetFullPath(candidate))
                            && EditorSidecarSubtitles.Matches(path, candidate))
                    ?? EditorSidecarSubtitles.Find(
                        path,
                        allowSingleFolderFallback: requested.Count == 1 && subtitles.Count == 0);
                if (subtitlePath is not null && usedSubtitles.Add(Path.GetFullPath(subtitlePath)))
                {
                    try { await AttachClipSubtitleAsync(clip, subtitlePath); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        StartupDiagnostics.WriteException("editor-auto-clip-srt", error);
                    }
                }
                clips.Add(clip);
                added.Add(clip);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                StartupDiagnostics.WriteException("editor-timeline-add-clip", error);
                rejected++;
            }
        }
        if (added.Count == 0)
        {
            StatusText.Text = rejected > 0
                ? "Không thêm được video nào trong các file đã chọn."
                : "Các video đã chọn đã có trên dải thời gian.";
            return null;
        }
        var attached = added.Count(clip => HasClipSubtitle(clip.Id));
        var missing = added.Where(clip => !HasClipSubtitle(clip.Id)).Select(clip => clip.Label).ToArray();
        var summary = $"Đã thêm {added.Count} video vào cuối dải thời gian";
        if (attached > 0) summary += $" · tự gắn {attached} SRT";
        if (missing.Length > 0)
            summary += $" · {missing.Length} video chưa có SRT ({string.Join(", ", missing.Take(3))}{(missing.Length > 3 ? "…" : "")})";
        if (rejected > 0) summary += $" · bỏ qua {rejected} file lỗi";
        StatusText.Text = summary + ".";
        return clips;
    }

    /// <summary>Shared import path for a drop or a multi-video new project.</summary>
    internal async Task AppendVideosAsync(
        IReadOnlyList<string> requested, IReadOnlyList<string>? droppedSubtitles = null)
    {
        var clips = await ImportVideosAsync(requested, droppedSubtitles);
        if (clips is null) return;
        CommitTimelineClips(clips);
        await SaveProjectNowAsync();
        RefreshTimelinePreview();
        NotifyEditorCompositeChanged();
    }

    /// <summary>
    /// Drag and drop of SRT files: bind each one to the clip whose video name it
    /// matches (the same rule as the automatic import). One leftover file goes to
    /// the selected clip, so "drop the subtitle on the clip" also works.
    /// </summary>
    internal async Task AttachDroppedSubtitlesAsync(IReadOnlyList<string> dropped)
    {
        if (_project is null) return;
        var remaining = dropped
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Every SRT already attached somewhere in the project. Dropping the same
        // file again must never reload or rebind it to another clip.
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_subtitleSource is not null) bound.Add(Path.GetFullPath(_subtitleSource.Path));
        foreach (var source in _clipSubtitleSources.Values) bound.Add(Path.GetFullPath(source.Path));
        var attached = 0;
        var alreadyBound = 0;
        var failed = 0;
        foreach (var clip in _timelineClips)
        {
            if (remaining.Count == 0) break;
            var primary = IsPrimaryTimelineClip(clip);
            var match = remaining.FirstOrDefault(candidate =>
                EditorSidecarSubtitles.Matches(clip.SourcePath, candidate));
            if (match is null) continue;
            remaining.Remove(match);
            var fullPath = Path.GetFullPath(match);
            if (bound.Contains(fullPath))
            {
                alreadyBound++;
                continue;
            }
            try
            {
                if (primary) await ImportSubtitlePathAsync(match);
                else await AttachClipSubtitleAsync(clip, match);
                bound.Add(fullPath);
                attached++;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                failed++;
                StartupDiagnostics.WriteException("editor-drop-srt", error);
            }
        }
        if (remaining.Count == 1)
        {
            var selected = SelectedTimelineClip();
            var primary = selected is not null && IsPrimaryTimelineClip(selected);
            var fullPath = Path.GetFullPath(remaining[0]);
            var alreadyOnSelected = selected is not null && (primary
                ? _subtitleSource is not null
                : HasClipSubtitle(selected.Id));
            if (bound.Contains(fullPath))
            {
                alreadyBound++;
                remaining.Clear();
            }
            else if (selected is not null && !alreadyOnSelected)
            {
                try
                {
                    if (primary) await ImportSubtitlePathAsync(remaining[0]);
                    else await AttachClipSubtitleAsync(selected, remaining[0]);
                    bound.Add(fullPath);
                    attached++;
                    remaining.Clear();
                }
                catch (Exception error)
                {
                    failed++;
                    StartupDiagnostics.WriteException("editor-drop-srt-selected", error);
                }
            }
        }
        await SaveProjectNowAsync();
        QueueTimelineRender();
        if (!_playback.IsPreviewMode) QueuePreviewRefresh();
        NotifyEditorCompositeChanged();
        StatusText.Text = attached > 0
            ? $"Đã gắn {attached} SRT vào các đoạn khớp tên."
            : failed > 0
                ? "Có SRT không đọc được hoặc không khớp đoạn nào."
                : alreadyBound > 0
                    ? "SRT này đã gắn sẵn cho đoạn tương ứng."
                    : "Không khớp được SRT với đoạn nào trên dải thời gian; hãy chọn đoạn rồi bấm “Gắn SRT”.";
    }

    /// <summary>
    /// First open of a project: attach a same-name/same-episode Vietnamese SRT
    /// beside the video, like an editor reading a sidecar subtitle.
    /// </summary>
    private async Task<bool> TryAttachSidecarSubtitleAsync(string videoPath)
    {
        if (_project is null || _project.Subtitle is not null) return false;
        var sidecar = EditorSidecarSubtitles.Find(videoPath);
        if (sidecar is null) return false;
        try
        {
            var candidate = await _application.LoadEditorSubtitleAsync(sidecar, CancellationToken.None);
            candidate = EditorSubtitleDocument.UseVietnameseSrt(candidate);
            _subtitleSource = candidate;
            _subtitlePlacement = EditorSubtitlePlacement.Default;
            AttachSubtitleToProject(string.Empty);
            SrtPathText.Text = candidate.Path;
            UpdateSubtitleSummary();
            SubtitleStatusText.Text =
                $"Đã tự gắn SRT cùng tên “{Path.GetFileName(sidecar)}” · {candidate.Cues.Count} câu.";
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            StartupDiagnostics.WriteException("editor-auto-primary-srt", error);
            return false;
        }
    }

    /// <summary>
    /// Restores every per-clip SRT/voice after a project/timeline was loaded.
    /// Stale SRT files are skipped exactly like the primary subtitle, so a
    /// missing external file never breaks the rest of the project.
    /// </summary>
    internal async Task RestoreClipSubtitlesAsync()
    {
        _clipSubtitleSources.Clear();
        _clipTts.Clear();
        _clipTtsQueue.Clear();
        if (_project?.ClipSubtitles is not { Count: > 0 } saved) return;
        var clipIds = _timelineClips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (clipId, savedSubtitle) in saved)
        {
            if (!clipIds.Contains(clipId)) continue;
            try
            {
                var current = await _application.LoadEditorSubtitleAsync(savedSubtitle.SourcePath, CancellationToken.None);
                if (!string.Equals(current.Sha256, savedSubtitle.SourceSha256, StringComparison.Ordinal))
                {
                    SubtitleStatusText.Text =
                        "Một SRT theo đoạn đã thay đổi ngoài ứng dụng; hãy gắn lại SRT cho đoạn đó để dùng đúng nội dung mới.";
                    continue;
                }
                _clipSubtitleSources[clipId] = MergeRestoredClipSubtitle(current, savedSubtitle);
            }
            catch (Exception error)
            {
                SubtitleStatusText.Text = "Không khôi phục được SRT của một đoạn: " + error.Message;
            }
        }
        if (_project?.ClipTts is not { Count: > 0 } savedTts) return;
        foreach (var (clipId, tts) in savedTts)
        {
            if (!_clipSubtitleSources.ContainsKey(clipId) || tts.Status != "complete"
                || !File.Exists(tts.VoiceTrack.Path))
                continue;
            _clipTts[clipId] = tts;
        }
    }

    private static EditorSubtitleSource MergeRestoredClipSubtitle(
        EditorSubtitleSource current,
        EditorSubtitleProject saved)
    {
        var translated = saved.Cues.ToDictionary(cue => cue.Id, cue => cue.VietnameseText, StringComparer.Ordinal);
        var savedTimings = saved.Cues.ToDictionary(cue => cue.Id, cue => cue, StringComparer.Ordinal);
        return current with
        {
            Cues = current.Cues.Select(cue =>
            {
                var vietnamese = translated.TryGetValue(cue.Id, out var value) ? value : string.Empty;
                if (!savedTimings.TryGetValue(cue.Id, out var savedCue)) return cue with { VietnameseText = vietnamese };
                if (Math.Abs(savedCue.Start - cue.Start) < .0005 && Math.Abs(savedCue.End - cue.End) < .0005)
                    return cue with { VietnameseText = vietnamese };
                return cue with
                {
                    VietnameseText = vietnamese,
                    Start = savedCue.Start,
                    End = savedCue.End,
                    Timing = savedCue.Timing,
                };
            }).ToArray(),
        };
    }

    /// <summary>Copies SRT/voice binding when a clip is split; both halves keep the same source SRT.</summary>
    internal void InheritClipAttachments(string sourceClipId, string targetClipId)
    {
        if (string.Equals(sourceClipId, targetClipId, StringComparison.Ordinal)) return;
        if (_clipSubtitleSources.TryGetValue(sourceClipId, out var subtitle))
            _clipSubtitleSources[targetClipId] = subtitle;
        if (_clipTts.TryGetValue(sourceClipId, out var tts))
            _clipTts[targetClipId] = tts;
    }

    /// <summary>
    /// Applies a finished clip voice job to that clip only. The primary voice
    /// remains owned by the existing project Tts pipeline.
    /// </summary>
    private async Task ApplyClipTtsResultAsync(string clipId, EditorTtsResult result)
    {
        if (_project is null || !_clipSubtitleSources.ContainsKey(clipId)) return;
        var track = result.VoiceTrack with { Gain = _voiceGain };
        _clipTts[clipId] = new EditorTtsProject(
            "complete",
            result.Engine,
            result.EngineVersion,
            result.Voice,
            result.Voice,
            result.ManifestPath,
            result.ManifestSha256,
            track,
            result.Cues.Count,
            result.ReviewCount,
            result.CueWindows,
            _voiceGain);
        var clip = _timelineClips.FirstOrDefault(item => item.Id == clipId);
        VoiceStatusText.Text = result.ShortenedCues > 0
            ? $"Voice đoạn “{clip?.Label}” hoàn tất · {result.Cues.Count} câu · ⚠ {result.ShortenedCues} câu bị rút gọn lời."
            : result.ReviewCount == 0
                ? $"Voice đoạn “{clip?.Label}” hoàn tất · {result.Cues.Count} câu đọc đủ chữ."
                : $"Voice đoạn “{clip?.Label}” đã canh thời lượng · {result.Cues.Count} câu · {result.ReviewCount} câu cần nghe lại.";
        await SaveProjectNowAsync();
        QueuePreviewRefresh();
    }

    /// <summary>
    /// Voices every non-primary clip that has its own SRT, one clip job at a
    /// time. SRT-only jobs need no Whisper analysis, so adding a video costs
    /// exactly one model pass for its own subtitles.
    /// </summary>
    private async Task RunClipTtsQueueAsync(string voice)
    {
        _clipTtsQueue.Clear();
        if (_project is null || _ttsJobId is not null) return;
        foreach (var clip in _timelineClips)
        {
            if (IsPrimaryTimelineClip(clip)) continue;
            if (!_clipSubtitleSources.TryGetValue(clip.Id, out var subtitle) || subtitle.Cues.Count == 0
                || subtitle.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.VietnameseText)))
                continue;
            _clipTtsQueue.Enqueue(clip.Id);
        }
        _clipTtsTotal = _clipTtsQueue.Count;
        if (_clipTtsTotal == 0) return;
        var sourceRevision = _editorSourceRevision;
        var done = 0;
        while (_clipTtsQueue.Count > 0)
        {
            var clipId = _clipTtsQueue.Dequeue();
            if (_editorSourceRevision != sourceRevision || _ttsPausedByUser) break;
            var clip = _timelineClips.FirstOrDefault(item => item.Id == clipId);
            if (clip is null || !_clipSubtitleSources.TryGetValue(clipId, out var subtitle)) continue;
            if (!ClipSubtitleFingerprintMatches(clipId))
            {
                VoiceStatusText.Text = $"SRT của đoạn “{clip.Label}” đã thay đổi; hãy gắn lại SRT cho đoạn đó.";
                break;
            }
            var media = await TimelineClipMediaAsync(clip);
            _ttsTargetClipId = clipId;
            _ttsLastJobApplied = false;
            _ttsJobId = _application.StartEditorTts(new EditorTtsRequest(
                _project.Id,
                clip.SourcePath,
                media.Duration,
                subtitle,
                string.Empty,
                new string('0', 64),
                voice,
                SrtOnly: true));
            VoiceProgress.Value = 0;
            VoiceStatusText.Text = $"Đang tạo voice đoạn {done + 1}/{_clipTtsTotal} “{clip.Label}” từ SRT riêng (không cần Whisper)...";
            RefreshEditorActions();
            await PollTtsJobAsync();
            if (!_ttsLastJobApplied) break;
            done++;
        }
        _ttsTargetClipId = null;
        if (_editorSourceRevision == sourceRevision && _clipTts.Count > 0 && !_ttsPausedByUser)
            VoiceStatusText.Text =
                $"Voice Việt đã sẵn sàng cho {_clipTts.Count} đoạn có SRT riêng · Preview/Export đọc đủ mọi đoạn.";
        RefreshEditorActions();
    }
}

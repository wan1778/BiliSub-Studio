using BiliSubStudio.Core.Editor;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

internal sealed record VoiceCuePreviewItem(
    string CueId,
    string DisplayText,
    string TimecodeText,
    string DurationText,
    double SourceStart,
    double Duration);

public sealed partial class EditorPage
{
    private MediaPlayer? _voiceCuePreviewPlayer;
    private DispatcherQueueTimer? _voiceCuePreviewStopTimer;
    private VoiceCuePreviewItem? _playingVoiceCue;
    private string? _voiceCuePreviewKey;
    private int _voiceCuePreviewRevision;

    private void RefreshVoiceCuePreview()
    {
        var track = _voiceTrack;
        var subtitle = _subtitleSource;
        var available = track is not null
            && subtitle is not null
            && File.Exists(track.Path)
            && track.Duration > 0;
        var key = available
            ? string.Join('|', Path.GetFullPath(track!.Path), track.Start.ToString("R"), track.Duration.ToString("R"),
                _project?.Tts?.ManifestSha256 ?? string.Empty, _project?.Speech?.AnalysisSha256 ?? string.Empty,
                subtitle!.Sha256, subtitle.Cues.Count, _cueSpeechTiming.Count, _voiceCueWindows.Count)
            : string.Empty;

        VoiceCuePreviewList.IsEnabled = available && !EditorBusy && !_playback.IsPreviewMode;
        if (string.Equals(_voiceCuePreviewKey, key, StringComparison.Ordinal)) return;

        StopVoiceCuePreview();
        _voiceCuePreviewKey = key;
        if (!available)
        {
            VoiceCuePreviewList.ItemsSource = null;
            VoiceCuePreviewList.Visibility = Visibility.Collapsed;
            VoiceCuePreviewCountText.Text = "0 câu";
            VoiceCuePreviewStatusText.Text = "Tạo voice xong để nghe từng câu tại đây.";
            return;
        }

        var timings = new Dictionary<string, EditorCueSpeechTiming>(StringComparer.Ordinal);
        foreach (var timing in _cueSpeechTiming)
            if (!timings.ContainsKey(timing.CueId)) timings.Add(timing.CueId, timing);

        var rows = new List<VoiceCuePreviewItem>(subtitle!.Cues.Count);
        foreach (var cue in subtitle.Cues)
        {
            VoiceCuePreviewItem? row;
            if (_voiceCueWindows.TryGetValue(cue.Id, out var actualWindow))
                row = CreateVoiceCuePreviewItem(cue, actualWindow, track!);
            else if (timings.TryGetValue(cue.Id, out var timing))
                row = CreateVoiceCuePreviewItem(cue, timing, track!);
            else continue;
            if (row is not null) rows.Add(row);
        }

        VoiceCuePreviewList.ItemsSource = rows;
        VoiceCuePreviewList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoiceCuePreviewCountText.Text = $"{rows.Count} câu";
        VoiceCuePreviewStatusText.Text = rows.Count == 0
            ? "Voice đã có nhưng chưa tìm thấy mốc thoại Whisper hợp lệ để nghe riêng từng câu."
            : "Bấm ▶ để nghe đúng đoạn của từng câu trong master voice đã tạo.";
    }

    private static VoiceCuePreviewItem? CreateVoiceCuePreviewItem(
        EditorSubtitleCue cue,
        EditorCueSpeechTiming timing,
        EditorVoiceTrack track)
    {
        if (!string.Equals(timing.CueId, cue.Id, StringComparison.Ordinal)
            || timing.CueStart != cue.Start || timing.CueEnd != cue.End)
            return null;

        // Keep this envelope identical to LocalTtsService.BuildWholeCue: the preview
        // seeks the generated master, including the explicit full-SRT fallback.
        var fallback = timing.Words.Count == 0;
        var voiceStart = fallback ? cue.Start : Math.Max(cue.Start, timing.Words.Min(word => word.Start));
        var voiceEnd = fallback ? cue.End : Math.Min(cue.End, timing.Words.Max(word => word.End));
        return CreateVoiceCuePreviewItem(cue,
            new EditorTtsCueWindow(cue.Id, voiceStart, voiceEnd, fallback ? "srt-fallback" : "whisper", "review"),
            track);
    }

    private static VoiceCuePreviewItem? CreateVoiceCuePreviewItem(
        EditorSubtitleCue cue,
        EditorTtsCueWindow window,
        EditorVoiceTrack track)
    {
        if (!string.Equals(window.Id, cue.Id, StringComparison.Ordinal)) return null;
        var voiceStart = window.VoiceStart;
        var voiceEnd = window.VoiceEnd;
        if (!double.IsFinite(voiceStart) || !double.IsFinite(voiceEnd) || voiceEnd <= voiceStart
            || Math.Round(voiceEnd * 22050) <= Math.Round(voiceStart * 22050))
            return null;

        var sourceStart = voiceStart - track.Start;
        var duration = voiceEnd - voiceStart;
        if (!double.IsFinite(sourceStart) || sourceStart < 0 || sourceStart >= track.Duration)
            return null;
        duration = Math.Min(duration, track.Duration - sourceStart);
        if (duration <= 0) return null;

        var text = string.IsNullOrWhiteSpace(cue.VietnameseText) ? cue.SourceText : cue.VietnameseText;
        return new VoiceCuePreviewItem(
            cue.Id,
            $"{cue.Number}. {text.Trim()}",
            $"{FormatVoiceCueTime(voiceStart)} → {FormatVoiceCueTime(voiceStart + duration)}",
            window.TimingSource switch
            {
                "srt-fallback" => $"Đọc {duration:0.###} giây · theo timecode SRT",
                "sentence-group" => $"Đọc {duration:0.###} giây · dùng nhịp chung của câu",
                _ => $"Đọc {duration:0.###} giây",
            },
            sourceStart,
            duration);
    }

    private async void VoiceCuePreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VoiceCuePreviewItem item } || _voiceTrack is not { } track) return;
        if (ReferenceEquals(_playingVoiceCue, item))
        {
            StopVoiceCuePreview();
            VoiceCuePreviewStatusText.Text = $"Đã dừng câu {item.DisplayText}.";
            return;
        }
        if (EditorBusy || _playback.IsPreviewMode)
        {
            VoiceCuePreviewStatusText.Text = "Hãy hoàn tất tác vụ hoặc thoát Xem bản chỉnh trước khi nghe riêng từng câu.";
            return;
        }

        StopVoiceCuePreview();
        var revision = _voiceCuePreviewRevision;
        var expectedPath = track.Path;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(expectedPath);
            if (revision != _voiceCuePreviewRevision || !IsLoaded
                || _voiceTrack is not { } currentTrack
                || !string.Equals(currentTrack.Path, expectedPath, StringComparison.OrdinalIgnoreCase))
                return;

            var player = new MediaPlayer { AutoPlay = false };
            player.MediaOpened += VoiceCuePreview_MediaOpened;
            player.MediaEnded += VoiceCuePreview_MediaEnded;
            player.MediaFailed += VoiceCuePreview_MediaFailed;
            _voiceCuePreviewPlayer = player;
            _playingVoiceCue = item;
            player.Source = MediaSource.CreateFromStorageFile(file);
            VoiceCuePreviewStatusText.Text = $"Đang chuẩn bị {item.DisplayText} · {item.TimecodeText} · {item.DurationText}.";
        }
        catch (Exception error)
        {
            if (revision != _voiceCuePreviewRevision) return;
            StopVoiceCuePreview();
            VoiceCuePreviewStatusText.Text = "Không phát được câu voice: " + error.Message;
        }
    }

    private void VoiceCuePreview_MediaOpened(MediaPlayer sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_voiceCuePreviewPlayer, sender) || _playingVoiceCue is not { } item) return;
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(item.SourceStart);
            sender.Play();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(item.Duration);
            timer.IsRepeating = false;
            timer.Tick += VoiceCuePreviewStopTimer_Tick;
            _voiceCuePreviewStopTimer = timer;
            timer.Start();
            VoiceCuePreviewStatusText.Text = $"Đang nghe {item.DisplayText} · {item.TimecodeText} · {item.DurationText}.";
        });

    private void VoiceCuePreviewStopTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(_voiceCuePreviewStopTimer, sender)) return;
        var finished = _playingVoiceCue;
        StopVoiceCuePreview();
        VoiceCuePreviewStatusText.Text = finished is null
            ? "Đã nghe xong câu voice."
            : $"Đã nghe xong {finished.DisplayText} · {finished.DurationText}.";
    }

    private void VoiceCuePreview_MediaEnded(MediaPlayer sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_voiceCuePreviewPlayer, sender)) return;
            var finished = _playingVoiceCue;
            StopVoiceCuePreview();
            VoiceCuePreviewStatusText.Text = finished is null
                ? "Đã nghe xong câu voice."
                : $"Đã nghe xong {finished.DisplayText} · {finished.DurationText}.";
        });

    private void VoiceCuePreview_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var detail = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_voiceCuePreviewPlayer, sender)) return;
            StopVoiceCuePreview();
            VoiceCuePreviewStatusText.Text = "Không phát được câu voice: " + detail;
        });
    }

    private void StopVoiceCuePreview()
    {
        ++_voiceCuePreviewRevision;
        var player = _voiceCuePreviewPlayer;
        var timer = _voiceCuePreviewStopTimer;
        _voiceCuePreviewPlayer = null;
        _voiceCuePreviewStopTimer = null;
        _playingVoiceCue = null;
        if (timer is not null)
        {
            timer.Stop();
            timer.Tick -= VoiceCuePreviewStopTimer_Tick;
        }
        if (player is null) return;
        player.MediaOpened -= VoiceCuePreview_MediaOpened;
        player.MediaEnded -= VoiceCuePreview_MediaEnded;
        player.MediaFailed -= VoiceCuePreview_MediaFailed;
        player.Pause();
        player.Source = null;
        player.Dispose();
    }

    private void CleanupVoiceCuePreview()
    {
        StopVoiceCuePreview();
        _voiceCuePreviewKey = null;
    }

    private static string FormatVoiceCueTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var hours = (int)Math.Floor(value.TotalHours);
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }
}

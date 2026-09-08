using BiliSubStudio.Core.Timeline;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

public sealed partial class TimelineEditorPage
{
    private readonly SemaphoreSlim _timelinePreviewStopGate = new(1, 1);
    private TimelinePreviewController? _timelinePreviewController;
    private TimelineProcessedPreviewService? _timelinePreviewService;
    private TimelineProcessedPreviewArtifact? _timelinePreviewArtifact;
    private MediaPlayer? _timelinePreviewMediaPlayer;
    private CancellationTokenSource? _timelinePreviewUiCancellation;
    private Task? _timelinePreviewStartTask;
    private long _timelinePreviewUiRevision;
    private bool _timelinePreviewBusy;
    private bool _timelinePreviewStopping;
    private bool _timelinePageLoaded;
    private bool _timelineClosing;

    private bool TimelinePreviewLocked =>
        _timelinePreviewBusy
        || _timelinePreviewStopping
        || _timelinePreviewController is not null
        || _timelinePreviewMediaPlayer is not null;

    private void InitializeTimelinePlayback()
    {
        Loaded += TimelineEditorPage_Loaded;
        Unloaded += TimelineEditorPage_Unloaded;
        RefreshTimelinePreviewControls();
    }

    private void TimelineEditorPage_Loaded(object sender, RoutedEventArgs e)
    {
        _timelinePageLoaded = true;
        RefreshTimelinePreviewControls();
    }

    private async void TimelineEditorPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _timelinePageLoaded = false;
        try { await StopTimelinePreviewAsync(updateStatus: false); }
        catch { }
    }

    private async void TimelinePreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_timelineClosing || _timelinePreviewStopping)
            return;
        if (_timelineAutosaveBusy)
        {
            TimelinePreviewStatusText.Text = "Đang tự động lưu; preview sẽ sẵn sàng ngay sau khi lưu xong.";
            return;
        }
        if (_timelinePreviewMediaPlayer is { } retained)
        {
            retained.Play();
            TimelinePreviewStatusText.Text = "Đang phát lại processed preview hiện tại.";
            if (_timelinePageLoaded)
                RefreshTimelinePreviewControls();
            return;
        }
        if (_timelinePreviewBusy)
            return;
        if (!_editor.HasOpenProject || _editor.Clips.Count == 0)
        {
            TimelinePreviewStatusText.Text = "Hãy mở project có ít nhất một clip trước khi preview.";
            return;
        }

        var uiRevision = checked(++_timelinePreviewUiRevision);
        var editRevision = _editor.Revision;
        var project = _editor.Project;
        var cancellation = new CancellationTokenSource();
        _timelinePreviewUiCancellation = cancellation;
        _timelinePreviewBusy = true;
        TimelinePreviewStatusText.Text = "Đang render processed preview bằng cùng pipeline với Export...";
        RefreshTimelinePreviewControls();
        var startTask = GenerateAndPlayTimelinePreviewAsync(
            project, editRevision, uiRevision, cancellation.Token);
        _timelinePreviewStartTask = startTask;
        try
        {
            await startTask;
        }
        catch (OperationCanceledException)
        {
            if (uiRevision == _timelinePreviewUiRevision && _timelinePageLoaded)
                TimelinePreviewStatusText.Text = "Đã hủy processed preview.";
        }
        catch (Exception error)
        {
            if (uiRevision == _timelinePreviewUiRevision && _timelinePageLoaded)
                TimelinePreviewStatusText.Text = "Không tạo được processed preview: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_timelinePreviewStartTask, startTask))
                _timelinePreviewStartTask = null;
            if (ReferenceEquals(_timelinePreviewUiCancellation, cancellation))
            {
                _timelinePreviewUiCancellation = null;
                cancellation.Dispose();
            }
            if (uiRevision == _timelinePreviewUiRevision)
                _timelinePreviewBusy = false;
            if (_timelinePageLoaded)
                RefreshTimelinePreviewControls();
        }
    }

    private async Task GenerateAndPlayTimelinePreviewAsync(
        TimelineProjectV2 project,
        long editRevision,
        long uiRevision,
        CancellationToken cancellationToken)
    {
        TimelinePreviewController? controller = null;
        var activated = false;
        try
        {
            var plan = TimelineCompiler.Compile(project);
            if (plan.TotalDurationUs <= 0)
                throw new InvalidDataException("Timeline preview cần ít nhất một clip có duration dương.");
            var ffmpeg = await _application.Tools.EnsureFfmpegAsync(cancellationToken);
            EnsureTimelinePreviewRequestCurrent(project.Id, editRevision, uiRevision, cancellationToken);
            var ffprobe = await _application.Tools.EnsureFfprobeAsync(cancellationToken);
            EnsureTimelinePreviewRequestCurrent(project.Id, editRevision, uiRevision, cancellationToken);

            var renderer = new TimelineFfmpegProcessedPreviewRenderer(
                _application.Paths, ffmpeg, ffprobe, project);
            var service = new TimelineProcessedPreviewService(_application.Paths, renderer);
            // Reap dead-owner and stale legacy proxies before taking ownership;
            // CleanupCacheAsync preserves artifacts owned by a live process.
            await service.CleanupCacheAsync(cancellationToken);
            EnsureTimelinePreviewRequestCurrent(project.Id, editRevision, uiRevision, cancellationToken);
            controller = new TimelinePreviewController(service);
            _timelinePreviewService = service;
            _timelinePreviewController = controller;

            var artifact = await controller.RequestAsync(
                plan, 0, plan.TotalDurationUs, cancellationToken);
            EnsureTimelinePreviewRequestCurrent(project.Id, editRevision, uiRevision, cancellationToken);
            if (controller.CurrentArtifact != artifact)
                throw new OperationCanceledException(cancellationToken);
            var file = await StorageFile.GetFileFromPathAsync(artifact.Path);
            EnsureTimelinePreviewRequestCurrent(project.Id, editRevision, uiRevision, cancellationToken);

            var player = new MediaPlayer { AutoPlay = false };
            player.PlaybackSession.PositionChanged += TimelinePreviewPlayer_PositionChanged;
            player.MediaEnded += TimelinePreviewPlayer_MediaEnded;
            player.MediaFailed += TimelinePreviewPlayer_MediaFailed;
            _timelinePreviewArtifact = artifact;
            _timelinePreviewMediaPlayer = player;
            TimelinePreviewPlayer.SetMediaPlayer(player);
            player.Source = MediaSource.CreateFromStorageFile(file);
            TimelinePreviewPlayer.Visibility = Visibility.Visible;
            TimelinePreviewPlaceholder.Visibility = Visibility.Collapsed;
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
            activated = true;
            TimelinePreviewStatusText.Text =
                $"Đang phát {_editor.Project.Name}; edit bị khóa đến khi Dừng.";
        }
        finally
        {
            if (!activated && controller is not null)
            {
                DetachTimelinePreviewPlayer();
                if (!_timelinePreviewStopping)
                {
                    await controller.CancelAsync();
                    await controller.DisposeAsync();
                    if (ReferenceEquals(_timelinePreviewController, controller))
                    {
                        _timelinePreviewController = null;
                        _timelinePreviewService = null;
                        _timelinePreviewArtifact = null;
                    }
                }
            }
        }
    }

    private void TimelinePreviewPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timelinePreviewMediaPlayer is null)
            return;
        _timelinePreviewMediaPlayer.Pause();
        TimelinePreviewStatusText.Text = "Đã tạm dừng; bấm Phát tiếp để giữ nguyên vị trí.";
        RefreshTimelinePreviewControls();
    }

    private async void TimelinePreviewStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopTimelinePreviewAsync(updateStatus: true);
        }
        catch (Exception error)
        {
            TimelinePreviewStatusText.Text = "Không dọn sạch được processed preview: " + error.Message;
        }
    }

    private async Task StopTimelinePreviewAsync(bool updateStatus)
    {
        await _timelinePreviewStopGate.WaitAsync();
        CancellationTokenSource? cancellation = null;
        try
        {
            _timelinePreviewStopping = true;
            if (_timelinePageLoaded)
                RefreshTimelinePreviewControls();
            checked { _timelinePreviewUiRevision++; }
            cancellation = _timelinePreviewUiCancellation;
            _timelinePreviewUiCancellation = null;
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }

            var startTask = _timelinePreviewStartTask;
            var controller = _timelinePreviewController;
            DetachTimelinePreviewPlayer();
            try
            {
                if (controller is not null)
                {
                    await controller.CancelAsync();
                    await controller.DisposeAsync();
                    if (ReferenceEquals(_timelinePreviewController, controller))
                    {
                        _timelinePreviewController = null;
                        _timelinePreviewService = null;
                        _timelinePreviewArtifact = null;
                    }
                }
            }
            finally
            {
                if (startTask is not null)
                {
                    try { await startTask; }
                    catch (OperationCanceledException) { }
                    catch { }
                }
            }
            if (updateStatus && _timelinePageLoaded)
                TimelinePreviewStatusText.Text = "Đã dừng preview, tháo player và xóa proxy; có thể tiếp tục edit.";
        }
        finally
        {
            cancellation?.Dispose();
            _timelinePreviewBusy = false;
            _timelinePreviewStopping = false;
            if (_timelinePageLoaded)
                RefreshTimelinePreviewControls();
            _timelinePreviewStopGate.Release();
        }
    }

    private void DetachTimelinePreviewPlayer()
    {
        var player = _timelinePreviewMediaPlayer;
        _timelinePreviewMediaPlayer = null;
        if (player is not null)
        {
            player.PlaybackSession.PositionChanged -= TimelinePreviewPlayer_PositionChanged;
            player.MediaEnded -= TimelinePreviewPlayer_MediaEnded;
            player.MediaFailed -= TimelinePreviewPlayer_MediaFailed;
            player.Pause();
            player.Source = null;
            TimelinePreviewPlayer.SetMediaPlayer(null);
            player.Dispose();
        }
        TimelinePreviewPlayer.Visibility = Visibility.Collapsed;
        TimelinePreviewPlaceholder.Visibility = Visibility.Visible;
        TimelinePreviewClockText.Text = "00:00 / 00:00";
        _timelinePlayheadUs = -1;
        UpdateTimelinePlayhead(fullRender: false);
    }

    private void TimelinePreviewPlayer_PositionChanged(MediaPlaybackSession sender, object args)
    {
        var uiRevision = _timelinePreviewUiRevision;
        DispatcherQueue.TryEnqueue(() =>
        {
            var player = _timelinePreviewMediaPlayer;
            if (uiRevision != _timelinePreviewUiRevision
                || player is null
                || !ReferenceEquals(sender, player.PlaybackSession))
                return;
            try
            {
                var duration = _timelinePreviewArtifact is { } artifact
                    ? TimeSpan.FromSeconds(TimelineTime.ToSeconds(artifact.DurationUs))
                    : sender.NaturalDuration;
                TimelinePreviewClockText.Text =
                    $"{FormatTimelineClock(sender.Position)} / {FormatTimelineClock(duration)}";
                // G32: move the playhead without re-rendering the canvas.
                _timelinePlayheadUs = Math.Max(0, sender.Position.Ticks / 10);
                UpdateTimelinePlayhead(fullRender: false);
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void TimelinePreviewPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _timelinePreviewMediaPlayer))
                return;
            try { await StopTimelinePreviewAsync(updateStatus: true); }
            catch (Exception error)
            {
                if (_timelinePageLoaded)
                    TimelinePreviewStatusText.Text = "Không dọn sạch được preview đã phát xong: " + error.Message;
            }
        });
    }

    private void TimelinePreviewPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _timelinePreviewMediaPlayer))
                return;
            try { await StopTimelinePreviewAsync(updateStatus: false); }
            catch (Exception cleanupError)
            {
                message += "; cleanup: " + cleanupError.Message;
            }
            if (_timelinePageLoaded)
                TimelinePreviewStatusText.Text = "Processed preview phát lỗi: " + message;
        });
    }

    private void EnsureTimelinePreviewRequestCurrent(
        string projectId,
        long editRevision,
        long uiRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_timelinePageLoaded
            || uiRevision != _timelinePreviewUiRevision
            || _timelineClosing
            || !_editor.HasOpenProject
            || !string.Equals(_editor.Project.Id, projectId, StringComparison.Ordinal)
            || _editor.Revision != editRevision)
            throw new OperationCanceledException(cancellationToken);
    }

    private void RefreshTimelinePreviewControls()
    {
        var hasProject = _editor.HasOpenProject && _editor.Clips.Count > 0;
        var locked = TimelinePreviewLocked;
        TimelinePreviewPlayButton.IsEnabled = _timelinePageLoaded && hasProject
            && !_timelinePreviewBusy && !_timelinePreviewStopping && !_timelineClosing
            && !_timelineAutosaveBusy;
        TimelinePreviewPlayButton.Content = _timelinePreviewMediaPlayer is null
            ? "Tạo và phát preview"
            : "Phát tiếp";
        TimelinePreviewPauseButton.IsEnabled = _timelinePreviewMediaPlayer is not null;
        TimelinePreviewStopButton.IsEnabled = locked;
        TimelinePreviewProgress.IsActive = _timelinePreviewBusy;
        TimelinePreviewProgress.Visibility = _timelinePreviewBusy ? Visibility.Visible : Visibility.Collapsed;
        TimelinePreviewPlaceholder.Visibility = _timelinePreviewMediaPlayer is null && !_timelinePreviewBusy
            ? Visibility.Visible
            : Visibility.Collapsed;

        var editable = hasProject && !locked && !_savingProject && !_openingProject && !_timelineClosing;
        ClipList.IsEnabled = editable;
        TimelineSurface.IsHitTestVisible = editable;
        TimelineSurface.Opacity = editable ? 1 : 0.62;
        TrimInBox.IsEnabled = editable;
        TrimOutBox.IsEnabled = editable;
        TrimClipButton.IsEnabled = editable;
        SplitAtBox.IsEnabled = editable;
        SplitClipButton.IsEnabled = editable;
        MoveUpButton.IsEnabled = editable;
        MoveDownButton.IsEnabled = editable;
        DeleteClipButton.IsEnabled = editable;
        UndoButton.IsEnabled = editable && _editor.UndoCount > 0;
        RedoButton.IsEnabled = editable && _editor.RedoCount > 0;
        SaveProjectButton.IsEnabled = hasProject && !_savingProject && _editor.IsDirty;
        SaveProjectButton.Content = hasProject && _editor.IsDirty ? "Lưu ●" : "Lưu";
        ExportProjectButton.IsEnabled = !locked && !_timelineClosing;
    }

    internal async Task FlushForAppCloseAsync(CancellationToken cancellationToken)
    {
        _timelineClosing = true;
        try
        {
            if (_openingProject || _savingProject)
                throw new InvalidOperationException(
                    "Timeline đang mở hoặc lưu project; hãy chờ hoàn tất rồi đóng lại.");
            await StopTimelinePreviewAsync(updateStatus: false).WaitAsync(cancellationToken);
            if (_editor.HasOpenProject && _editor.IsDirty)
                await _editor.SaveAsync(cancellationToken);
        }
        catch
        {
            _timelineClosing = false;
            if (_timelinePageLoaded)
                RefreshTimelinePreviewControls();
            throw;
        }
    }

    internal void AbortAppClose()
    {
        _timelineClosing = false;
        if (_timelinePageLoaded)
            RefreshTimelinePreviewControls();
    }

    private static string FormatTimelineClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero || value == TimeSpan.MaxValue)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}

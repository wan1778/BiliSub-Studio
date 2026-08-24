using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private async void PlayerPlayPause_Click(object sender, RoutedEventArgs e)
    {
        try { await _playback.ToggleAsync(); }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng tạo bản xem trước."; }
        catch (Exception error) { StatusText.Text = "Preview bản chỉnh: " + error.Message; }
    }

    private async void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        try { await _playback.ToggleFullscreenAsync(); }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng tạo bản xem trước."; }
        catch (Exception error) { StatusText.Text = "Toàn màn hình bản chỉnh: " + error.Message; }
    }

    private void PreviewMute_Toggled(object sender, RoutedEventArgs e) =>
        _playback.SetMuted(PreviewMuteToggle.IsOn);

    private void PreviewVolume_ValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        _playback.SetVolume(PreviewVolumeSlider.Value / 100);

    private sealed class EditorPlaybackController
    {
        private readonly EditorPage _page;
        private readonly EditorPreviewRequestCoordinator _previewRequests = new();
        private MediaPlayer? _player;
        private string? _previewPath;
        private double _sourceStart;
        private double _sourceDuration;
        private FullscreenSnapshot? _fullscreenSnapshot;
        private long? _fullWindowChangedToken;

        private readonly record struct FullscreenSnapshot(bool PreviewMode, bool Playing, bool Ended);

        internal EditorPlaybackController(EditorPage page) => _page = page;

        internal bool IsPreviewMode { get; private set; }
        internal bool HasEnded { get; private set; }
        internal bool IsRendering => _previewRequests.IsActive;
        internal bool IsReady => _player is not null;
        internal bool IsPlaying => IsPreviewMode &&
            _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        internal async Task PrepareAsync()
        {
            await _previewRequests.CancelAsync();
            ClearFullscreenTracking(unregisterCallback: true);
            DisposePlayer();

            var stalePreview = _previewPath;
            _previewPath = null;
            IsPreviewMode = false;
            HasEnded = false;
            _sourceStart = 0;
            _sourceDuration = 0;
            ApplyPresentation(processed: false);
            if (stalePreview is not null)
                await _page._application.DeleteEditorPreviewSegmentAsync(stalePreview);

            CreatePlayer();
        }

        internal async Task ToggleAsync()
        {
            if (HasEnded) await ReplayFromStartAsync();
            else if (IsPreviewMode && _player is not null)
            {
                if (IsPlaying) PauseAtCurrentFrame();
                else ResumeFromCurrentFrame();
            }
            else
            {
                await PlayFromStartAsync();
            }
            _page.SyncShellPlayerControls();
        }

        private void PauseAtCurrentFrame() => _player?.Pause();

        private void ResumeFromCurrentFrame() => _player?.Play();

        private Task PlayFromStartAsync() => LoadSegmentAsync(0, play: true);

        private Task ReplayFromStartAsync() => PlayFromStartAsync();

        internal async Task EnterFullscreenAsync()
        {
            if (_page.PreviewPlayer.IsFullWindow) return;
            var snapshot = new FullscreenSnapshot(IsPreviewMode, IsPlaying, HasEnded);
            EnsureFullscreenTracking();
            _fullscreenSnapshot = snapshot;
            try
            {
                await SetModeAsync(enabled: true, play: false);
                HasEnded = snapshot.Ended;
                _page.PreviewPlayer.IsFullWindow = true;
            }
            catch
            {
                _fullscreenSnapshot = null;
                throw;
            }
        }

        internal async Task ToggleFullscreenAsync()
        {
            if (!_page.PreviewPlayer.IsFullWindow)
            {
                await EnterFullscreenAsync();
                return;
            }

            var snapshot = _fullscreenSnapshot;
            _fullscreenSnapshot = null;
            _page.PreviewPlayer.IsFullWindow = false;
            if (snapshot is { } state) await RestoreFullscreenRoundtripAsync(state);
        }

        private void EnsureFullscreenTracking()
        {
            _fullWindowChangedToken ??= _page.PreviewPlayer.RegisterPropertyChangedCallback(
                MediaPlayerElement.IsFullWindowProperty, PreviewPlayerFullWindowChanged);
        }

        private void PreviewPlayerFullWindowChanged(DependencyObject sender, DependencyProperty property)
        {
            if (_page.PreviewPlayer.IsFullWindow || _fullscreenSnapshot is not { } snapshot) return;
            _fullscreenSnapshot = null;
            _ = RestoreFullscreenRoundtripAsync(snapshot);
        }

        private async Task RestoreFullscreenRoundtripAsync(FullscreenSnapshot snapshot)
        {
            try
            {
                if (!snapshot.PreviewMode)
                    await SetModeAsync(enabled: false, play: false);
                HasEnded = snapshot.Ended;
                if (snapshot.Ended)
                {
                    PauseAtCurrentFrame();
                    _page.StatusText.Text = "Đã xem hết bản chỉnh. Bấm Play để phát lại từ đầu.";
                }
                else if (snapshot.Playing) ResumeFromCurrentFrame();
                else PauseAtCurrentFrame();
                _page.SyncShellPlayerControls();
            }
            catch (OperationCanceledException)
            {
                _page.StatusText.Text = "Đã dừng khôi phục preview sau toàn màn hình.";
            }
            catch (Exception error)
            {
                _page.StatusText.Text = "Không khôi phục được preview sau toàn màn hình: " + error.Message;
            }
        }

        private void ClearFullscreenTracking(bool unregisterCallback)
        {
            _fullscreenSnapshot = null;
            _page.PreviewPlayer.IsFullWindow = false;
            if (!unregisterCallback || _fullWindowChangedToken is not { } token) return;
            _page.PreviewPlayer.UnregisterPropertyChangedCallback(MediaPlayerElement.IsFullWindowProperty, token);
            _fullWindowChangedToken = null;
        }

        internal void SetMuted(bool muted)
        {
            if (_player is not null) _player.IsMuted = muted;
        }

        internal void SetVolume(double volume)
        {
            if (_player is not null) _player.Volume = Math.Clamp(volume, 0, 1);
        }

        internal async Task SetModeAsync(bool enabled, bool play)
        {
            if (enabled)
            {
                if (IsPreviewMode)
                {
                    if (play) ResumeFromCurrentFrame();
                    return;
                }
                await LoadSegmentAsync(_page.Timeline.Value, play);
                return;
            }

            await _previewRequests.CancelAsync();
            if (!IsPreviewMode && !IsRendering) return;
            HasEnded = false;
            var sourcePosition = IsPreviewMode
                ? _sourceStart + Math.Clamp(_player?.PlaybackSession.Position.TotalSeconds ?? 0, 0, _sourceDuration)
                : _page.Timeline.Value;
            IsPreviewMode = false;
            if (_player is not null)
            {
                _player.Pause();
                _player.Source = null;
            }
            ClearFullscreenTracking(unregisterCallback: false);
            ApplyPresentation(processed: false);
            var previewPath = _previewPath;
            _previewPath = null;
            _sourceStart = 0;
            _sourceDuration = 0;
            _page._syncingTimeline = true;
            try { _page.Timeline.Value = Math.Clamp(sourcePosition, _page.Timeline.Minimum, _page.Timeline.Maximum); }
            finally { _page._syncingTimeline = false; }
            if (previewPath is not null)
                await _page._application.DeleteEditorPreviewSegmentAsync(previewPath);
            await _page.UpdateFrameAsync();
            _page.StatusText.Text = "Đã về khung chỉnh tại vị trí hiện tại.";
            _page.RefreshEditorActions();
        }

        internal async Task SeekAsync(double sourcePosition)
        {
            if (!IsPreviewMode || _page._media is null) return;
            try
            {
                if (IsPlaying) await SeekPlayingAsync(sourcePosition);
                else await SeekPausedAsync(sourcePosition);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _page.StatusText.Text = "Không seek được preview: " + error.Message; }
        }

        private async Task SeekPlayingAsync(double sourcePosition)
        {
            PauseAtCurrentFrame();
            await LoadSegmentAsync(sourcePosition, play: true);
        }

        private Task SeekPausedAsync(double sourcePosition) => LoadSegmentAsync(sourcePosition, play: false);

        internal Task DisposeForSourceChangeAsync() => ResetAsync();

        internal Task UnloadAsync() => ResetAsync();

        private async Task ResetAsync()
        {
            await _previewRequests.CancelAsync();
            ClearFullscreenTracking(unregisterCallback: true);
            DisposePlayer();
            IsPreviewMode = false;
            HasEnded = false;
            _sourceStart = 0;
            _sourceDuration = 0;
            ApplyPresentation(processed: false);
            var previewPath = _previewPath;
            _previewPath = null;
            if (previewPath is not null)
                await _page._application.DeleteEditorPreviewSegmentAsync(previewPath);
        }

        private async Task LoadSegmentAsync(double requestedStart, bool play)
        {
            try
            {
                await _previewRequests.RunLatestAsync(
                    cancellationToken => LoadSegmentCoreAsync(requestedStart, play, cancellationToken));
            }
            finally
            {
                _page.RefreshEditorActions();
                _page.SyncShellPlayerControls();
            }
        }

        private async Task LoadSegmentCoreAsync(
            double requestedStart,
            bool play,
            CancellationToken cancellationToken)
        {
            var player = _player ?? throw new InvalidOperationException("Chưa chọn video để xem bản chỉnh.");
            if (_page._path is null || _page._media is null)
                throw new InvalidOperationException("Chưa chọn video để xem bản chỉnh.");
            _page._previewCancellation?.Cancel();
            var previousPath = _previewPath;
            EditorPreviewSegment? segment = null;
            try
            {
                _page.RefreshEditorActions();
                _page.StatusText.Text = "Đang chuẩn bị bản xem trước tại vị trí hiện tại...";
                segment = await _page._application.CreateEditorPreviewSegmentAsync(
                    _page.CurrentEditRequest(_page.PreviewSubtitleBurn()), requestedStart, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(segment.Path);
                cancellationToken.ThrowIfCancellationRequested();
                var segmentPosition = PositionInSegment(segment, requestedStart);
                var sourcePosition = segment.SourceStart + segmentPosition;
                player.Pause();
                player.Source = MediaSource.CreateFromStorageFile(file);
                _previewPath = segment.Path;
                _sourceStart = segment.SourceStart;
                _sourceDuration = segment.Duration;
                HasEnded = false;
                IsPreviewMode = true;
                ApplyPresentation(processed: true);
                _page._syncingTimeline = true;
                try { _page.Timeline.Value = Math.Clamp(sourcePosition, _page.Timeline.Minimum, _page.Timeline.Maximum); }
                finally { _page._syncingTimeline = false; }
                player.PlaybackSession.Position = TimeSpan.FromSeconds(segmentPosition);
                if (play) player.Play();
                _page.StatusText.Text =
                    $"Đang xem bản chỉnh từ {FormatClock(sourcePosition)}. Preview sẽ tiếp tục tự động đến hết video.";
                segment = null;
                if (previousPath is not null && !string.Equals(previousPath, _previewPath, StringComparison.OrdinalIgnoreCase))
                    await _page._application.DeleteEditorPreviewSegmentAsync(previousPath);
            }
            catch
            {
                if (previousPath is null)
                {
                    player.Pause();
                    player.Source = null;
                    _previewPath = null;
                    IsPreviewMode = false;
                    ApplyPresentation(processed: false);
                }
                throw;
            }
            finally
            {
                if (segment is not null)
                    await _page._application.DeleteEditorPreviewSegmentAsync(segment.Path);
            }
        }

        private static double PositionInSegment(EditorPreviewSegment segment, double requestedStart)
        {
            var lastFrame = Math.Max(0, segment.Duration - .05);
            return Math.Clamp(requestedStart - segment.SourceStart, 0, lastFrame);
        }

        private void ApplyPresentation(bool processed)
        {
            _page.PreviewPlayer.Visibility = processed ? Visibility.Visible : Visibility.Collapsed;
            _page.PreviewImage.Visibility = processed ? Visibility.Collapsed : Visibility.Visible;
            _page.Overlay.Visibility = processed ? Visibility.Collapsed : Visibility.Visible;
            if (!processed) _page.RenderOverlays();
            _page.RenderImageOverlays();
            _page.SyncShellPlayerControls();
        }

        private void DisposePlayer()
        {
            if (_player is null) return;
            _player.PlaybackSession.PositionChanged -= PlayerPositionChanged;
            _player.MediaEnded -= PlayerMediaEnded;
            _player.MediaFailed -= PlayerMediaFailed;
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
            _player = null;
        }

        private void CreatePlayer()
        {
            var player = new MediaPlayer
            {
                AutoPlay = false,
                IsMuted = _page.PreviewMuteToggle.IsOn,
                Volume = Math.Clamp(_page.PreviewVolumeSlider.Value / 100, 0, 1),
            };
            player.PlaybackSession.PositionChanged += PlayerPositionChanged;
            player.MediaEnded += PlayerMediaEnded;
            player.MediaFailed += PlayerMediaFailed;
            _player = player;
            _page.PreviewPlayer.SetMediaPlayer(player);
        }

        private void PlayerPositionChanged(MediaPlaybackSession sender, object args)
        {
            if (!IsPreviewMode) return;
            var seconds = _sourceStart + Math.Clamp(sender.Position.TotalSeconds, 0, _sourceDuration);
            _page.DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsPreviewMode || _page._media is null) return;
                _page._syncingTimeline = true;
                try { _page.Timeline.Value = Math.Clamp(seconds, _page.Timeline.Minimum, _page.Timeline.Maximum); }
                finally { _page._syncingTimeline = false; }
                _page.UpdateClock();
                _page.RenderOverlays();
                _page.RenderTimelineRegions();
            });
        }

        private void PlayerMediaEnded(MediaPlayer sender, object args)
        {
            _page.DispatcherQueue.TryEnqueue(() => _ = ContinueAfterSegmentAsync());
        }

        private async Task ContinueAfterSegmentAsync()
        {
            try
            {
                if (!IsPreviewMode || _page._media is null) return;
                var nextStart = VideoEditorService.NextPreviewStart(
                    _sourceStart, _sourceDuration, _page._media.Duration);
                if (nextStart is null)
                {
                    await CompletePlaybackAsync();
                    return;
                }
                await LoadSegmentAsync(nextStart.Value, play: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _page.StatusText.Text = "Không tiếp tục được preview: " + error.Message; }
        }

        private async Task CompletePlaybackAsync()
        {
            await SetModeAsync(enabled: false, play: false);
            HasEnded = true;
            _page._syncingTimeline = true;
            try { _page.Timeline.Value = _page.Timeline.Maximum; }
            finally { _page._syncingTimeline = false; }
            _page.UpdateClock();
            _page.StatusText.Text = "Đã xem hết bản chỉnh. Bấm Play để phát lại từ đầu.";
            _page.SyncShellPlayerControls();
        }

        private void PlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var errorMessage = args.ErrorMessage;
            _page.DispatcherQueue.TryEnqueue(() => _ = RecoverFromPlayerFailureAsync(sender, errorMessage));
        }

        private async Task RecoverFromPlayerFailureAsync(MediaPlayer failedPlayer, string errorMessage)
        {
            if (!ReferenceEquals(failedPlayer, _player)) return;
            try
            {
                Exception? cleanupFailure = null;
                try { await ResetAsync(); }
                catch (Exception error) { cleanupFailure = error; }

                if (_player is not null && !ReferenceEquals(failedPlayer, _player)) return;
                if (ReferenceEquals(failedPlayer, _player))
                {
                    ClearFullscreenTracking(unregisterCallback: true);
                    DisposePlayer();
                    IsPreviewMode = false;
                    HasEnded = false;
                    ApplyPresentation(processed: false);
                }
                if (_player is null) CreatePlayer();
                _page.StatusText.Text = cleanupFailure is null
                    ? "Player preview lỗi: " + errorMessage + " Đã khôi phục player; bấm Play để thử lại."
                    : "Player preview lỗi: " + errorMessage + " Đã thay player; một phần cleanup báo lỗi: " + cleanupFailure.Message;
            }
            catch (Exception error)
            {
                _page.StatusText.Text = "Không khôi phục được player preview: " + error.Message;
            }
            finally
            {
                _page.RefreshEditorActions();
                _page.SyncShellPlayerControls();
            }
        }
    }
}

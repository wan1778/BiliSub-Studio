using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private void AssertEditorInteractionContract()
    {
        if (!_interactionRepairInitialized)
            throw new InvalidOperationException("Editor interaction repair chưa được khởi tạo.");
        if (!_imageFeatureInitialized || _imageModeButton is null || _imageInspectorPanel is null || _imageOverlayCanvas is null)
            throw new InvalidOperationException("Editor thiếu công cụ Ảnh/logo trong runtime thực tế.");
        if (!_subtitleCueEditorInitialized || _subtitleCueList is null || _subtitleSourceEdit is null || _subtitleVietnameseEdit is null ||
            _subtitleLockToggle is null || _subtitleRetranslateCueButton is null || _subtitleSaveSrtButton is null)
            throw new InvalidOperationException("Editor thiếu phần sửa/khóa/dịch lại từng câu SRT.");
        if (_compactPreviewChrome is null || _compactPreviewChrome.Height is < 40 or > 50)
            throw new InvalidOperationException("Editor phải có đúng một thanh điều khiển Preview cao 40–50 px.");
        if (PreviewPlayer.AreTransportControlsEnabled)
            throw new InvalidOperationException("MediaPlayerElement không được bật transport mặc định chồng lên Preview.");
        if (PlaybackButton.Visibility != Visibility.Collapsed || FullscreenButton.Visibility != Visibility.Collapsed ||
            RefreshFrameButton.Visibility != Visibility.Collapsed || ClockText.Visibility != Visibility.Collapsed ||
            RegionTimelineCanvas.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Editor vẫn còn bộ timeline/transport cũ bên ngoài Preview.");
        if (Timeline.Parent is not Grid compactGrid || !ReferenceEquals(compactGrid.Parent, _compactPreviewChrome))
            throw new InvalidOperationException("Thanh seek phải nằm bên trong thanh điều khiển Preview duy nhất.");
        if (!ImportSrtButton.IsEnabled)
            throw new InvalidOperationException("Chọn SRT tiếng Trung phải bấm được ngay cả trước khi mở video.");
        if (_compactPlayButton is null || _compactMuteButton is null || _compactVolumeSlider is null || _compactFullscreenButton is null ||
            _compactCurrentTime is null || _compactTotalTime is null)
            throw new InvalidOperationException("Preview thiếu Play/Pause, âm lượng, fullscreen hoặc thời gian.");
    }
}

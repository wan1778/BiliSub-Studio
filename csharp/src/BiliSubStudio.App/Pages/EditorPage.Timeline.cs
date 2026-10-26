using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// Dải thời gian kiểu CapCut/Premiere cho trang Edit: thước thời gian, các
/// lane (hiệu ứng · phụ đề · voice), playhead đồng bộ với Player và zoom.
///
/// Ràng buộc thiết kế: project thật có hơn 14.000 cue nên KHÔNG dựng element
/// cho toàn bộ. Mỗi lần vẽ chỉ dựng block nằm trong viewport, và khi mật độ
/// quá cao thì lane phụ đề chuyển sang dạng thanh tổng hợp để không treo UI.
/// </summary>
public sealed partial class EditorPage
{
    private const int TimelineLaneEffect = 0;
    private const int TimelineLaneSubtitle = 1;
    private const int TimelineLaneVoice = 2;
    private const int TimelineLaneCount = 3;
    private const double TimelineRulerGap = 3;
    private const int MaxTimelineBlocks = 600;

    private double _timelinePixelsPerSecond = 10;
    private double _timelineScrollOffset;
    private bool _syncingTimelineZoom;
    private bool _timelineRenderQueued;
    private bool _timelineSelectionHooked;

    private static readonly SolidColorBrush TimelineTickBrush = TimelineBrush(255, 88, 100, 118);
    private static readonly SolidColorBrush TimelineMajorTickBrush = TimelineBrush(255, 148, 164, 186);
    private static readonly SolidColorBrush TimelineLabelBrush = TimelineBrush(255, 176, 188, 206);
    private static readonly SolidColorBrush TimelineLaneBrush = TimelineBrush(255, 30, 39, 54);
    private static readonly SolidColorBrush TimelineLaneAltBrush = TimelineBrush(255, 24, 32, 46);
    private static readonly SolidColorBrush TimelineCueBrush = TimelineBrush(255, 66, 130, 245);
    private static readonly SolidColorBrush TimelineCueMissingBrush = TimelineBrush(255, 100, 112, 132);
    private static readonly SolidColorBrush TimelineCueSelectedBrush = TimelineBrush(255, 255, 255, 255);
    private static readonly SolidColorBrush TimelineVoiceBrush = TimelineBrush(255, 34, 197, 94);
    private static readonly SolidColorBrush TimelineVoiceReviewBrush = TimelineBrush(255, 239, 68, 68);
    private static readonly SolidColorBrush TimelineTrimShadeBrush = TimelineBrush(140, 10, 16, 26);
    private static readonly SolidColorBrush TimelineSummaryBrush = TimelineBrush(255, 72, 86, 108);

    private static SolidColorBrush TimelineBrush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    private void InitializeTimeline()
    {
        if (_timelineSelectionHooked) return;
        _timelineSelectionHooked = true;
        SubtitleCueList.SelectionChanged += TimelineCueListSelectionChanged;
        // Không dùng thuộc tính SizeChanged trong XAML: repo cấm layout mutate
        // từ SizeChanged. Subscribe bằng code + dispatch trễ để không re-enter.
        TimelineScrollViewer.SizeChanged += TimelineViewportResized;
    }

    private static double TimelinePixelsPerSecondForZoom(double zoom)
    {
        var min = EditorTimelineLayout.MinPixelsPerSecond;
        var max = EditorTimelineLayout.MaxPixelsPerSecond;
        var ratio = Math.Clamp(zoom, 0, 100) / 100;
        return min * Math.Pow(max / min, ratio);
    }

    private static double TimelineZoomForPixelsPerSecond(double pixelsPerSecond)
    {
        var min = EditorTimelineLayout.MinPixelsPerSecond;
        var max = EditorTimelineLayout.MaxPixelsPerSecond;
        var clamped = EditorTimelineLayout.ClampPixelsPerSecond(pixelsPerSecond);
        var ratio = Math.Log(clamped / min) / Math.Log(max / min);
        return Math.Clamp(ratio, 0, 1) * 100;
    }

    private void QueueTimelineRender()
    {
        if (_timelineRenderQueued) return;
        _timelineRenderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _timelineRenderQueued = false;
            RenderTimeline();
        });
    }

    private void RenderTimeline()
    {
        if (TimelineContentGrid is null || TimelineRulerCanvas is null || TimelineTracksCanvas is null) return;
        var duration = _media?.Duration ?? 0;
        var pps = _timelinePixelsPerSecond;
        var contentWidth = EditorTimelineLayout.ContentWidth(duration, pps);
        var tracksHeight = EditorTimelineLayout.TracksHeight(TimelineLaneCount);
        var contentHeight = EditorTimelineLayout.RulerHeight + TimelineRulerGap + tracksHeight;

        // Chỉ gán khi thực sự khác: nếu không, SizeChanged của ScrollViewer sẽ
        // đẩy vòng layout lặp lại vô hạn.
        if (Math.Abs(TimelineContentGrid.Width - contentWidth) > .5) TimelineContentGrid.Width = contentWidth;
        if (Math.Abs(TimelineContentGrid.Height - contentHeight) > .5) TimelineContentGrid.Height = contentHeight;
        if (Math.Abs(TimelineRulerCanvas.Width - contentWidth) > .5) TimelineRulerCanvas.Width = contentWidth;
        if (Math.Abs(TimelineTracksCanvas.Width - contentWidth) > .5) TimelineTracksCanvas.Width = contentWidth;
        Canvas.SetTop(TimelineRulerCanvas, 0);
        Canvas.SetTop(TimelineTracksCanvas, EditorTimelineLayout.RulerHeight + TimelineRulerGap);
        if (Math.Abs(TimelineTracksCanvas.Height - tracksHeight) > .5) TimelineTracksCanvas.Height = tracksHeight;
        if (Math.Abs(TimelinePlayhead.Height - contentHeight) > .5) TimelinePlayhead.Height = contentHeight;

        var (visibleStart, visibleEnd) = EditorTimelineLayout.VisibleWindow(
            CurrentTimelineScrollOffset(),
            TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth,
            pps,
            duration);

        RenderTimelineRuler(duration, pps, visibleStart, visibleEnd);
        RenderTimelineLanes(contentWidth, pps, visibleStart, visibleEnd);
        UpdateTimelinePlayhead();
        UpdateTimelineZoomText();
    }

    private void RenderTimelineRuler(double duration, double pps, double visibleStart, double visibleEnd)
    {
        TimelineRulerCanvas.Children.Clear();

        // Vùng bị cắt khỏi export được làm mờ để thấy rõ phần giữ lại.
        var trim = _project?.Trim;
        if (trim is not null)
        {
            if (trim.Start > 0)
            {
                var shade = new Rectangle
                {
                    Width = Math.Max(1, EditorTimelineLayout.XForTime(trim.Start, pps)),
                    Height = EditorTimelineLayout.RulerHeight,
                    Fill = TimelineTrimShadeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(shade, 0);
                Canvas.SetTop(shade, 0);
                TimelineRulerCanvas.Children.Add(shade);
            }
            if (duration > trim.End)
            {
                var shade = new Rectangle
                {
                    Width = Math.Max(1, EditorTimelineLayout.XForTime(duration - trim.End, pps)),
                    Height = EditorTimelineLayout.RulerHeight,
                    Fill = TimelineTrimShadeBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(shade, EditorTimelineLayout.XForTime(trim.End, pps));
                Canvas.SetTop(shade, 0);
                TimelineRulerCanvas.Children.Add(shade);
            }
        }

        var major = EditorTimelineLayout.MajorTickSeconds(pps);
        var minor = Math.Max(.01, EditorTimelineLayout.MinorTickSeconds(pps));
        var firstIndex = (int)Math.Floor(visibleStart / minor);
        var lastIndex = (int)Math.Ceiling(visibleEnd / minor);
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var time = index * minor;
            if (time < -1e-9 || time > duration + 1e-9) continue;
            var x = EditorTimelineLayout.XForTime(time, pps);
            var isMajor = Math.Abs(Math.Round(time / major) * major - time) <= 1e-6;
            var tick = new Rectangle
            {
                Width = 1,
                Height = isMajor ? 10 : 5,
                Fill = isMajor ? TimelineMajorTickBrush : TimelineTickBrush,
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, isMajor ? 1 : 6);
            TimelineRulerCanvas.Children.Add(tick);
            if (!isMajor) continue;
            var label = new TextBlock
            {
                Text = FormatClock(time),
                FontSize = 9,
                Foreground = TimelineLabelBrush,
            };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 12);
            TimelineRulerCanvas.Children.Add(label);
        }
    }

    private void RenderTimelineLanes(double contentWidth, double pps, double visibleStart, double visibleEnd)
    {
        TimelineTracksCanvas.Children.Clear();
        for (var lane = 0; lane < TimelineLaneCount; lane++)
        {
            var background = new Rectangle
            {
                Width = contentWidth,
                Height = EditorTimelineLayout.LaneHeight,
                Fill = lane % 2 == 0 ? TimelineLaneBrush : TimelineLaneAltBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(background, 0);
            Canvas.SetTop(background, EditorTimelineLayout.LaneTop(lane));
            TimelineTracksCanvas.Children.Add(background);
        }

        RenderTimelineEffectBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineSubtitleBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineVoiceBlocks(pps, visibleStart, visibleEnd);
    }

    private void RenderTimelineEffectBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var duration = _media?.Duration ?? 0;
        var regions = _document.Regions;
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            var start = region.WholeVideo ? 0 : region.Start;
            var end = region.WholeVideo ? duration : region.End;
            if (!EditorTimelineLayout.IntersectsVisible(start, end, visibleStart, visibleEnd)) continue;
            var left = EditorTimelineLayout.XForTime(start, pps);
            var width = Math.Max(2, EditorTimelineLayout.XForTime(end - start, pps));
            var block = new Border
            {
                Width = width,
                Height = EditorTimelineLayout.LaneHeight - 4,
                CornerRadius = new CornerRadius(3),
                Background = TimelineEffectBrush(region.Effect),
                BorderBrush = TimelineBrush(255, 255, 255, 255),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneEffect) + 2);
            block.DataContext = index;
            ToolTipService.SetToolTip(block,
                $"{EffectLabel(region.Effect)} · {(region.WholeVideo ? "toàn video" : $"{region.Start:0.0}–{region.End:0.0}s")}");
            block.PointerPressed += TimelineRegionPressed;
            TimelineTracksCanvas.Children.Add(block);
        }
    }

    private void RenderTimelineSubtitleBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var cues = _subtitleSource?.Cues;
        if (cues is null || cues.Count == 0) return;
        var visible = 0;
        foreach (var cue in cues)
            if (EditorTimelineLayout.IntersectsVisible(cue.Start, cue.End, visibleStart, visibleEnd)) visible++;
        if (visible > MaxTimelineBlocks)
        {
            // Mật độ quá cao: dựng một thanh tổng hợp thay vì hàng nghìn element.
            var summary = new Border
            {
                Width = Math.Max(2, EditorTimelineLayout.XForTime(visibleEnd - visibleStart, pps)),
                Height = EditorTimelineLayout.LaneHeight - 4,
                CornerRadius = new CornerRadius(3),
                Background = TimelineSummaryBrush,
            };
            Canvas.SetLeft(summary, EditorTimelineLayout.XForTime(visibleStart, pps));
            Canvas.SetTop(summary, EditorTimelineLayout.LaneTop(TimelineLaneSubtitle) + 2);
            ToolTipService.SetToolTip(summary,
                $"{visible} câu phụ đề trong khung nhìn — thu gọn để giữ app mượt. Thu phóng để xem từng câu.");
            TimelineTracksCanvas.Children.Add(summary);
            return;
        }

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            if (!EditorTimelineLayout.IntersectsVisible(cue.Start, cue.End, visibleStart, visibleEnd)) continue;
            var left = EditorTimelineLayout.XForTime(cue.Start, pps);
            var width = Math.Max(2, EditorTimelineLayout.XForTime(cue.End - cue.Start, pps));
            var hasVietnamese = !string.IsNullOrWhiteSpace(cue.VietnameseText);
            var selected = _subtitleCueSelectedIndex == index;
            var block = new Border
            {
                Width = width,
                Height = EditorTimelineLayout.LaneHeight - 4,
                CornerRadius = new CornerRadius(2),
                Background = hasVietnamese ? TimelineCueBrush : TimelineCueMissingBrush,
                BorderBrush = selected ? TimelineCueSelectedBrush : TimelineBrush(0, 0, 0, 0),
                BorderThickness = new Thickness(selected ? 1 : 0),
            };
            if (width >= 46)
            {
                var text = new TextBlock
                {
                    Text = TrimPreview(hasVietnamese ? cue.VietnameseText : cue.SourceText),
                    FontSize = 9,
                    Foreground = TimelineBrush(255, 240, 246, 255),
                    Margin = new Thickness(3, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextScaleFactorEnabled = false,
                };
                block.Child = text;
            }
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneSubtitle) + 2);
            ToolTipService.SetToolTip(block, $"#{cue.Number} · {cue.Timing}\n{TrimPreview(cue.SourceText)}");
            var captured = index;
            block.PointerPressed += (_, args) => TimelineCuePressed(captured, args);
            TimelineTracksCanvas.Children.Add(block);
        }
    }

    private void RenderTimelineVoiceBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var track = _voiceTrack;
        if (track is null) return;
        var start = track.Start;
        var end = track.Start + Math.Max(0, track.Duration);
        if (!EditorTimelineLayout.IntersectsVisible(start, end, visibleStart, visibleEnd)) return;
        var left = EditorTimelineLayout.XForTime(start, pps);
        var width = Math.Max(2, EditorTimelineLayout.XForTime(end - start, pps));
        var block = new Border
        {
            Width = width,
            Height = EditorTimelineLayout.LaneHeight - 8,
            CornerRadius = new CornerRadius(3),
            Background = TimelineVoiceBrush,
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneVoice) + 4);
        ToolTipService.SetToolTip(block, $"Voice · {start:0.0}–{end:0.0}s");
        TimelineTracksCanvas.Children.Add(block);

        var visibleWindows = 0;
        foreach (var window in _voiceCueWindows.Values)
            if (EditorTimelineLayout.IntersectsVisible(window.VoiceStart, window.VoiceEnd, visibleStart, visibleEnd))
                visibleWindows++;
        if (visibleWindows > MaxTimelineBlocks)
        {
            var summary = new Border
            {
                Width = Math.Max(2, EditorTimelineLayout.XForTime(visibleEnd - visibleStart, pps)),
                Height = EditorTimelineLayout.LaneHeight - 6,
                CornerRadius = new CornerRadius(3),
                Background = TimelineVoiceBrush,
                Opacity = .45,
            };
            Canvas.SetLeft(summary, EditorTimelineLayout.XForTime(visibleStart, pps));
            Canvas.SetTop(summary, EditorTimelineLayout.LaneTop(TimelineLaneVoice) + 3);
            ToolTipService.SetToolTip(summary, $"{visibleWindows} đoạn voice trong khung nhìn — thu gọn để giữ app mượt.");
            TimelineTracksCanvas.Children.Add(summary);
            return;
        }

        foreach (var window in _voiceCueWindows.Values)
        {
            if (!EditorTimelineLayout.IntersectsVisible(window.VoiceStart, window.VoiceEnd, visibleStart, visibleEnd)) continue;
            var mark = new Rectangle
            {
                Width = Math.Max(1, EditorTimelineLayout.XForTime(window.VoiceEnd - window.VoiceStart, pps)),
                Height = EditorTimelineLayout.LaneHeight - 4,
                Fill = string.Equals(window.Status, "review", StringComparison.OrdinalIgnoreCase)
                    ? TimelineVoiceReviewBrush
                    : TimelineBrush(60, 255, 255, 255),
            };
            Canvas.SetLeft(mark, EditorTimelineLayout.XForTime(window.VoiceStart, pps));
            Canvas.SetTop(mark, EditorTimelineLayout.LaneTop(TimelineLaneVoice) + 2);
            TimelineTracksCanvas.Children.Add(mark);
        }
    }

    private static SolidColorBrush TimelineEffectBrush(string effect) => effect?.Trim().ToLowerInvariant() switch
    {
        "mosaic" => TimelineBrush(255, 168, 85, 247),
        "cover" => TimelineBrush(255, 236, 72, 153),
        "clean" => TimelineBrush(255, 251, 191, 36),
        _ => TimelineBrush(255, 100, 116, 139),
    };

    private void TimelineCuePressed(int index, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        var cues = _subtitleSource?.Cues;
        if (cues is null || index < 0 || index >= cues.Count) return;
        if (_subtitleCueSelectedIndex == index)
        {
            _ = SeekEditorToSubtitleCueAsync(cues[index].Start);
            return;
        }
        SubtitleCueList.SelectedIndex = index;
    }

    private void TimelineRegionPressed(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement block || block.DataContext is not int regionIndex) return;
        var regions = _document.Regions;
        if (regionIndex < 0 || regionIndex >= regions.Count) return;
        // RegionList là chủ sở hữu duy nhất của việc chọn vùng (BLUR-03): timeline
        // chỉ gán chỉ số, không tự gọi _document.Select.
        RegionList.SelectedIndex = regionIndex;
    }

    private void TimelineCueListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subtitleCueSyncing) return;
        var cues = _subtitleSource?.Cues;
        if (cues is null) return;
        var index = SubtitleCueList.SelectedIndex;
        if (index < 0 || index >= cues.Count) return;
        ScrollTimelineToTime(cues[index].Start);
        QueueTimelineRender();
    }

    private void ScrollTimelineToTime(double time)
    {
        if (TimelineScrollViewer is null) return;
        var viewport = TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth;
        if (viewport <= 1) return;
        var pps = _timelinePixelsPerSecond;
        var x = EditorTimelineLayout.XForTime(time, pps);
        var offset = Math.Clamp(x - viewport / 2, 0, Math.Max(0, EditorTimelineLayout.ContentWidth(_media?.Duration ?? 0, pps) - viewport));
        if (Math.Abs(offset - CurrentTimelineScrollOffset()) < 1) return;
        TimelineScrollViewer.ChangeView(offset, null, null);
    }

    private void UpdateTimelinePlayhead()
    {
        if (TimelinePlayhead is null) return;
        var x = EditorTimelineLayout.XForTime(Timeline.Value, _timelinePixelsPerSecond);
        Canvas.SetLeft(TimelinePlayhead, x);
    }

    private void UpdateTimelineZoomText()
    {
        if (TimelineZoomText is null) return;
        var secondsPerPixel = 1 / Math.Max(.0001, _timelinePixelsPerSecond);
        TimelineZoomText.Text = secondsPerPixel >= 1
            ? $"{secondsPerPixel:0.##} giây/px"
            : $"{1 / secondsPerPixel:0.##} px/giây";
    }

    private void TimelineZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTimelineZoom) return;
        _timelinePixelsPerSecond = TimelinePixelsPerSecondForZoom(e.NewValue);
        QueueTimelineRender();
    }

    private void TimelineZoomOut_Click(object sender, RoutedEventArgs e)
    {
        TimelineZoomSlider.Value = Math.Max(TimelineZoomSlider.Minimum, TimelineZoomSlider.Value - 6);
    }

    private void TimelineZoomIn_Click(object sender, RoutedEventArgs e)
    {
        TimelineZoomSlider.Value = Math.Min(TimelineZoomSlider.Maximum, TimelineZoomSlider.Value + 6);
    }

    private void TimelineFit_Click(object sender, RoutedEventArgs e)
    {
        var viewport = TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth;
        var duration = _media?.Duration ?? 0;
        if (viewport <= 1 || duration <= 0) return;
        var pps = EditorTimelineLayout.FitPixelsPerSecond(duration, viewport);
        _syncingTimelineZoom = true;
        try { TimelineZoomSlider.Value = TimelineZoomForPixelsPerSecond(pps); }
        finally { _syncingTimelineZoom = false; }
        _timelinePixelsPerSecond = pps;
        TimelineScrollViewer.ChangeView(0, null, null);
        QueueTimelineRender();
    }

    /// <summary>
    /// WinUI 3 ScrollViewer không expose offset đọc được, nên đo bằng cách
    /// đặt một marker ở x=0 trong nội dung và hỏi vị trí nó so với ScrollViewer.
    /// </summary>
    private double CurrentTimelineScrollOffset()
    {
        if (TimelineOriginMarker is null || TimelineScrollViewer is null) return _timelineScrollOffset;
        try
        {
            var point = TimelineOriginMarker.TransformToVisual(TimelineScrollViewer).TransformPoint(new Point(0, 0));
            if (double.IsFinite(point.X)) return Math.Max(0, -point.X);
        }
        catch (Exception)
        {
            // Marker chưa nằm trong cây visual (panel đang ẩn) -> giữ offset cuối.
        }
        return _timelineScrollOffset;
    }

    private void TimelineScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        _timelineScrollOffset = CurrentTimelineScrollOffset();
        QueueTimelineRender();
    }

    private void TimelineViewportResized(object sender, SizeChangedEventArgs e) => QueueTimelineRender();

    private void TimelineRuler_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    private void TimelineTracks_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    private void SeekTimelineFromPointer(PointerRoutedEventArgs e)
    {
        if (_media is null || TimelineContentGrid is null) return;
        var position = e.GetCurrentPoint(TimelineContentGrid).Position;
        var time = EditorTimelineLayout.TimeForX(position.X, _timelinePixelsPerSecond);
        var target = Math.Clamp(time, 0, _media.Duration);
        _ = SeekEditorToSubtitleCueAsync(target);
    }
}

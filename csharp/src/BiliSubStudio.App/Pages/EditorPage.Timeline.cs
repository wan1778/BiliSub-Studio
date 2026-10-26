using BiliSubStudio.App.Services;
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
    private const int TimelineLaneVideo = 0;
    private const int TimelineLaneEffect = 1;
    private const int TimelineLaneSubtitle = 2;
    private const int TimelineLaneVoice = 3;
    private const int TimelineLaneCount = 4;
    private const double TimelineRulerGap = 3;
    private const int MaxTimelineBlocks = 600;
    private const double TimelineCueEdgeGrabPixels = 6;
    private const double TimelineCueMinDuration = .2;
    private const double TimelineCueVisiblePixels = 3;

    private double _timelinePixelsPerSecond = 10;
    private double _timelineScrollOffset;
    private bool _syncingTimelineZoom;
    private bool _timelineRenderQueued;
    private bool _timelineSelectionHooked;
    private bool _timelineFitPending;
    private double? _timelinePendingScrollTime;
    private int _timelineDragCueIndex = -1;
    private EditorCueEdge _timelineDragEdge;
    private double _timelineDragOriginContentX;
    private double _timelineDragOriginStart;
    private double _timelineDragOriginEnd;
    private double _timelineDragNewStart;
    private double _timelineDragNewEnd;
    private double _timelineDragOriginScreenX;
    private FrameworkElement? _timelineDragBlock;
    private IReadOnlyList<EditorTimelineClip> _timelineClips = [];
    private int _timelineSelectedClipIndex = -1;

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
    private static readonly SolidColorBrush TimelineClipBrush = TimelineBrush(255, 45, 212, 191);
    private static readonly SolidColorBrush TimelineClipSelectedBrush = TimelineBrush(255, 125, 211, 252);

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
        SyncTimelineHeaderRows();
        // WinUI nối handler ValueChanged SAU khi nạp XAML, nên Value="35" trong
        // XAML không được áp cho code. Đặt zoom ở đây để trạng thái luôn
        // xác định, không phụ thuộc thứ tự nạp XAML.
        ApplyTimelineZoom(DefaultTimelineZoom);
    }

    private const double DefaultTimelineZoom = 35;
    private const double ReadableTimelinePixelsPerSecond = 8;

    /// <summary>
    /// Cột nhãn lane phải khớp đúng chiều cao lane trong Canvas, nếu không
    /// chữ sẽ trôi dần theo từng lane (mỗi lane lệch đúng bằng khoảng cách
    /// giữa các lane).
    /// </summary>
    private void SyncTimelineHeaderRows()
    {
        if (TimelineHeaderGrid is null) return;
        var rows = TimelineHeaderGrid.RowDefinitions;
        if (rows.Count < 2 + TimelineLaneCount) return;
        rows[0].Height = new GridLength(EditorTimelineLayout.RulerHeight);
        rows[1].Height = new GridLength(TimelineRulerGap);
        for (var lane = 0; lane < TimelineLaneCount; lane++)
            rows[2 + lane].Height = new GridLength(EditorTimelineLayout.LaneHeight);
    }

    /// <summary>
    /// Mở nguồn/project mới: dải thời gian phải vừa khung và quay về đầu.
    /// Không làm vậy thì thước giữ zoom của project cũ và hiển thị sai phạm vi
    /// (video 9 giờ nhìn như video 3 giờ).
    /// </summary>
    private void ResetTimelineForNewSource()
    {
        _timelineFitPending = true;
        _timelineScrollOffset = 0;
        _timelineDragCueIndex = -1;
        _timelineDragBlock = null;
        ApplyTimelineComposition();
        ApplyTimelineFit();
    }

    /// <summary>
    /// Nạp danh sách clip của project. Dự án cũ chưa có timeline thì tự sinh
    /// một clip từ video nguồn để không phá dự án đang dùng.
    /// </summary>
    private void ApplyTimelineComposition()
    {
        EditorSourceFingerprint? fingerprint = _project?.Source;
        if (fingerprint is null && _media is not null)
            fingerprint = new EditorSourceFingerprint(_path ?? string.Empty, 0, 0, _media.Width, _media.Height, _media.Duration);
        _timelineClips = EditorTimelineComposition.Resolve(_project?.Timeline, fingerprint ?? default);
        _timelineSelectedClipIndex = _timelineClips.Count > 0 ? 0 : -1;
    }

    /// <summary>Tổng thời lượng của dải ghép (tổng các clip).</summary>
    private double TimelineDuration => EditorTimelineComposition.TotalDuration(_timelineClips);

    private void ApplyTimelineFit()
    {
        if (TimelineScrollViewer is null) return;
        var duration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;
        var viewport = TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth;
        if (duration <= 0 || viewport <= 1) return;
        _timelineFitPending = false;
        var pps = EditorTimelineLayout.FitPixelsPerSecond(duration, viewport);
        ApplyTimelineZoom(TimelineZoomForPixelsPerSecond(pps));
        TimelineScrollViewer.ChangeView(0, null, null);
        QueueTimelineRender();
    }

    /// <summary>Đặt zoom từ giá trị slider (0..100) và cập nhật mọi thứ liên quan.</summary>
    private void ApplyTimelineZoom(double zoom)
    {
        _syncingTimelineZoom = true;
        try { TimelineZoomSlider.Value = Math.Clamp(zoom, TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum); }
        finally { _syncingTimelineZoom = false; }
        _timelinePixelsPerSecond = TimelinePixelsPerSecondForZoom(TimelineZoomSlider.Value);
        UpdateTimelineZoomText();
        QueueTimelineRender();
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
        var duration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;
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
        ApplyPendingTimelineScroll();
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
                Text = FormatClock(time, duration >= 3600),
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

        RenderTimelineVideoBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineEffectBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineSubtitleBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineVoiceBlocks(pps, visibleStart, visibleEnd);
    }

    /// <summary>
    /// Lane video: mỗi clip là một khối trên dải ghép. Bấm vào khối để chọn
    /// clip (dùng cho tách/xoá), clip chứa playhead được tô sáng.
    /// </summary>
    private void RenderTimelineVideoBlocks(double pps, double visibleStart, double visibleEnd)
    {
        if (_timelineClips.Count == 0) return;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var playheadClip = EditorTimelineComposition.Locate(_timelineClips, Timeline.Value).Clip.Id;
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            var timelineStart = starts[index];
            var timelineEnd = timelineStart + clip.Duration;
            if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
            var width = Math.Max(2, EditorTimelineLayout.XForTime(clip.Duration, pps));
            var selected = index == _timelineSelectedClipIndex || clip.Id == playheadClip;
            var block = new Border
            {
                Width = width,
                Height = EditorTimelineLayout.LaneHeight - 4,
                CornerRadius = new CornerRadius(3),
                Background = selected ? TimelineClipSelectedBrush : TimelineClipBrush,
                BorderBrush = index == _timelineSelectedClipIndex ? TimelineCueSelectedBrush : TimelineBrush(0, 0, 0, 0),
                BorderThickness = new Thickness(index == _timelineSelectedClipIndex ? 1 : 0),
            };
            if (width >= 46)
            {
                block.Child = new TextBlock
                {
                    Text = TrimPreview(clip.Label),
                    FontSize = 9,
                    Foreground = TimelineBrush(255, 8, 47, 55),
                    Margin = new Thickness(4, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextScaleFactorEnabled = false,
                };
            }
            Canvas.SetLeft(block, EditorTimelineLayout.XForTime(timelineStart, pps));
            Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneVideo) + 2);
            ToolTipService.SetToolTip(block,
                $"{clip.Label} · {FormatClock(clip.Duration)} · nguồn {clip.SourcePath}\nKéo mép phải để cắt đoạn này.");
            var captured = index;
            block.PointerPressed += (_, args) => TimelineVideoClipPressed(captured, args);
            TimelineTracksCanvas.Children.Add(block);
        }
    }

    private void TimelineVideoClipPressed(int index, PointerRoutedEventArgs args)
    {
        if (index < 0 || index >= _timelineClips.Count) return;
        _timelineSelectedClipIndex = index;
        QueueTimelineRender();
        var clip = _timelineClips[index];
        StatusText.Text = $"Đang chọn đoạn “{clip.Label}” ({FormatClock(clip.Duration)}). Dùng Tách/Xoá để chỉnh dải ghép.";
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
            var timelineStart = TimelineTimeForSource(start);
            var timelineEnd = TimelineTimeForSource(end);
            if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
            var left = EditorTimelineLayout.XForTime(timelineStart, pps);
            var width = Math.Max(2, EditorTimelineLayout.XForTime(timelineEnd - timelineStart, pps));
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
            if (EditorTimelineLayout.IntersectsVisible(
                    TimelineTimeForSource(cue.Start),
                    TimelineTimeForSource(cue.End),
                    visibleStart,
                    visibleEnd)) visible++;
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
            summary.PointerPressed += TimelineSubtitleSummaryPressed;
            TimelineTracksCanvas.Children.Add(summary);
            return;
        }

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var timelineStart = TimelineTimeForSource(cue.Start);
            var timelineEnd = TimelineTimeForSource(cue.End);
            if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
            var left = EditorTimelineLayout.XForTime(timelineStart, pps);
            var width = Math.Max(2, EditorTimelineLayout.XForTime(timelineEnd - timelineStart, pps));
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
            ToolTipService.SetToolTip(block, $"#{cue.Number} · {cue.Timing}\n{TrimPreview(cue.SourceText)}\nKéo mép trái/phải để đổi thời lượng.");
            var captured = index;
            block.PointerPressed += (_, args) => TimelineCuePointerPressed(captured, block, args);
            block.PointerMoved += (_, args) => TimelineCuePointerMoved(captured, block, args);
            block.PointerReleased += (_, args) => TimelineCuePointerReleased(captured, block, args);
            block.PointerCanceled += (_, args) => TimelineCuePointerCanceled(captured, block, args);
            TimelineTracksCanvas.Children.Add(block);
        }
    }

    private void RenderTimelineVoiceBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var track = _voiceTrack;
        if (track is null) return;
        var start = track.Start;
        var end = track.Start + Math.Max(0, track.Duration);
        var timelineStart = TimelineTimeForSource(start);
        var timelineEnd = TimelineTimeForSource(end);
        if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) return;
        var left = EditorTimelineLayout.XForTime(timelineStart, pps);
        var width = Math.Max(2, EditorTimelineLayout.XForTime(timelineEnd - timelineStart, pps));
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
            if (EditorTimelineLayout.IntersectsVisible(
                    TimelineTimeForSource(window.VoiceStart),
                    TimelineTimeForSource(window.VoiceEnd),
                    visibleStart,
                    visibleEnd)) visibleWindows++;
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
            var windowStart = TimelineTimeForSource(window.VoiceStart);
            var windowEnd = TimelineTimeForSource(window.VoiceEnd);
            if (!EditorTimelineLayout.IntersectsVisible(windowStart, windowEnd, visibleStart, visibleEnd)) continue;
            var mark = new Rectangle
            {
                Width = Math.Max(1, EditorTimelineLayout.XForTime(windowEnd - windowStart, pps)),
                Height = EditorTimelineLayout.LaneHeight - 4,
                Fill = string.Equals(window.Status, "review", StringComparison.OrdinalIgnoreCase)
                    ? TimelineVoiceReviewBrush
                    : TimelineBrush(60, 255, 255, 255),
            };
            Canvas.SetLeft(mark, EditorTimelineLayout.XForTime(windowStart, pps));
            Canvas.SetTop(mark, EditorTimelineLayout.LaneTop(TimelineLaneVoice) + 2);
            TimelineTracksCanvas.Children.Add(mark);
        }
    }

    /// <summary>
    /// Đổi thời gian trong file nguồn sang thời gian trên dải ghép. Dải một
    /// video thì bằng nhau; dải nhiều clip thì tính theo vị trí của clip chứa
    /// thời gian đó.
    /// </summary>
    private double TimelineTimeForSource(double sourceTime)
    {
        if (_timelineClips.Count <= 1) return sourceTime;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (sourceTime >= clip.Start - .0005 && sourceTime <= clip.End + .0005)
                return starts[index] + (sourceTime - clip.Start);
        }
        return sourceTime;
    }

    /// <summary>Đổi thời gian trên dải ghép về thời gian trong file nguồn của clip tương ứng.</summary>
    private double SourceTimeForTimeline(double timelineTime)
        => EditorTimelineComposition.Locate(_timelineClips, timelineTime).SourceTime;

    private void ApplyTimelineClips(IReadOnlyList<EditorTimelineClip> clips)
    {
        _timelineClips = clips;
        _timelineSelectedClipIndex = clips.Count > 0 ? Math.Clamp(_timelineSelectedClipIndex, 0, clips.Count - 1) : -1;
        if (_project is not null) _project = _project with { Timeline = new EditorTimelineProject(clips) };
        QueueTimelineRender();
    }

    private async void TimelineAddClipButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = await _picker.PickVideoAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            var media = await _application.Media.ProbeAsync(path, CancellationToken.None);
            if (media.Duration <= 0)
            {
                StatusText.Text = "Không đọc được thời lượng của video vừa chọn.";
                return;
            }
            var clip = new EditorTimelineClip(
                $"clip-{Guid.NewGuid().ToString("N")[..8]}",
                path,
                0,
                media.Duration,
                Path.GetFileName(path));
            var clips = new List<EditorTimelineClip>(_timelineClips) { clip };
            ApplyTimelineClips(clips);
            await SaveProjectNowAsync();
            StatusText.Text = $"Đã thêm đoạn “{clip.Label}” ({FormatClock(clip.Duration)}) vào cuối dải thời gian.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không thêm được đoạn video: " + error.Message;
        }
    }

    private async void TimelineSplitClipButton_Click(object sender, RoutedEventArgs args)
    {
        if (_timelineClips.Count == 0) return;
        var (clip, sourceTime, _) = EditorTimelineComposition.Locate(_timelineClips, Timeline.Value);
        var index = -1;
        for (var position = 0; position < _timelineClips.Count; position++)
        {
            if (_timelineClips[position].Id != clip.Id) continue;
            index = position;
            break;
        }
        var clips = EditorTimelineComposition.SplitAtSourceTime(_timelineClips, index, sourceTime);
        if (clips.Count == _timelineClips.Count)
        {
            StatusText.Text = "Không tách được: hãy đặt playhead vào giữa một đoạn video.";
            return;
        }
        _timelineSelectedClipIndex = index;
        ApplyTimelineClips(clips);
        await SaveProjectNowAsync();
        StatusText.Text = "Đã tách đoạn video tại playhead.";
    }

    private async void TimelineRemoveClipButton_Click(object sender, RoutedEventArgs args)
    {
        var index = _timelineSelectedClipIndex;
        if (index < 0 || index >= _timelineClips.Count)
        {
            StatusText.Text = "Hãy bấm vào một đoạn video trên dải để chọn đoạn cần xoá.";
            return;
        }
        if (_timelineClips.Count <= 1)
        {
            StatusText.Text = "Dải thời gian phải còn ít nhất một đoạn video.";
            return;
        }
        var label = _timelineClips[index].Label;
        var clips = EditorTimelineComposition.RemoveAt(_timelineClips, index);
        ApplyTimelineClips(clips);
        await SaveProjectNowAsync();
        StatusText.Text = $"Đã xoá đoạn “{label}” khỏi dải thời gian.";
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

    private void TimelineSubtitleSummaryPressed(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        if (TimelineContentGrid is null) return;
        var position = args.GetCurrentPoint(TimelineContentGrid).Position;
        var time = EditorTimelineLayout.TimeForX(position.X, _timelinePixelsPerSecond);
        ApplyTimelineZoom(TimelineZoomForPixelsPerSecond(ReadableTimelinePixelsPerSecond));
        ScrollTimelineToTime(time);
    }

    private void TimelineCuePointerPressed(int index, FrameworkElement block, PointerRoutedEventArgs args)
    {
        var cues = _subtitleSource?.Cues;
        if (cues is null || index < 0 || index >= cues.Count || TimelineContentGrid is null) return;
        var width = block.ActualWidth;
        if (width < TimelineCueEdgeGrabPixels * 3)
        {
            TimelineCuePressed(index, args);
            return;
        }
        var position = args.GetCurrentPoint(block).Position;
        EditorCueEdge edge;
        if (position.X <= TimelineCueEdgeGrabPixels) edge = EditorCueEdge.Start;
        else if (position.X >= width - TimelineCueEdgeGrabPixels) edge = EditorCueEdge.End;
        else
        {
            TimelineCuePressed(index, args);
            return;
        }
        args.Handled = true;
        var cue = cues[index];
        _timelineDragCueIndex = index;
        _timelineDragEdge = edge;
        _timelineDragOriginStart = cue.Start;
        _timelineDragOriginEnd = cue.End;
        _timelineDragNewStart = cue.Start;
        _timelineDragNewEnd = cue.End;
        _timelineDragOriginContentX = args.GetCurrentPoint(TimelineContentGrid).Position.X;
        _timelineDragBlock = block;
        block.CapturePointer(args.Pointer);
        _timelineDragOriginScreenX = Win32Cursor.TryGetScreenPosition(out var screenX, out _) ? screenX : 0d;
    }

    private void TimelineCuePointerMoved(int index, FrameworkElement block, PointerRoutedEventArgs args)
    {
        if (_timelineDragCueIndex != index || _timelineDragBlock is null || TimelineContentGrid is null) return;
        args.Handled = true;
        var cues = _subtitleSource?.Cues;
        if (cues is null || index < 0 || index >= cues.Count) return;
        var pps = _timelinePixelsPerSecond;
        if (pps <= 0) return;
        var scale = TimelineContentGrid.XamlRoot?.RasterizationScale ?? 1d;
        var cursorContentX = _timelineDragOriginContentX;
        if (Win32Cursor.TryGetScreenPosition(out var screenX, out _))
        {
            var originScreenX = _timelineDragOriginScreenX - _timelineDragOriginContentX * scale;
            cursorContentX = (screenX - originScreenX) / scale;
        }
        var deltaSeconds = (cursorContentX - _timelineDragOriginContentX) / pps;
        var updated = EditorSubtitleDocument.RetimeCue(
            cues,
            index,
            _timelineDragEdge,
            _timelineDragEdge == EditorCueEdge.Start
                ? _timelineDragOriginStart + deltaSeconds
                : _timelineDragOriginEnd + deltaSeconds,
            TimelineCueMinDuration);
        _timelineDragNewStart = updated.Start;
        _timelineDragNewEnd = updated.End;
        Canvas.SetLeft(block, EditorTimelineLayout.XForTime(updated.Start, pps));
        block.Width = Math.Max(2, EditorTimelineLayout.XForTime(updated.End - updated.Start, pps));
    }

    private void TimelineCuePointerReleased(int index, FrameworkElement block, PointerRoutedEventArgs args)
    {
        if (_timelineDragCueIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        var start = _timelineDragNewStart;
        var end = _timelineDragNewEnd;
        var changed = Math.Abs(start - _timelineDragOriginStart) > .001
            || Math.Abs(end - _timelineDragOriginEnd) > .001;
        _timelineDragCueIndex = -1;
        _timelineDragBlock = null;
        if (!changed)
        {
            QueueTimelineRender();
            return;
        }
        _ = CommitCueTimingAsync(index, start, end);
    }

    private void TimelineCuePointerCanceled(int index, FrameworkElement block, PointerRoutedEventArgs args)
    {
        if (_timelineDragCueIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        _timelineDragCueIndex = -1;
        _timelineDragBlock = null;
        QueueTimelineRender();
    }

    private async Task CommitCueTimingAsync(int index, double start, double end)
    {
        try
        {
            var source = _subtitleSource;
            var cues = source?.Cues;
            if (source is null || cues is null || index < 0 || index >= cues.Count) return;
            var cue = cues[index];
            var updated = cue with
            {
                Start = start,
                End = end,
                Timing = EditorSubtitleDocument.FormatTiming(start, end),
            };
            var array = cues.ToArray();
            array[index] = updated;
            _subtitleSource = source with { Cues = array };
            MarkTranslatedOutputStale();
            await SaveProjectNowAsync();
            _subtitleCueSyncing = true;
            try { SubtitleCueList.SelectedIndex = index; }
            finally { _subtitleCueSyncing = false; }
            _subtitleCueSelectedIndex = index;
            LoadSelectedSubtitleCue();
            UpdateSubtitleSummary();
            RenderOverlays();
            QueuePreviewRefresh();
            RefreshEditorActions();
            RefreshSubtitleCueEditorControls();
            SubtitleCueEditorStatus.Text = $"Đã đổi thời lượng cue {cue.Number}: {updated.Timing}";
            StatusText.Text = $"Đã đổi thời lượng cue {cue.Number} trên dải thời gian. Bấm “Lưu SRT Việt” để ghi timecode mới ra file.";
            QueueTimelineRender();
        }
        catch (Exception error)
        {
            StatusText.Text = "Không đổi được thời lượng cue: " + error.Message;
        }
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
        var cue = cues[index];
        ScrollTimelineToTime(cue.Start, cue.End - cue.Start);
        QueueTimelineRender();
    }

    private void ScrollTimelineToTime(double time, double spanSeconds = 0)
    {
        if (TimelineScrollViewer is null) return;
        // Cue quá nhỏ để nhìn thấy khi đang thu nhỏ: phóng tới mức đọc được
        // rồi mới cuộn tới cue, nhưng không đụng vào mức zoom người dùng đã
        // phóng vào.
        if (spanSeconds > 0 && _timelinePixelsPerSecond < ReadableTimelinePixelsPerSecond
            && spanSeconds * _timelinePixelsPerSecond < TimelineCueVisiblePixels)
        {
            ApplyTimelineZoom(TimelineZoomForPixelsPerSecond(ReadableTimelinePixelsPerSecond));
        }
        _timelinePendingScrollTime = TimelineTimeForSource(time);
        QueueTimelineRender();
    }

    /// <summary>
    /// Cuộn sau khi render: đổi zoom làm đổi chiều rộng nội dung, nếu cuộn
    /// trước khi render thì ScrollViewer kẹp offset về 0.
    /// </summary>
    private void ApplyPendingTimelineScroll()
    {
        if (_timelinePendingScrollTime is not { } time || TimelineScrollViewer is null) return;
        _timelinePendingScrollTime = null;
        var viewport = TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth;
        if (viewport <= 1) return;
        var pps = _timelinePixelsPerSecond;
        var x = EditorTimelineLayout.XForTime(time, pps);
        var timelineDuration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;
        var offset = Math.Clamp(x - viewport / 2, 0, Math.Max(0, EditorTimelineLayout.ContentWidth(timelineDuration, pps) - viewport));
        if (Math.Abs(offset - CurrentTimelineScrollOffset()) < 1) return;
        TimelineScrollViewer.UpdateLayout();
        TimelineScrollViewer.ChangeView(offset, null, null);
    }

    private void UpdateTimelinePlayhead()
    {
        if (TimelinePlayhead is null) return;
        var x = EditorTimelineLayout.XForTime(TimelineTimeForSource(Timeline.Value), _timelinePixelsPerSecond);
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
        _timelineFitPending = false;
        _timelinePixelsPerSecond = TimelinePixelsPerSecondForZoom(e.NewValue);
        QueueTimelineRender();
    }

    private void TimelineZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ApplyTimelineZoom(TimelineZoomSlider.Value - 6);
    }

    private void TimelineZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ApplyTimelineZoom(TimelineZoomSlider.Value + 6);
    }

    private void TimelineFit_Click(object sender, RoutedEventArgs e)
    {
        ApplyTimelineFit();
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

    private void TimelineViewportResized(object sender, SizeChangedEventArgs e)
    {
        if (_timelineFitPending) ApplyTimelineFit();
        else QueueTimelineRender();
    }

    private void TimelineRuler_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    private void TimelineTracks_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    private void SeekTimelineFromPointer(PointerRoutedEventArgs e)
    {
        if (_media is null || TimelineContentGrid is null) return;
        var position = e.GetCurrentPoint(TimelineContentGrid).Position;
        var timelineTime = EditorTimelineLayout.TimeForX(position.X, _timelinePixelsPerSecond);
        var target = Math.Clamp(SourceTimeForTimeline(timelineTime), 0, _media.Duration);
        _ = SeekEditorToSubtitleCueAsync(target);
    }
}

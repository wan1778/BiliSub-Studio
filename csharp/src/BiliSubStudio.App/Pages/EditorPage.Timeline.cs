using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Editor;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// A CapCut/Premiere-style timeline for the Edit page: the time ruler, the
/// lanes (video · voice · subtitles · effects), the playhead synced with the
/// Player, and zoom.
///
/// Design constraint: a real project has over 14,000 cues, so elements are NOT
/// built for all of them. Each render only builds blocks inside the viewport,
/// and when density is too high the subtitle lane switches to a summary bar so
/// the UI does not freeze.
/// </summary>
public sealed partial class EditorPage
{
    /// <summary>
    /// Clip drag mode on the timeline: drag an edge to trim the segment, drag
    /// the body to reorder it, or no dragging at all (just a click to select).
    /// </summary>
    private enum TimelineClipDragMode
    {
        None = 0,
        Edge = 1,
        Move = 2,
    }

    private const int TimelineLaneVideo = (int)EditorTimelineLane.Video;
    private const int TimelineLaneVoice = (int)EditorTimelineLane.Voice;
    private const int TimelineLaneSubtitle = (int)EditorTimelineLane.Subtitle;
    private const int TimelineLaneEffect = (int)EditorTimelineLane.Effect;
    private const int TimelineLaneCount = EditorTimelineLayout.LaneCount;
    private const double TimelineRulerGap = EditorTimelineLayout.RulerGap;
    private const int MaxTimelineBlocks = 600;
    private const double TimelineCueEdgeGrabPixels = 6;
    private const double TimelineCueMinDuration = .2;
    private const double TimelineEffectEdgeGrabPixels = 7;
    private const double TimelineEffectMinDuration = .2;
    private const double TimelineCueVisiblePixels = 3;
    private const double TimelineClipEdgeGrabPixels = EditorTimelineTrim.EdgeGrabPixels;
    private const double TimelineMajorTickWidth = 2;
    private const double TimelineMinorTickWidth = 1;
    private const double TimelineMajorTickHeight = 11;
    private const double TimelineMinorTickHeight = 5;
    private const double TimelineRulerLabelFontSize = 9;
    private const double TimelineLabelCharacterWidth = 5.4;
    private const double TimelineLabelOverlapPadding = 4;
    private const double TimelineClipAccentWidth = 3;
    private const int TimelineClipDragZIndex = 100;
    private const double TimelinePlayheadDragThresholdPixels = 2;

    private double _timelinePixelsPerSecond = 10;
    private double _timelineScrollOffset;
    private bool _syncingTimelineZoom;
    private bool _timelineRenderQueued;
    private bool _timelineSelectionHooked;
    private bool _timelineFitPending;
    private double? _timelinePendingScrollTime;
    private double? _timelinePendingScrollOffset;
    private int _timelineDragCueIndex = -1;
    private EditorCueEdge _timelineDragEdge;
    private double _timelineDragOriginContentX;
    private double _timelineDragOriginStart;
    private double _timelineDragOriginEnd;
    private double _timelineDragNewStart;
    private double _timelineDragNewEnd;
    private double _timelineDragOriginScreenX;
    private FrameworkElement? _timelineDragBlock;
    private int _timelineDragRegionIndex = -1;
    private EditorCueEdge _timelineDragRegionEdge;
    private EditRegion? _timelineDragRegionOrigin;
    private double _timelineDragRegionOriginContentX;
    private double _timelineDragRegionOriginScreenX;
    private double _timelineDragRegionOriginStart;
    private double _timelineDragRegionOriginEnd;
    private double _timelineDragRegionNewStart;
    private double _timelineDragRegionNewEnd;
    private TimelineEffectBlock? _timelineDragRegionBlock;
    private IReadOnlyList<EditorTimelineClip> _timelineClips = [];
    private readonly EditorTimelineHistory _timelineHistory = new();
    private int _timelineSelectedClipIndex = -1;
    private int _timelineDragClipIndex = -1;
    private TimelineClipDragMode _timelineDragClipMode = TimelineClipDragMode.None;
    private int _timelineDragClipMoveIndex = -1;
    private bool _timelineDragClipMoveActive;
    private EditorTimelineTrimEdge _timelineDragClipEdge;
    private double _timelineDragClipOriginContentX;
    private double _timelineDragClipOriginScreenX;
    private double _timelineDragClipOriginSourceIn;
    private double _timelineDragClipOriginDuration;
    private double _timelineDragClipAppliedDelta;
    private double _timelineDragClipNewSourceIn;
    private double _timelineDragClipNewDuration;
    private FrameworkElement? _timelineDragClipBlock;
    private double _timelineDragClipOriginLeft;
    private readonly List<(int Index, TimelineClipBlock Block)> _timelineClipBlocks = [];
    private IReadOnlyList<double> _timelineDragSnapCandidates = [];
    private List<double> _timelineSnapMarkers = [];
    private double _timelineSeenPlayhead = double.NaN;
    private int _timelineHoverClipIndex = -1;
    private EditorTimelineTrimEdge? _timelineHoverClipEdge;
    private bool _timelinePlayheadDragging;
    private bool _timelinePlayheadDragMoved;
    private double _timelinePlayheadDragTime;
    private double _timelinePlayheadDragOriginX;
    private double _timelinePlayheadDragOriginScreenX;
    private UIElement? _timelinePlayheadDragCapture;
    private EditorVoiceWaveform? _voiceWaveform;
    private string? _voiceWaveformSource;
    private bool _voiceWaveformBuildRunning;

    private static readonly SolidColorBrush TimelineTickBrush = TimelineBrush(255, 96, 110, 130);
    private static readonly SolidColorBrush TimelineRulerBrush = TimelineBrush(255, 22, 29, 42);
    private static readonly SolidColorBrush TimelineMajorTickBrush = TimelineBrush(255, 176, 194, 218);
    private static readonly SolidColorBrush TimelineLabelBrush = TimelineBrush(255, 206, 216, 232);
    private static readonly SolidColorBrush TimelineGridBrush = TimelineBrush(44, 120, 150, 190);
    private static readonly SolidColorBrush TimelineLaneBrush = TimelineBrush(255, 34, 45, 63);
    private static readonly SolidColorBrush TimelineLaneAltBrush = TimelineBrush(255, 25, 33, 47);
    private static readonly SolidColorBrush TimelineLaneSeparatorBrush = TimelineBrush(255, 99, 126, 163);
    private static readonly SolidColorBrush TimelineLaneSeparatorShadowBrush = TimelineBrush(170, 8, 12, 20);
    private static readonly SolidColorBrush TimelineCueBrush = TimelineBrush(255, 66, 130, 245);
    private static readonly SolidColorBrush TimelineCueMissingBrush = TimelineBrush(255, 100, 112, 132);
    private static readonly SolidColorBrush TimelineCueSelectedBrush = TimelineBrush(255, 255, 255, 255);
    private static readonly SolidColorBrush TimelineVoiceBrush = TimelineBrush(255, 34, 197, 94);
    private static readonly SolidColorBrush TimelineVoiceReviewBrush = TimelineBrush(255, 239, 68, 68);
    private static readonly SolidColorBrush TimelineWaveformBrush = TimelineBrush(215, 4, 26, 14);
    private static readonly SolidColorBrush TimelineTrimShadeBrush = TimelineBrush(140, 10, 16, 26);
    private static readonly SolidColorBrush TimelineSummaryBrush = TimelineBrush(255, 72, 86, 108);
    private static readonly SolidColorBrush TimelineClipBrush = TimelineBrush(255, 45, 212, 191);
    private static readonly SolidColorBrush TimelineClipActiveBrush = TimelineBrush(255, 94, 232, 213);
    private static readonly SolidColorBrush TimelineClipSelectedBrush = TimelineBrush(255, 125, 211, 252);
    private static readonly SolidColorBrush TimelineClipSelectedBorderBrush = TimelineBrush(255, 255, 255, 255);
    private static readonly SolidColorBrush TimelineClipAccentBrush = TimelineBrush(255, 13, 148, 136);
    private static readonly InputCursor? TimelineEdgeResizeCursor = CreateTimelineEdgeResizeCursor();

    private static SolidColorBrush TimelineBrush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    /// <summary>
    /// The horizontal resize cursor for the clip edge grab zone (like
    /// FilmCraft: a vertical edge uses the horizontal drag arrow).
    /// </summary>
    private static InputCursor? CreateTimelineEdgeResizeCursor()
    {
        try
        {
            return InputCursor.CreateFromCoreCursor(new CoreCursor(CoreCursorType.SizeWestEast, 0));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A clip block on the timeline: a Grid layer with a transparent
    /// background to catch the mouse and change the cursor (WinUI's
    /// ProtectedCursor is protected), containing a rounded-corner frame, a bar
    /// marking the content area and a file-name label.
    /// </summary>
    private sealed class TimelineClipBlock : Grid
    {
        private readonly Canvas _thumbCanvas;
        private readonly Border _surface;
        private readonly Rectangle _accent;
        private readonly TextBlock _label;

        public TimelineClipBlock()
        {
            Background = TimelineBrush(0, 0, 0, 0);
            _thumbCanvas = new Canvas
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Children.Add(_thumbCanvas);
            _surface = new Border
            {
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Children.Add(_surface);
            _accent = new Rectangle
            {
                Width = TimelineClipAccentWidth,
                Fill = TimelineClipAccentBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(2, 4, 0, 4),
                IsHitTestVisible = false,
            };
            Children.Add(_accent);
            _label = new TextBlock
            {
                FontSize = 9,
                Foreground = TimelineBrush(255, 8, 47, 55),
                Margin = new Thickness(7, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                IsTextScaleFactorEnabled = false,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Children.Add(_label);
        }

        /// <summary>The block's cursor (null = the default cursor).</summary>
        public InputCursor? EdgeCursor
        {
            get => ProtectedCursor;
            set => ProtectedCursor = value;
        }

        /// <summary>The clip's label.</summary>
        public TextBlock Label => _label;

        /// <summary>
        /// Tiles the clip's thumbnail across the visible part of the block.
        /// WinUI has no tiled brush, so every tile is its own Image on this
        /// clipped canvas and the element count stays bounded by the viewport
        /// no matter how wide the clip is. A null source hides the filmstrip.
        /// </summary>
        public void SetThumbTiles(ImageSource? source, double tileWidth, double fromX, double toX)
        {
            _thumbCanvas.Children.Clear();
            _thumbCanvas.Clip = null;
            _thumbCanvas.Visibility = Visibility.Collapsed;
            if (source is null || tileWidth <= 0 || toX <= fromX || Width <= 0 || Height <= 0) return;
            var firstTile = Math.Max(0, (int)Math.Floor(fromX / tileWidth));
            var lastTile = Math.Min((int)Math.Ceiling(Width / tileWidth), (int)Math.Ceiling(toX / tileWidth));
            if (firstTile >= lastTile) return;
            _thumbCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) };
            for (var tile = firstTile; tile < lastTile; tile++)
            {
                var image = new Image
                {
                    Source = source,
                    Width = tileWidth,
                    Height = Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(image, tile * tileWidth);
                _thumbCanvas.Children.Add(image);
            }
            _thumbCanvas.Visibility = Visibility.Visible;
        }

        /// <summary>The background of the clip block.</summary>
        public Brush SurfaceBackground
        {
            get => _surface.Background;
            set => _surface.Background = value;
        }

        /// <summary>The border of the clip block (used for the selected clip).</summary>
        public Brush SurfaceBorderBrush
        {
            get => _surface.BorderBrush;
            set => _surface.BorderBrush = value;
        }

        /// <summary>The border thickness of the clip block.</summary>
        public Thickness SurfaceBorderThickness
        {
            get => _surface.BorderThickness;
            set => _surface.BorderThickness = value;
        }
    }

    /// <summary>
    /// An effect-region block on the timeline: a transparent hit-test grid holding
    /// the colored surface and two visible edge grips. The grips are display-only
    /// (the whole block catches the pointer) so grabbing either edge never depends
    /// on hitting a 3 px child element.
    /// </summary>
    private sealed class TimelineEffectBlock : Grid
    {
        private readonly Border _surface;
        private readonly Rectangle _leftGrip;
        private readonly Rectangle _rightGrip;

        public TimelineEffectBlock()
        {
            Background = TimelineBrush(0, 0, 0, 0);
            _surface = new Border
            {
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Children.Add(_surface);
            _leftGrip = CreateGrip(HorizontalAlignment.Left);
            _rightGrip = CreateGrip(HorizontalAlignment.Right);
            Children.Add(_leftGrip);
            Children.Add(_rightGrip);
            HighlightEdge(null);
        }

        private static Rectangle CreateGrip(HorizontalAlignment alignment) => new()
        {
            Width = 3,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(1, 3, 1, 3),
            Fill = TimelineBrush(220, 255, 255, 255),
            RadiusX = 1.5,
            RadiusY = 1.5,
            IsHitTestVisible = false,
        };

        /// <summary>The block's cursor (null = the default cursor).</summary>
        public InputCursor? EdgeCursor
        {
            get => ProtectedCursor;
            set => ProtectedCursor = value;
        }

        public Brush? SurfaceBackground
        {
            get => _surface.Background;
            set => _surface.Background = value;
        }

        public Brush? SurfaceBorderBrush
        {
            get => _surface.BorderBrush;
            set => _surface.BorderBrush = value;
        }

        public Thickness SurfaceBorderThickness
        {
            get => _surface.BorderThickness;
            set => _surface.BorderThickness = value;
        }

        /// <summary>Highlights the edge the pointer is over (or the edge being dragged).</summary>
        public void HighlightEdge(EditorCueEdge? edge)
        {
            _leftGrip.Opacity = edge == EditorCueEdge.Start ? 1 : .5;
            _rightGrip.Opacity = edge == EditorCueEdge.End ? 1 : .5;
        }
    }

    private void InitializeTimeline()
    {
        if (_timelineSelectionHooked) return;
        _timelineSelectionHooked = true;
        SubtitleCueList.SelectionChanged += TimelineCueListSelectionChanged;
        // Do not use the SizeChanged attribute in XAML: the repo forbids layout
        // mutation from SizeChanged. Subscribe in code with a deferred dispatch to avoid re-entrancy.
        TimelineScrollViewer.SizeChanged += TimelineViewportResized;
        TimelineScrollViewer.PointerWheelChanged += TimelineScrollViewer_PointerWheelChanged;
        // The playhead head is a drag handle: hovering it with the mouse switches the
        // cursor to the horizontal drag arrow, just like trimming a clip on the timeline.
        // The playhead head's width equals Core's grab zone exactly: the round button
        // control is the real hit-test area, and if it were narrower than the grab
        // zone the rest of that zone would fall onto the ruler and break the drag start.
        if (TimelinePlayheadHead is not null)
        {
            TimelinePlayheadHead.DragCursor = TimelineEdgeResizeCursor;
            TimelinePlayheadHead.Width = EditorTimelinePlayhead.GrabZoneWidth;
        }
        SyncTimelineHeaderRows();
        // WinUI attaches the ValueChanged handler AFTER loading XAML, so Value="35" in
        // XAML never reaches the code. Set the zoom here so the state is always
        // deterministic and independent of the XAML load order.
        ApplyTimelineZoom(DefaultTimelineZoom);
    }

    private const double DefaultTimelineZoom = 35;
    private const double ReadableTimelinePixelsPerSecond = 8;

    /// <summary>
    /// The lane-label column must match the lane heights in the Canvas exactly,
    /// otherwise the text drifts lane by lane (each lane offset by exactly the
    /// distance between lanes). Each lane in the label column has a spare 3 px
    /// row equal to the gap between two lanes on the timeline, so each track
    /// name always stays centered on its own row.
    /// </summary>
    private void SyncTimelineHeaderRows()
    {
        if (TimelineHeaderGrid is null) return;
        var rows = TimelineHeaderGrid.RowDefinitions;
        if (rows.Count < 2 + TimelineLaneCount * 2 - 1) return;
        rows[0].Height = new GridLength(EditorTimelineLayout.RulerHeight);
        rows[1].Height = new GridLength(TimelineRulerGap);
        for (var lane = 0; lane < TimelineLaneCount; lane++)
        {
            rows[2 + lane * 2].Height = new GridLength(EditorTimelineLayout.LaneHeight);
            var gapRow = 3 + lane * 2;
            if (gapRow < rows.Count) rows[gapRow].Height = new GridLength(EditorTimelineLayout.LaneGap);
        }
    }

    /// <summary>
    /// Opening a new source/project: the timeline must fit the viewport and
    /// return to the start. Otherwise the ruler keeps the old project's zoom
    /// and shows the wrong range (a 9-hour video looks like a 3-hour one).
    /// </summary>
    private void ResetTimelineForNewSource()
    {
        _timelineFitPending = true;
        _timelineScrollOffset = 0;
        _timelineDragCueIndex = -1;
        _timelineDragBlock = null;
        // New project: the old timeline's undo/redo history does not apply to the new one.
        _timelineHistory.Clear();
        RefreshTimelineClipTools();
        // New project: drop the pending playhead save of the old project.
        ClearPlayheadSave();
        ApplyTimelineComposition();
        ApplyTimelineStepFrequency();
        ApplyTimelineFit();
        _timelineSeenPlayhead = Timeline.Value;
        RestoreTimelinePlayhead();
    }

    /// <summary>
    /// The scrub slider's step grid must be the media's frame grid: WinUI itself
    /// rounds the value when the user drags the thumb along StepFrequency, so
    /// setting the step to 1/fps makes dragging land on real frames. The grid
    /// comes from the clip currently containing the playhead, falling back to a
    /// fine 1ms grid when unknown; frame snapping itself is Core's job.
    /// </summary>
    private void ApplyTimelineStepFrequency()
    {
        var frameRate = TimelineFrameRateAt(Timeline.Value);
        Timeline.StepFrequency = frameRate > 0 ? 1d / frameRate : 0.001d;
        // The left/right arrow keys are owned by the page's shortcut system (exact
        // frame counts from the real fps), so the slider must not jump on its own
        // via SmallChange/LargeChange: enabling them would apply the key twice.
        Timeline.SmallChange = 0d;
        Timeline.LargeChange = 0d;
    }

    /// <summary>
    /// Restores the playhead position saved in the project (FilmCraft saves the
    /// playhead per project): the saved value is kept as-is when it lies on the
    /// frame grid of the clip containing the playhead. When that clip's fps is
    /// unknown, do NOT use the project media's fps: two different frame grids
    /// shift the saved position by about one frame (5s at 24fps becomes 4.97s at 29.97fps).
    /// </summary>
    private void RestoreTimelinePlayhead()
    {
        var saved = EditorProjectStore.NormalizePlayhead(_project?.PlayheadSeconds);
        if (saved <= 0) return;
        var duration = TimelineWorkingDuration;
        var target = Math.Clamp(
            EditorTimelinePlayhead.RestoreTime(saved),
            0,
            Math.Max(0, duration));
        _syncingTimeline = true;
        try { Timeline.Value = target; }
        finally { _syncingTimeline = false; }
        _timelineSeenPlayhead = target;
        UpdateClock();
        RevealTimelinePlayhead(target);
    }

    /// <summary>
    /// The fps of the clip containing a position on the timeline; null when that
    /// clip's fps is unknown (its media has not finished probing).
    /// </summary>
    /// <param name="timelineTime">Time on the composed timeline (seconds).</param>
    private double? TimelineClipFrameRateAt(double timelineTime)
    {
        if (_timelineClips.Count == 0) return null;
        var (clip, _, _) = LocateTimelineClip(timelineTime);
        var media = TimelineClipMediaCached(clip);
        if (media is { FrameRate: > 0 }) return media.FrameRate;
        // The clip has not been probed yet: if it is the project's source file, use
        // the already-probed media (same file means same fps).
        if (IsProjectSourceFile(clip) && _media is { FrameRate: > 0 }) return _media.FrameRate;
        return null;
    }

    /// <summary>
    /// The frames per second of the frame grid at a position on the timeline:
    /// always prefer the fps of the clip containing the playhead, falling back
    /// to the project media only when that clip is unknown, and to FilmCraft's default 30 fps otherwise.
    /// </summary>
    /// <param name="timelineTime">Time on the composed timeline (seconds).</param>
    private double TimelineFrameRateAt(double timelineTime)
        => EditorTimelinePlayhead.ResolveFrameRate(TimelineClipFrameRateAt(timelineTime), _media?.FrameRate);

    /// <summary>
    /// Snaps the playhead to the frame grid of the media being scrubbed: snap
    /// time = frame index / fps, not a free real value.
    /// </summary>
    /// <param name="timelineTime">Time on the composed timeline (seconds).</param>
    private double SnapTimelinePlayhead(double timelineTime)
    {
        if (!double.IsFinite(timelineTime)) return 0;
        return EditorTimelinePlayhead.SnapToFrame(timelineTime, TimelineFrameRateAt(timelineTime));
    }

    /// <summary>
    /// Brings the playhead into view when it lies outside (auto-follow, as in
    /// FilmCraft). Used when opening a project: the saved playhead is scrolled
    /// into view whether or not the previous position already shared the same frame.
    /// </summary>
    /// <param name="timelineTime">Playhead time on the composed timeline (seconds).</param>
    private void RevealTimelinePlayhead(double timelineTime)
    {
        if (TimelineScrollViewer is null) return;
        var viewport = TimelineViewportWidth();
        if (viewport <= 1) return;
        var pps = _timelinePixelsPerSecond;
        var duration = TimelineWorkingDuration;
        var contentWidth = EditorTimelineLayout.ContentWidth(duration, pps);
        var offset = EditorTimelinePlayhead.RevealScrollOffset(
            timelineTime,
            CurrentTimelineScrollOffset(),
            pps,
            viewport,
            contentWidth);
        if (offset is not { } target) return;
        if (Math.Abs(target - CurrentTimelineScrollOffset()) < 1) return;
        _timelinePendingScrollOffset = target;
        QueueTimelineRender();
    }

    /// <summary>
    /// The timeline follows the playhead when the user scrubs (the playhead
    /// changes frame) and the playhead lies outside the viewport. When the user
    /// scrolls by hand the playhead stays put, so the timeline is not scrolled continuously.
    /// </summary>
    /// <param name="timelineTime">Playhead time on the composed timeline (seconds).</param>
    private void FollowTimelinePlayhead(double timelineTime)
    {
        var previous = _timelineSeenPlayhead;
        _timelineSeenPlayhead = timelineTime;
        if (TimelineScrollViewer is null) return;
        var viewport = TimelineViewportWidth();
        if (viewport <= 1) return;
        var pps = _timelinePixelsPerSecond;
        var duration = TimelineWorkingDuration;
        var contentWidth = EditorTimelineLayout.ContentWidth(duration, pps);
        var offset = EditorTimelinePlayhead.FollowScrollOffset(
            previous,
            timelineTime,
            CurrentTimelineScrollOffset(),
            pps,
            viewport,
            contentWidth,
            TimelineFrameRateAt(timelineTime));
        if (offset is not { } target) return;
        if (Math.Abs(target - CurrentTimelineScrollOffset()) < 1) return;
        _timelinePendingScrollOffset = target;
        QueueTimelineRender();
    }

    /// <summary>
    /// Loads the project's clip list. An old project without a timeline gets one
    /// clip generated from the source video so the project in use is not broken.
    /// </summary>
    private void ApplyTimelineComposition()
    {
        EditorSourceFingerprint? fingerprint = _project?.Source;
        if (fingerprint is null && _media is not null)
            fingerprint = new EditorSourceFingerprint(_path ?? string.Empty, 0, 0, _media.Width, _media.Height, _media.Duration);
        _timelineClips = EditorTimelineComposition.Resolve(_project?.Timeline, fingerprint ?? default);
        _timelineSelectedClipIndex = _timelineClips.Count > 0 ? 0 : -1;
        SyncTimelineDuration();
        RefreshRegionTimeLimits();
    }

    /// <summary>Synchronizes the time axis length with the total duration of the composed timeline.</summary>
    private void SyncTimelineDuration()
    {
        if (Timeline is null) return;
        var duration = TimelineWorkingDuration;
        Timeline.Maximum = Math.Max(0.1, duration);
        UpdateClock();
    }

    /// <summary>Total duration of the composed timeline (sum of all clips).</summary>
    private double TimelineDuration => EditorTimelineComposition.TotalDuration(_timelineClips);

    /// <summary>
    /// Working extent of the strip: the composed video plus an EMPTY TAIL of
    /// the same real length (10 min of video - the timeline works until minute
    /// 20). Every consumer of the strip's EXTENT (content width, ruler range,
    /// scroll range, fit, playhead range, click-to-seek) uses this value;
    /// playback, export and the clip geometry keep <see cref="TimelineDuration"/>
    /// so the video itself is never stretched and its length never changes.
    /// </summary>
    private double TimelineWorkingDuration
        => EditorTimelineLayout.WorkingSpan(TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0);

    /// <summary>
    /// The "Fit to viewport" button: pixel density is computed from the timeline's
    /// REAL VIEWPORT WIDTH (ScrollViewer), not counting the lane-label column or the toolbar.
    /// </summary>
    private void ApplyTimelineFit()
    {
        if (TimelineScrollViewer is null) return;
        var duration = TimelineWorkingDuration;
        var viewport = TimelineViewportWidth();
        if (duration <= 0 || viewport <= 1) return;
        _timelineFitPending = false;
        var pps = EditorTimelineLayout.FitPixelsPerSecond(duration, viewport);
        ApplyTimelineZoom(TimelineZoomForPixelsPerSecond(pps));
        _timelinePendingScrollOffset = null;
        TimelineScrollViewer.ChangeView(0, null, null);
        QueueTimelineRender();
    }

    /// <summary>
    /// Sets the zoom from the slider value (0..100) and updates everything
    /// related. The anchor is the left edge of the area being viewed.
    /// </summary>
    private void ApplyTimelineZoom(double zoom)
    {
        var clamped = Math.Clamp(zoom, TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
        _syncingTimelineZoom = true;
        try { TimelineZoomSlider.Value = clamped; }
        finally { _syncingTimelineZoom = false; }
        ApplyTimelineZoomPixels(TimelinePixelsPerSecondForZoom(clamped));
    }

    /// <summary>
    /// Changes the timeline's pixel density. The default anchor is the LEFT EDGE
    /// of the area being viewed: the time at the left edge (right beside the
    /// ruler) stays fixed across every zoom level and the timeline only grows
    /// or shrinks to the right (like Premiere/Resolve/CapCut and FilmCraft's
    /// zoom bar). Alt/Ctrl + wheel anchors on the cursor instead (pass
    /// anchorContentX). The slider value is not reassigned here because WinUI loading XAML assigns Value and calls the handler again.
    /// </summary>
    /// <param name="newPps">New pixel density (px/second).</param>
    /// <param name="anchorContentX">Horizontal coordinate within the content of the anchor; defaults to the viewport's left edge.</param>
    private void ApplyTimelineZoomPixels(double newPps, double? anchorContentX = null)
    {
        var oldPps = EditorTimelineLayout.ClampPixelsPerSecond(_timelinePixelsPerSecond);
        newPps = EditorTimelineLayout.ClampPixelsPerSecond(newPps);
        var scrollOffset = CurrentTimelineScrollOffset();
        var viewport = TimelineViewportWidth();

        _timelinePixelsPerSecond = newPps;
        _syncingTimelineZoom = true;
        try { TimelineZoomSlider.Value = TimelineZoomForPixelsPerSecond(newPps); }
        finally { _syncingTimelineZoom = false; }
        UpdateTimelineZoomText();

        // Keeps the anchor still: when pixel density changes, the scroll offset
        // must compensate exactly by how far the anchor moved within the content.
        var duration = TimelineWorkingDuration;
        if (viewport > 1 && oldPps > 0)
        {
            var contentWidth = EditorTimelineLayout.ContentWidth(duration, newPps);
            // The anchor keeps its exact screen coordinate: the slider and the +/-
            // buttons anchor the viewport's left edge, Alt/Ctrl + wheel anchors the cursor.
            // Zooming does NOT scroll the timeline to the playhead (FilmCraft does not
            // either): otherwise the anchor would jump and zooming would feel wrong.
            var target = anchorContentX is { } anchor
                ? EditorTimelineLayout.ZoomAnchorOffset(anchor, oldPps, newPps, scrollOffset, contentWidth, viewport)
                : EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft(oldPps, newPps, scrollOffset, contentWidth, viewport);
            _timelinePendingScrollOffset = target;
        }
        else
        {
            _timelinePendingScrollOffset = null;
        }
        QueueTimelineRender();
    }

    /// <summary>Width of the timeline's viewport (px), 0 when layout has not run yet.</summary>
    private double TimelineViewportWidth()
        => TimelineScrollViewer is null
            ? 0
            : (TimelineScrollViewer.ViewportWidth > 1 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth);

    /// <summary>
    /// Alt or Ctrl + wheel over the timeline zooms anchored on the cursor (as in
    /// FilmCraft). Alt + wheel is FilmCraft's built-in mechanism, Ctrl + wheel
    /// is the page's own shortcut: both share the same cursor-anchored zoom
    /// path. A plain wheel still scrolls the timeline as usual.
    /// </summary>
    private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        var point = e.GetCurrentPoint(viewer);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        var zoomAboutPointer = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu)
            || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        if (!zoomAboutPointer) return;
        var pps = _timelinePixelsPerSecond;
        if (pps <= 0) return;
        e.Handled = true;
        var notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);
        var steps = Math.Max(-3, Math.Min(3, notches));
        var factor = steps > 0 ? TimelineWheelZoomFactor : 1 / TimelineWheelZoomFactor;
        var newPps = Math.Clamp(
            pps * Math.Pow(factor, Math.Abs(steps)),
            EditorTimelineLayout.MinPixelsPerSecond,
            EditorTimelineLayout.MaxPixelsPerSecond);
        var scrollOffset = CurrentTimelineScrollOffset();
        var pointerX = Math.Max(0, point.Position.X);
        // Both inputs are pixels: the pointer is measured from the viewport's
        // left edge, so adding the hidden content offset gives the exact moment
        // under the mouse. Dividing by pps here would compare seconds to pixels.
        var anchorContentX = EditorTimelineLayout.ZoomAnchorContentX(scrollOffset, pointerX);
        ApplyTimelineZoomPixels(newPps, anchorContentX);
    }

    private const double TimelineWheelZoomFactor = 1.2;



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
        EnsureVoiceWaveformForRender();
        if (_timelineRenderQueued) return;
        _timelineRenderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _timelineRenderQueued = false;
            RenderTimeline();
        });
    }

    /// <summary>
    /// Keeps the voice waveform of the timeline in sync with the current voice
    /// track: a new voice file invalidates the cached envelope, the decode runs
    /// off the UI thread (ffmpeg + disk cache) and the finished peaks trigger
    /// exactly one more render.
    /// </summary>
    private void EnsureVoiceWaveformForRender()
    {
        var source = _voiceTrack?.Path;
        if (string.IsNullOrWhiteSpace(source))
        {
            _voiceWaveform = null;
            _voiceWaveformSource = null;
            return;
        }
        if (_voiceWaveformBuildRunning) return;
        if (string.Equals(_voiceWaveformSource, source, StringComparison.OrdinalIgnoreCase)) return;
        // No ffmpeg yet: wait for a later render instead of starting a build
        // that could never finish (each attempt would requeue the next one).
        if (!_application.Tools.Status.FfmpegReady) return;
        _voiceWaveformBuildRunning = true;
        _ = BuildVoiceWaveformAsync(source);
    }

    private async Task BuildVoiceWaveformAsync(string source)
    {
        EditorVoiceWaveform? built = null;
        try
        {
            built = await EditorVoiceWaveform.LoadOrBuildAsync(
                _application.Tools, _application.Processes, source, CancellationToken.None);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("timeline-voice-waveform: " + error);
        }
        if (!DispatcherQueue.TryEnqueue(() => FinishVoiceWaveformBuild(source, built)))
            _voiceWaveformBuildRunning = false;
    }

    private void FinishVoiceWaveformBuild(string source, EditorVoiceWaveform? built)
    {
        _voiceWaveformBuildRunning = false;
        if (!string.Equals(_voiceTrack?.Path, source, StringComparison.OrdinalIgnoreCase))
        {
            // The voice changed while decoding: settle the new track instead.
            QueueTimelineRender();
            return;
        }
        _voiceWaveform = built;
        _voiceWaveformSource = source;
        QueueTimelineRender();
    }

    private void RenderTimeline()
    {
        if (TimelineContentGrid is null || TimelineRulerCanvas is null || TimelineTracksCanvas is null) return;
        var duration = TimelineWorkingDuration;
        var pps = _timelinePixelsPerSecond;
        // Timeline content width = duration x pixel density; it can be smaller than
        // the viewport.
        var contentWidth = EditorTimelineLayout.ContentWidth(duration, pps);
        // Content canvas width: a timeline shorter than the viewport still spans
        // the full viewport width so no white gap is left on the left (the
        // viewport scrolls left; the content is not centered).
        var fillWidth = EditorTimelineLayout.FillWidth(contentWidth, TimelineViewportWidth());
        var tracksHeight = EditorTimelineLayout.TracksHeight(TimelineLaneCount);
        var contentHeight = EditorTimelineLayout.RulerHeight + TimelineRulerGap + tracksHeight;

        // Only assign when the value really differs: otherwise the ScrollViewer's
        // SizeChanged drives an infinite layout loop.
        if (Math.Abs(TimelineContentGrid.Width - fillWidth) > .5) TimelineContentGrid.Width = fillWidth;
        if (Math.Abs(TimelineContentGrid.Height - contentHeight) > .5) TimelineContentGrid.Height = contentHeight;
        if (Math.Abs(TimelineRulerCanvas.Width - fillWidth) > .5) TimelineRulerCanvas.Width = fillWidth;
        if (Math.Abs(TimelineTracksCanvas.Width - fillWidth) > .5) TimelineTracksCanvas.Width = fillWidth;
        Canvas.SetTop(TimelineRulerCanvas, 0);
        Canvas.SetTop(TimelineTracksCanvas, EditorTimelineLayout.RulerHeight + TimelineRulerGap);
        if (Math.Abs(TimelineTracksCanvas.Height - tracksHeight) > .5) TimelineTracksCanvas.Height = tracksHeight;
        if (Math.Abs(TimelinePlayhead.Height - contentHeight) > .5) TimelinePlayhead.Height = contentHeight;

        var (visibleStart, visibleEnd) = EditorTimelineLayout.VisibleWindow(
            CurrentTimelineScrollOffset(),
            TimelineViewportWidth(),
            pps,
            duration);

        RenderTimelineRuler(fillWidth, duration, pps, visibleStart, visibleEnd);
        RenderTimelineLanes(fillWidth, pps, visibleStart, visibleEnd);
        UpdateTimelinePlayhead();
        UpdateTimelineZoomText();
        ApplyPendingTimelineScroll();
    }

    private void RenderTimelineRuler(double fillWidth, double duration, double pps, double visibleStart, double visibleEnd)
    {
        TimelineRulerCanvas.Children.Clear();

        // The ruler background spans the timeline's full width (including the part
        // outside the real content) so the viewport's right edge is never left
        // without a background; the ticks still mark real time only.
        var band = new Rectangle
        {
            Width = fillWidth,
            Height = EditorTimelineLayout.RulerHeight,
            Fill = TimelineRulerBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(band, 0);
        Canvas.SetTop(band, 0);
        TimelineRulerCanvas.Children.Add(band);

        // Separator line under the ruler band: 1 px of light color so the ruler
        // stands out from the lane area, in the style of professional rulers.
        var rulerEdge = new Rectangle
        {
            Width = fillWidth,
            Height = 1,
            Fill = TimelineLaneSeparatorBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rulerEdge, 0);
        Canvas.SetTop(rulerEdge, EditorTimelineLayout.RulerHeight - 1);
        TimelineRulerCanvas.Children.Add(rulerEdge);

        // The part cut out of the export is shaded so the kept part is clearly
        // visible. Kept ranges live in the original clip's source-file
        // coordinates, so they must be mapped to composed-timeline coordinates first.
        foreach (var range in TrimExcludedTimelineRanges())
        {
            var shade = new Rectangle
            {
                Width = Math.Max(1, EditorTimelineLayout.XForTime(range.End - range.Start, pps)),
                Height = EditorTimelineLayout.RulerHeight,
                Fill = TimelineTrimShadeBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shade, EditorTimelineLayout.XForTime(range.Start, pps));
            Canvas.SetTop(shade, 0);
            TimelineRulerCanvas.Children.Add(shade);
        }

        var major = EditorTimelineLayout.MajorTickSeconds(pps);
        var minor = Math.Max(.01, EditorTimelineLayout.MinorTickSeconds(pps));
        // Time labels are only drawn when two labels still have enough spacing;
        // otherwise the label is skipped rather than drawn on top of another.
        var drawLabels = EditorTimelineLayout.LabelsFit(major, pps);
        var lastLabelRight = double.NegativeInfinity;
        var majorTimes = EditorTimelineLayout.TickTimes(visibleStart, visibleEnd, major);
        var minorTimes = EditorTimelineLayout.TickTimes(visibleStart, visibleEnd, minor);
        foreach (var time in minorTimes)
        {
            if (time < -1e-9 || time > duration + 1e-9) continue;
            if (IsMajorTimelineTick(time, major)) continue;
            var tick = new Rectangle
            {
                Width = TimelineMinorTickWidth,
                Height = TimelineMinorTickHeight,
                Fill = TimelineTickBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tick, EditorTimelineLayout.XForTime(time, pps));
            Canvas.SetTop(tick, 6);
            TimelineRulerCanvas.Children.Add(tick);
        }
        foreach (var time in majorTimes)
        {
            if (time < -1e-9 || time > duration + 1e-9) continue;
            var x = EditorTimelineLayout.XForTime(time, pps);
            var tick = new Rectangle
            {
                Width = TimelineMajorTickWidth,
                Height = TimelineMajorTickHeight,
                Fill = TimelineMajorTickBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tick, x - (TimelineMajorTickWidth - 1) / 2);
            Canvas.SetTop(tick, 1);
            TimelineRulerCanvas.Children.Add(tick);
            if (!drawLabels) continue;
            var text = FormatClock(time, duration >= 3600);
            var labelLeft = x + 3;
            var labelRight = labelLeft + text.Length * TimelineLabelCharacterWidth;
            if (labelLeft < lastLabelRight + TimelineLabelOverlapPadding) continue;
            var label = new TextBlock
            {
                Text = text,
                FontSize = TimelineRulerLabelFontSize,
                Foreground = TimelineLabelBrush,
                IsHitTestVisible = false,
                IsTextScaleFactorEnabled = false,
            };
            Canvas.SetLeft(label, labelLeft);
            Canvas.SetTop(label, 12);
            TimelineRulerCanvas.Children.Add(label);
            lastLabelRight = labelRight;
        }
    }

    /// <summary>
    /// Whether a time mark coincides with a major tick (a minor tick on a major
    /// tick is skipped so two ticks are not drawn on top of each other).
    /// </summary>
    /// <param name="time">Time mark (seconds).</param>
    /// <param name="majorSeconds">Spacing of major ticks (seconds).</param>
    private static bool IsMajorTimelineTick(double time, double majorSeconds)
        => Math.Abs(Math.Round(time / majorSeconds) * majorSeconds - time) <= 1e-6;

    private void RenderTimelineLanes(double fillWidth, double pps, double visibleStart, double visibleEnd)
    {
        // Never rebuild the lane while dragging a clip edge: the dragged block
        // would lose pointer capture and jump back to its old position mid-drag.
        if (_timelineDragClipIndex >= 0) return;
        TimelineTracksCanvas.Children.Clear();
        _timelineClipBlocks.Clear();
        for (var lane = 0; lane < TimelineLaneCount; lane++)
        {
            var background = new Rectangle
            {
                Width = fillWidth,
                Height = EditorTimelineLayout.LaneHeight,
                Fill = lane % 2 == 0 ? TimelineLaneBrush : TimelineLaneAltBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(background, 0);
            Canvas.SetTop(background, EditorTimelineLayout.LaneTop(lane));
            TimelineTracksCanvas.Children.Add(background);
        }

        RenderTimelineGridLines(pps, visibleStart, visibleEnd);

        // Lane separator: drawn in the gap between two lanes (the lane border) so
        // the user can clearly see which lane is Video, Voice, Subtitles and
        // Effects. The line lies outside the lane band so it does not cover
        // clips/cues/markers. Below the light line there is also 1 px of dark color so the lane edge stands out on alternating light/dark backgrounds.
        for (var lane = 1; lane < TimelineLaneCount; lane++)
        {
            var separator = new Rectangle
            {
                Width = fillWidth,
                Height = 1,
                Fill = TimelineLaneSeparatorBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(separator, 0);
            Canvas.SetTop(separator, EditorTimelineLayout.LaneTop(lane) - EditorTimelineLayout.LaneGap);
            TimelineTracksCanvas.Children.Add(separator);
            var shadow = new Rectangle
            {
                Width = fillWidth,
                Height = 1,
                Fill = TimelineLaneSeparatorShadowBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shadow, 0);
            Canvas.SetTop(shadow, EditorTimelineLayout.LaneTop(lane) - EditorTimelineLayout.LaneGap + 1);
            TimelineTracksCanvas.Children.Add(shadow);
        }

        RenderTimelineEffectBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineSubtitleBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineVideoBlocks(pps, visibleStart, visibleEnd);
        RenderTimelineVoiceBlocks(pps, visibleStart, visibleEnd);
        _timelineSnapMarkers = CollectTimelineSnapMarkers(visibleStart, visibleEnd);
    }

    /// <summary>
    /// Vertical grid along the ruler's major ticks, extended down into the
    /// track area so the user can align clips to time marks. The contrast is
    /// light and only major ticks extend down (minor ticks do not), so the
    /// view stays uncluttered. The grid lies beneath clips/cues/markers so it never covers content.
    /// </summary>
    /// <param name="pps">Current pixel density (px/second).</param>
    /// <param name="visibleStart">Start of the visible range (seconds).</param>
    /// <param name="visibleEnd">End of the visible range (seconds).</param>
    private void RenderTimelineGridLines(double pps, double visibleStart, double visibleEnd)
    {
        var major = EditorTimelineLayout.MajorTickSeconds(pps);
        var height = EditorTimelineLayout.TracksHeight(TimelineLaneCount);
        foreach (var time in EditorTimelineLayout.TickTimes(visibleStart, visibleEnd, major))
        {
            var line = new Rectangle
            {
                Width = 1,
                Height = height,
                Fill = TimelineGridBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(line, EditorTimelineLayout.XForTime(time, pps));
            Canvas.SetTop(line, 0);
            TimelineTracksCanvas.Children.Add(line);
        }
    }

    /// <summary>
    /// Video lane: each clip is one block on the composed timeline. Click a
    /// block to select that clip (used for split/remove), and the clip
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
            var width = EditorTimelineLayout.ClipWidth(clip.Duration, pps);
            var isSelected = index == _timelineSelectedClipIndex;
            var isPlayheadClip = clip.Id == playheadClip;
            var block = new TimelineClipBlock
            {
                Width = width,
                Height = EditorTimelineLayout.LaneHeight - 4,
            };
            var thumbnail = TimelineClipThumbnail(clip);
            var surfaceTint = isSelected
                ? TimelineClipSelectedBrush
                : isPlayheadClip ? TimelineClipActiveBrush : TimelineClipBrush;
            block.SurfaceBackground = thumbnail is null
                ? surfaceTint
                : TimelineTranslucent(surfaceTint, 130);
            block.SurfaceBorderBrush = isSelected ? TimelineClipSelectedBorderBrush : TimelineBrush(0, 0, 0, 0);
            block.SurfaceBorderThickness = new Thickness(isSelected ? 2 : 0);
            if (width >= 46)
            {
                block.Label.Text = TimelineClipDisplayName(clip);
                if (HasClipSubtitle(clip.Id)) block.Label.Text = "SRT · " + block.Label.Text;
            }
            var blockLeft = EditorTimelineLayout.XForTime(timelineStart, pps);
            block.SetThumbTiles(thumbnail, TimelineThumbTileWidth(thumbnail),
                Math.Max(0, EditorTimelineLayout.XForTime(visibleStart, pps) - blockLeft),
                Math.Min(width, EditorTimelineLayout.XForTime(visibleEnd, pps) - blockLeft));
            Canvas.SetLeft(block, blockLeft);
            Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneVideo) + 2);
            ToolTipService.SetToolTip(block,
                $"{clip.Label} · {FormatClock(clip.Duration)} · nguồn {clip.SourcePath}\n"
                + "Kéo mép trái để cắt đầu đoạn, kéo mép phải để cắt cuối đoạn.\n"
                + "Nhấn Delete để xoá đoạn đang chọn.");
            var captured = index;
            block.PointerPressed += (_, args) => TimelineVideoClipPressed(captured, block, args);
            block.PointerMoved += (_, args) => TimelineVideoClipPointerMoved(captured, block, args);
            block.PointerReleased += (_, args) => TimelineVideoClipPointerReleased(captured, block, args);
            block.PointerCanceled += (_, args) => TimelineVideoClipPointerCanceled(captured, block, args);
            block.PointerMoved += (_, args) => TimelineVideoClipHoverMoved(captured, block, args);
            block.PointerExited += (_, args) => TimelineVideoClipHoverExited(captured, block, args);
            TimelineTracksCanvas.Children.Add(block);
            _timelineClipBlocks.Add((captured, block));
            PrefetchTimelineClipMedia(clip);
            PrefetchTimelineClipThumbnail(clip);
        }
    }

    /// <summary>The same clip tint at a lower alpha, so a thumbnail stays visible under the selection state.</summary>
    private static SolidColorBrush TimelineTranslucent(Brush brush, byte alpha)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>Natural filmstrip tile width: the thumbnail at the block's full height, at its own aspect ratio.</summary>
    private static double TimelineThumbTileWidth(BitmapImage? thumbnail)
    {
        var height = EditorTimelineLayout.LaneHeight - 4;
        if (thumbnail is null || thumbnail.PixelHeight <= 0) return height * 16 / 9.0;
        return Math.Max(1, height * thumbnail.PixelWidth / (double)thumbnail.PixelHeight);
    }

    /// <summary>
    /// The display name on a clip block: that segment's source file name (an
    /// empty label falls back to the file name from the path), shortened so it does not overflow the block.
    /// </summary>
    /// <param name="clip">Clip to display on the timeline.</param>
    private static string TimelineClipDisplayName(EditorTimelineClip clip)
    {
        var label = string.IsNullOrWhiteSpace(clip.Label) ? Path.GetFileName(clip.SourcePath) : clip.Label;
        return TrimPreview(label);
    }

    private void TimelineVideoClipPressed(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (index < 0 || index >= _timelineClips.Count) return;
        var clip = _timelineClips[index];
        _timelineSelectedClipIndex = index;
        var width = block.ActualWidth;
        var position = args.GetCurrentPoint(block).Position;
        EditorTimelineTrimEdge? edge = null;
        if (TimelineContentGrid is not null && EditorTimelineTrim.CanGrabEdge(width, TimelineClipEdgeGrabPixels))
        {
            edge = EditorTimelineTrim.EdgeAt(position.X, width, TimelineClipEdgeGrabPixels);
        }
        if (edge is null)
        {
            // Clicking a clip body: only selects the clip and prepares a reorder drag.
            // The drag starts only once the cursor passes the threshold, so a light
            // click still only selects. The event is swallowed here, so clicking a
            // clip body does NOT scrub the playhead: only clicking the ruler or an empty area scrubs.
            args.Handled = true;
            StartTimelineClipMoveDrag(index, block, args);
            UpdateTimelineClipSelection();
            QueueTimelineRender();
            StatusText.Text = $"Đang chọn đoạn “{clip.Label}” ({FormatClock(clip.Duration)}). "
                + "Kéo thân clip để đổi thứ tự, dùng Tách/Xoá hoặc phím Delete để chỉnh dải ghép.";
            return;
        }
        args.Handled = true;
        _timelineDragClipMode = TimelineClipDragMode.Edge;
        _timelineDragClipIndex = index;
        _timelineDragClipEdge = edge.Value;
        _timelineDragClipOriginSourceIn = clip.SourceIn;
        _timelineDragClipOriginDuration = clip.Duration;
        _timelineDragClipNewSourceIn = clip.SourceIn;
        _timelineDragClipNewDuration = clip.Duration;
        _timelineDragClipAppliedDelta = 0;
        _timelineDragClipOriginContentX = args.GetCurrentPoint(TimelineContentGrid).Position.X;
        _timelineDragClipOriginScreenX = Win32Cursor.TryGetScreenPosition(out var screenX, out _) ? screenX : 0d;
        _timelineDragClipBlock = block;
        _timelineDragSnapCandidates = TimelineSnapCandidates(index);
        block.CapturePointer(args.Pointer);
        // While dragging an edge, the cursor-following highlight only adds noise,
        // and the resize cursor is kept unchanged.
        HideTimelineEdgeHover();
        block.EdgeCursor = TimelineEdgeResizeCursor;
        StatusText.Text = edge == EditorTimelineTrimEdge.In
            ? $"Kéo mép trái để cắt đầu đoạn “{clip.Label}” trong file nguồn."
            : $"Kéo mép phải để cắt cuối đoạn “{clip.Label}” trong file nguồn.";
    }

    /// <summary>
    /// Repaints the selected clip directly on the lane: the lane must not be
    /// redrawn mid-drag (the dragged block keeps its pointer capture), so the
    /// selection border has to be updated in place.
    /// </summary>
    private void UpdateTimelineClipSelection()
    {
        if (_timelineClips.Count == 0) return;
        var playheadClip = EditorTimelineComposition.Locate(_timelineClips, Timeline.Value).Clip.Id;
        foreach (var entry in _timelineClipBlocks)
        {
            if (entry.Index < 0 || entry.Index >= _timelineClips.Count) continue;
            var selected = entry.Index == _timelineSelectedClipIndex;
            var isPlayheadClip = _timelineClips[entry.Index].Id == playheadClip;
            entry.Block.SurfaceBackground = selected
                ? TimelineClipSelectedBrush
                : isPlayheadClip ? TimelineClipActiveBrush : TimelineClipBrush;
            entry.Block.SurfaceBorderBrush = selected ? TimelineClipSelectedBorderBrush : TimelineBrush(0, 0, 0, 0);
            entry.Block.SurfaceBorderThickness = new Thickness(selected ? 2 : 0);
        }
        RefreshTimelineClipTools();
    }

    /// <summary>
    /// Starts dragging a clip body: captures the pointer and waits for the
    /// drag threshold before computing the insertion position. A light click
    /// </summary>
    /// <param name="index">Index of the clip being dragged.</param>
    /// <param name="block">Block of the clip being dragged.</param>
    /// <param name="args">The mouse-press event.</param>
    private void StartTimelineClipMoveDrag(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (TimelineContentGrid is null) return;
        _timelineDragClipMode = TimelineClipDragMode.Move;
        _timelineDragClipIndex = index;
        _timelineDragClipMoveIndex = index;
        _timelineDragClipMoveActive = false;
        _timelineDragClipOriginContentX = args.GetCurrentPoint(TimelineContentGrid).Position.X;
        _timelineDragClipOriginScreenX = Win32Cursor.TryGetScreenPosition(out var screenX, out _) ? screenX : 0d;
        _timelineDragClipOriginLeft = Canvas.GetLeft(block);
        _timelineDragClipBlock = block;
        // The dragged clip sits above the other clips so the user can see it
        // following the cursor.
        Canvas.SetZIndex(block, TimelineClipDragZIndex);
        block.Opacity = .9;
        block.CapturePointer(args.Pointer);
        HideTimelineEdgeHover();
    }

    /// <summary>
    /// Dragging a clip edge: the cursor drives the dragged edge, which snaps
    /// to the nearest marker within 9 screen pixels and then trims by Core's
    /// rules. A drag below the minimum threshold trims nothing, so a light click does not create a stray trim.
    /// </summary>
    private void TimelineVideoClipPointerMoved(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragClipIndex != index || _timelineDragClipBlock is null || TimelineContentGrid is null) return;
        args.Handled = true;
        if (_timelineDragClipMode == TimelineClipDragMode.Move)
        {
            UpdateTimelineClipMoveDrag(index);
            return;
        }
        UpdateTimelineClipEdgeDrag(index, block);
    }

    /// <summary>
    /// Dragging a clip body: the insertion position is only computed once the
    /// cursor passes the drag threshold, then the lane layout is rebuilt around the cursor and the insertion line is drawn.
    /// </summary>
    /// <param name="index">Index of the clip being dragged.</param>
    private void UpdateTimelineClipMoveDrag(int index)
    {
        var pps = _timelinePixelsPerSecond;
        if (pps <= 0) return;
        var cursorContentX = CurrentTimelineCursorContentX();
        var moved = cursorContentX - _timelineDragClipOriginContentX;
        if (!EditorTimelineTrim.ReachedDragThreshold(moved, EditorTimelineClipMove.DragThresholdPixels))
        {
            _timelineDragClipMoveActive = false;
            _timelineDragClipMoveIndex = index;
            RestoreTimelineClipLayout();
            HideTimelineDropIndicator();
            return;
        }
        _timelineDragClipMoveActive = true;
        var insertion = EditorTimelineClipMove.InsertionIndex(_timelineClips, index, cursorContentX, pps);
        _timelineDragClipMoveIndex = insertion;
        ApplyTimelineClipMovePreview(index, insertion, cursorContentX, pps);
        ShowTimelineDropIndicator(
            EditorTimelineClipMove.InsertionBoundaryTime(_timelineClips, index, insertion),
            pps);
        StatusText.Text = insertion == index
            ? "Thả đoạn video vào đây để giữ nguyên thứ tự trên dải."
            : $"Thả đoạn video vào vị trí {insertion + 1} trên dải thời gian.";
    }

    /// <summary>
    /// Redraws the lane while dragging a clip body: the other clips close up in
    /// the new order (the dragged clip's slot is empty), while the dragged
    /// clip follows the cursor at its true width (neither the source file's in point nor its duration changes).
    /// </summary>
    /// <param name="index">Index of the clip being dragged.</param>
    /// <param name="insertion">New insertion position of the clip.</param>
    /// <param name="cursorContentX">Cursor X coordinate within the timeline content.</param>
    /// <param name="pps">Current pixel density of the timeline.</param>
    private void ApplyTimelineClipMovePreview(int index, int insertion, double cursorContentX, double pps)
    {
        var layout = EditorTimelineClipMove.Preview(_timelineClips, index, insertion);
        var draggedLeft = _timelineDragClipOriginLeft + (cursorContentX - _timelineDragClipOriginContentX);
        ApplyTimelineClipPositions(index, layout.Order, layout.Starts, draggedLeft, pps);
    }

    /// <summary>
    /// Returns the clip blocks to their previous layout (used when the drag has
    /// not reached the threshold or when a drag is cancelled).
    /// </summary>
    private void RestoreTimelineClipLayout()
    {
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var order = new List<int>(_timelineClips.Count);
        for (var index = 0; index < _timelineClips.Count; index++) order.Add(index);
        ApplyTimelineClipPositions(-1, order, starts, _timelineDragClipOriginLeft, _timelinePixelsPerSecond);
    }

    /// <summary>
    /// Positions the clip blocks for a lane layout: each clip uses its own
    /// original index, while the dragged clip follows the cursor.
    /// </summary>
    /// <param name="draggedIndex">Index of the clip being dragged (-1 when none).</param>
    /// <param name="order">Original clip indices in lane order.</param>
    /// <param name="starts">Start time of each clip in that order.</param>
    /// <param name="draggedLeft">X coordinate of the dragged clip.</param>
    /// <param name="pps">Current pixel density of the timeline.</param>
    private void ApplyTimelineClipPositions(
        int draggedIndex,
        IReadOnlyList<int> order,
        IReadOnlyList<double> starts,
        double draggedLeft,
        double pps)
    {
        for (var position = 0; position < order.Count; position++)
        {
            var clipIndex = order[position];
            if (clipIndex == draggedIndex) continue;
            var block = TimelineClipBlockAt(clipIndex);
            if (block is null || position >= starts.Count) continue;
            Canvas.SetLeft(block, EditorTimelineLayout.XForTime(starts[position], pps));
        }
        if (draggedIndex < 0) return;
        var dragged = TimelineClipBlockAt(draggedIndex);
        if (dragged is null) return;
        Canvas.SetLeft(dragged, draggedLeft);
    }

    /// <summary>
    /// The clip block currently drawn on the lane according to its position in
    /// the clip list (null when that clip lies outside the viewport).
    /// </summary>
    /// <param name="clipIndex">Position of the clip in the clip list.</param>
    private TimelineClipBlock? TimelineClipBlockAt(int clipIndex)
    {
        foreach (var entry in _timelineClipBlocks)
            if (entry.Index == clipIndex) return entry.Block;
        return null;
    }

    /// <summary>
    /// Dragging a clip edge: the edge follows the cursor, snaps to the nearest
    /// marker and then trims by Core's rules.
    /// </summary>
    /// <param name="index">Index of the clip being dragged.</param>
    /// <param name="block">Block of the clip being dragged.</param>
    private void UpdateTimelineClipEdgeDrag(int index, TimelineClipBlock block)
    {
        if (index < 0 || index >= _timelineClips.Count) return;
        var pps = _timelinePixelsPerSecond;
        if (pps <= 0) return;
        var cursorContentX = CurrentTimelineCursorContentX();
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var timelineStart = starts[index];
        var originEdge = _timelineDragClipEdge == EditorTimelineTrimEdge.In
            ? timelineStart
            : timelineStart + _timelineDragClipOriginDuration;
        var pressTime = EditorTimelineLayout.TimeForX(_timelineDragClipOriginContentX, pps);
        var edgeTime = EditorTimelineLayout.TimeForX(cursorContentX, pps);
        // The edge follows the cursor but keeps the initial press offset, so the
        // edge does not jump when the drag begins.
        var rawEdge = originEdge + (edgeTime - pressTime);
        if (!EditorTimelineTrim.ReachedDragThreshold(
                cursorContentX - _timelineDragClipOriginContentX, EditorTimelineTrim.MinimumDragPixels))
        {
            _timelineDragClipAppliedDelta = 0;
            _timelineDragClipNewSourceIn = _timelineDragClipOriginSourceIn;
            _timelineDragClipNewDuration = _timelineDragClipOriginDuration;
            ApplyTimelineClipDragPreview(block, _timelineClips[index], timelineStart, pps);
            HideTimelineSnapIndicator();
            return;
        }
        var snapped = EditorTimelineSnapping.SnapTime(
            rawEdge,
            _timelineDragSnapCandidates,
            pps);
        var delta = snapped - originEdge;
        var media = TimelineClipMediaCached(_timelineClips[index]);
        var mediaDuration = media is { Duration: > 0 } ? media.Duration : _timelineDragClipOriginSourceIn + _timelineDragClipOriginDuration;
        var minDuration = EditorTimelineTrim.MinimumDuration(media?.FrameRate ?? 0);
        var appliedDelta = EditorTimelineTrim.ClampTrim(
            _timelineClips,
            index,
            _timelineDragClipEdge,
            delta,
            mediaDuration,
            minDuration);
        var trimmed = EditorTimelineTrim.MoveEdge(_timelineClips[index], _timelineDragClipEdge, appliedDelta);
        _timelineDragClipAppliedDelta = appliedDelta;
        _timelineDragClipNewSourceIn = trimmed.SourceIn;
        _timelineDragClipNewDuration = trimmed.Duration;
        ApplyTimelineClipDragPreview(block, trimmed, timelineStart, pps);
        if (Math.Abs(snapped - rawEdge) > 1e-9) ShowTimelineSnapIndicator(snapped, pps);
        else HideTimelineSnapIndicator();
        StatusText.Text = TimelineClipTrimSummary(trimmed, _timelineDragClipEdge);
    }

    /// <summary>
    /// The cursor's current X coordinate within the timeline content, measured
    /// from the real on-screen cursor position so it is not skewed while the timeline scrolls mid-drag.
    /// </summary>
    private double CurrentTimelineCursorContentX()
    {
        if (TimelineContentGrid is null) return _timelineDragClipOriginContentX;
        var scale = TimelineContentGrid.XamlRoot?.RasterizationScale ?? 1d;
        if (!Win32Cursor.TryGetScreenPosition(out var screenX, out _)) return _timelineDragClipOriginContentX;
        var originScreenX = _timelineDragClipOriginScreenX - _timelineDragClipOriginContentX * scale;
        return (screenX - originScreenX) / scale;
    }

    private void TimelineVideoClipPointerReleased(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        FocusEditorKeyboardSurface();
        if (_timelineDragClipIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        if (_timelineDragClipMode == TimelineClipDragMode.Move)
        {
            var active = _timelineDragClipMoveActive;
            var insertion = _timelineDragClipMoveIndex;
            ResetTimelineClipDrag();
            if (!active)
            {
                QueueTimelineRender();
                return;
            }
            CommitTimelineClipMove(index, insertion);
            return;
        }
        var sourceIn = _timelineDragClipNewSourceIn;
        var duration = _timelineDragClipNewDuration;
        var changed = Math.Abs(sourceIn - _timelineDragClipOriginSourceIn) > .0005
            || Math.Abs(duration - _timelineDragClipOriginDuration) > .0005;
        var edge = _timelineDragClipEdge;
        var appliedDelta = _timelineDragClipAppliedDelta;
        ResetTimelineClipDrag();
        HideTimelineSnapIndicator();
        ClearTimelineEdgeHover(block);
        if (!changed)
        {
            QueueTimelineRender();
            return;
        }
        var clips = new List<EditorTimelineClip>(_timelineClips);
        clips[index] = EditorTimelineTrim.MoveEdge(clips[index], edge, appliedDelta);
        CommitTimelineClips(clips);
        _ = SaveTimelineClipTrimAsync(index, sourceIn, duration);
    }

    private void TimelineVideoClipPointerCanceled(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragClipIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        ResetTimelineClipDrag();
        HideTimelineSnapIndicator();
        ClearTimelineEdgeHover(block);
        QueueTimelineRender();
    }

    /// <summary>
    /// Clears the clip drag state: ends edge dragging, body dragging and the insertion line.
    /// </summary>
    private void ResetTimelineClipDrag()
    {
        if (_timelineDragClipBlock is TimelineClipBlock dragged)
        {
            Canvas.SetZIndex(dragged, 0);
            dragged.Opacity = 1;
        }
        _timelineDragClipMode = TimelineClipDragMode.None;
        _timelineDragClipIndex = -1;
        _timelineDragClipBlock = null;
        _timelineDragClipMoveActive = false;
        _timelineDragClipMoveIndex = -1;
        HideTimelineDropIndicator();
    }

    /// <summary>
    /// Commits a clip body drag: reorders the clips on the timeline and records
    /// it in the undo/redo history. Dropping back into the same slot changes nothing.
    /// </summary>
    /// <param name="index">Index of the clip just dragged.</param>
    /// <param name="insertion">New insertion position of the clip.</param>
    private void CommitTimelineClipMove(int index, int insertion)
    {
        if (index < 0 || index >= _timelineClips.Count)
        {
            QueueTimelineRender();
            return;
        }
        if (insertion == index)
        {
            QueueTimelineRender();
            return;
        }
        var label = _timelineClips[index].Label;
        var clips = EditorTimelineClipMove.Move(_timelineClips, index, insertion);
        CommitTimelineClips(clips);
        _ = SaveTimelineHistoryAsync($"Đã chuyển đoạn “{label}” sang vị trí {insertion + 1} trên dải thời gian.");
    }

    /// <summary>
    /// Hovering the edge grab zone switches the cursor and draws a highlight on
    /// that edge, so the user can tell that edge is ready to be trimmed.
    /// </summary>
    private void TimelineVideoClipHoverMoved(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragClipIndex >= 0) return;
        if (index < 0 || index >= _timelineClips.Count) return;
        var position = args.GetCurrentPoint(block).Position;
        var edge = EditorTimelineTrim.EdgeAt(position.X, block.ActualWidth, TimelineClipEdgeGrabPixels);
        if (edge is null)
        {
            ClearTimelineEdgeHover(block);
            return;
        }
        ShowTimelineEdgeHover(index, edge.Value, block);
    }

    private void TimelineVideoClipHoverExited(int index, TimelineClipBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragClipIndex >= 0) return;
        if (_timelineHoverClipIndex != index || _timelineHoverClipEdge is null) return;
        ClearTimelineEdgeHover(block);
    }

    /// <summary>Draws the highlight on the edge of the clip being hovered.</summary>
    private void ShowTimelineEdgeHover(int index, EditorTimelineTrimEdge edge, TimelineClipBlock block)
    {
        if (TimelineEdgeHoverIndicator is null) return;
        var clip = _timelineClips[index];
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var timelineStart = starts[index];
        var edgeTime = edge == EditorTimelineTrimEdge.In ? timelineStart : timelineStart + clip.Duration;
        var x = EditorTimelineLayout.XForTime(edgeTime, _timelinePixelsPerSecond);
        TimelineEdgeHoverIndicator.Visibility = Visibility.Visible;
        TimelineEdgeHoverIndicator.Height = EditorTimelineLayout.LaneHeight - 4;
        Canvas.SetLeft(TimelineEdgeHoverIndicator, x - 1);
        Canvas.SetTop(TimelineEdgeHoverIndicator, EditorTimelineLayout.RulerHeight + TimelineRulerGap + 2);
        _timelineHoverClipIndex = index;
        _timelineHoverClipEdge = edge;
        block.EdgeCursor = TimelineEdgeResizeCursor;
    }

    /// <summary>Removes the highlight and restores the default cursor.</summary>
    private void ClearTimelineEdgeHover(TimelineClipBlock? block)
    {
        HideTimelineEdgeHover();
        if (block is not null) block.EdgeCursor = null;
    }

    private void HideTimelineEdgeHover()
    {
        if (TimelineEdgeHoverIndicator is not null)
            TimelineEdgeHoverIndicator.Visibility = Visibility.Collapsed;
        _timelineHoverClipIndex = -1;
        _timelineHoverClipEdge = null;
    }

    /// <summary>Updates the dragging clip's block shape according to the trim result.</summary>
    private void ApplyTimelineClipDragPreview(
        TimelineClipBlock block,
        EditorTimelineClip trimmed,
        double timelineStart,
        double pps)
    {
        block.Width = EditorTimelineLayout.ClipWidth(trimmed.Duration, pps);
        Canvas.SetLeft(block, EditorTimelineLayout.XForTime(timelineStart, pps));
        block.Label.Text = TimelineClipDisplayName(trimmed);
    }

    /// <summary>The timeline's snap markers: other clips, the playhead, markers and trim edges.</summary>
    private IReadOnlyList<double> TimelineSnapCandidates(int clipIndex)
    {
        var trimMarks = new List<double>();
        var trim = _project?.Trim;
        if (trim is not null)
        {
            trimMarks.Add(TimelineTimeForSource(trim.Start));
            trimMarks.Add(TimelineTimeForSource(trim.End));
        }
        return EditorTimelineSnapping.Candidates(
            _timelineClips,
            Timeline.Value,
            _timelineSnapMarkers,
            trimMarks,
            clipIndex);
    }

    /// <summary>
    /// The timeline's markers: start/end marks of effect regions, subtitle cues
    /// and voice windows, converted to composed-timeline time.
    /// </summary>
    private List<double> CollectTimelineSnapMarkers(double visibleStart, double visibleEnd)
    {
        var markers = new List<double>();
        // Effect regions may span several clips, so their markers are resolved
        // on the composed timeline before the primary-only subtitle/voice filter.
        foreach (var region in _document.Regions)
        {
            var timelineStart = region.WholeVideo ? 0 : TimelineTimeForSource(region.Start);
            var timelineEnd = region.WholeVideo
                ? (TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0)
                : TimelineTimeForSource(region.End);
            if (timelineEnd < visibleStart || timelineStart > visibleEnd) continue;
            markers.Add(timelineStart);
            markers.Add(timelineEnd);
        }
        var visibleSource = VisiblePrimarySourceWindow(visibleStart, visibleEnd);
        if (visibleSource is not { } source) return markers;
        var cues = _subtitleSource?.Cues;
        if (cues is not null)
        {
            foreach (var cue in cues)
            {
                if (cue.End < source.Start || cue.Start > source.End) continue;
                markers.Add(TimelineTimeForSource(cue.Start));
                markers.Add(TimelineTimeForSource(cue.End));
            }
        }
        foreach (var window in _voiceCueWindows.Values)
        {
            if (window.VoiceEnd < source.Start || window.VoiceStart > source.End) continue;
            markers.Add(TimelineTimeForSource(window.VoiceStart));
            markers.Add(TimelineTimeForSource(window.VoiceEnd));
        }
        return markers;
    }

    /// <summary>
    /// The source-time range of the original clip currently on screen; markers
    /// are only meaningful within that source file.
    /// </summary>
    private (double Start, double End)? VisiblePrimarySourceWindow(double visibleStart, double visibleEnd)
    {
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var found = false;
        var start = double.PositiveInfinity;
        var end = double.NegativeInfinity;
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (!IsPrimaryTimelineClip(clip)) continue;
            var composedStart = starts[index];
            var composedEnd = composedStart + clip.Duration;
            if (composedEnd < visibleStart || composedStart > visibleEnd) continue;
            var sourceStart = clip.SourceIn + Math.Max(0, visibleStart - composedStart);
            var sourceEnd = clip.SourceIn + Math.Min(clip.Duration, visibleEnd - composedStart);
            if (sourceEnd < sourceStart) continue;
            found = true;
            start = Math.Min(start, sourceStart);
            end = Math.Max(end, sourceEnd);
        }
        return found ? (start, end) : null;
    }

    private void ShowTimelineSnapIndicator(double snappedTime, double pps)
    {
        if (TimelineSnapIndicator is null) return;
        TimelineSnapIndicator.Visibility = Visibility.Visible;
        Canvas.SetLeft(TimelineSnapIndicator, EditorTimelineLayout.XForTime(snappedTime, pps));
    }

    private void HideTimelineSnapIndicator()
    {
        if (TimelineSnapIndicator is null) return;
        TimelineSnapIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the insertion line at the boundary where the dragged clip will
    /// land, so the user can see whether it will be inserted before or after its neighbor.
    /// </summary>
    /// <param name="boundaryTime">Time mark of the insertion boundary (seconds).</param>
    /// <param name="pps">Current pixel density of the timeline.</param>
    private void ShowTimelineDropIndicator(double boundaryTime, double pps)
    {
        if (TimelineDropIndicator is null) return;
        TimelineDropIndicator.Visibility = Visibility.Visible;
        Canvas.SetLeft(TimelineDropIndicator, EditorTimelineLayout.XForTime(boundaryTime, pps) - 1);
        Canvas.SetTop(TimelineDropIndicator, EditorTimelineLayout.RulerHeight + TimelineRulerGap
            + EditorTimelineLayout.LaneTop(TimelineLaneVideo) + 2);
    }

    /// <summary>Hides the dragged clip's insertion line.</summary>
    private void HideTimelineDropIndicator()
    {
        if (TimelineDropIndicator is null) return;
        TimelineDropIndicator.Visibility = Visibility.Collapsed;
    }

    private string TimelineClipTrimSummary(EditorTimelineClip trimmed, EditorTimelineTrimEdge edge)
        => edge == EditorTimelineTrimEdge.In
            ? $"Cắt đầu đoạn: nguồn {trimmed.SourceIn:0.0}s → {trimmed.End:0.0}s, thời lượng {FormatClock(trimmed.Duration)}."
            : $"Cắt cuối đoạn: nguồn {trimmed.SourceIn:0.0}s → {trimmed.End:0.0}s, thời lượng {FormatClock(trimmed.Duration)}.";

    private async Task SaveTimelineClipTrimAsync(int index, double sourceIn, double duration)
    {
        try
        {
            await SaveProjectNowAsync();
            RefreshTimelinePreview();
            var clip = index >= 0 && index < _timelineClips.Count ? _timelineClips[index] : null;
            StatusText.Text = clip is null
                ? "Đã cắt đoạn video trên dải thời gian."
                : $"Đã cắt đoạn “{clip.Label}”: nguồn {sourceIn:0.0}s, thời lượng {FormatClock(duration)}.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Không lưu được đoạn đã cắt: " + error.Message;
        }
    }

    private void RenderTimelineEffectBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var duration = TimelineDuration > 0 ? TimelineDuration : _media?.Duration ?? 0;
        var regions = _document.Regions;
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            // A whole-video region covers the composed output; a timed region
            // keeps its primary-source coordinates and may continue into later
            // clips, which is why the end is mapped instead of clamped here.
            var timelineStart = region.WholeVideo ? 0 : TimelineTimeForSource(region.Start);
            var timelineEnd = region.WholeVideo ? duration : TimelineTimeForSource(region.End);
            if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
            var left = EditorTimelineLayout.XForTime(timelineStart, pps);
            var width = EditorTimelineLayout.ClipWidth(timelineEnd - timelineStart, pps);
            var block = new TimelineEffectBlock
            {
                Width = width,
                Height = EditorTimelineLayout.LaneHeight - 4,
            };
            var selected = index == _document.SelectedIndex;
            block.SurfaceBackground = TimelineEffectBrush(region.Effect);
            block.SurfaceBorderBrush = selected ? TimelineClipSelectedBorderBrush : TimelineBrush(255, 255, 255, 255);
            block.SurfaceBorderThickness = selected ? new Thickness(2) : new Thickness(0, 0, 0, 1);
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneEffect) + 2);
            ToolTipService.SetToolTip(block,
                $"{EffectLabel(region.Effect)} · {(region.WholeVideo ? "toàn video" : $"{region.Start:0.0}–{region.End:0.0}s")}\n"
                + "Kéo mép trái hoặc mép phải để kéo dài hay thu ngắn vùng hiệu ứng.\n"
                + "Nhấn Delete để xoá vùng đang chọn.");
            var captured = index;
            block.PointerPressed += (_, args) => TimelineRegionPointerPressed(captured, block, args);
            block.PointerMoved += (_, args) => TimelineRegionPointerMoved(captured, block, args);
            block.PointerReleased += (_, args) => TimelineRegionPointerReleased(captured, block, args);
            block.PointerCanceled += (_, args) => TimelineRegionPointerCanceled(captured, block, args);
            block.PointerExited += (_, args) => TimelineRegionPointerExited(captured, block, args);
            TimelineTracksCanvas.Children.Add(block);
        }
    }

    private void RenderTimelineSubtitleBlocks(double pps, double visibleStart, double visibleEnd)
    {
        var visible = CountVisiblePrimarySubtitleBlocks(visibleStart, visibleEnd)
            + CountVisibleClipSubtitleBlocks(visibleStart, visibleEnd);
        if (visible == 0) return;
        if (visible > MaxTimelineBlocks)
        {
            // Density too high: build a single summary bar instead of thousands of elements.
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

        var cues = _subtitleSource?.Cues;
        if (cues is not null)
        {
            for (var index = 0; index < cues.Count; index++)
            {
                var cue = cues[index];
                var timelineStart = TimelineTimeForSource(cue.Start);
                var timelineEnd = TimelineTimeForSource(cue.End);
                if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
                var left = EditorTimelineLayout.XForTime(timelineStart, pps);
                var width = EditorTimelineLayout.ClipWidth(timelineEnd - timelineStart, pps);
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
        RenderTimelineClipSubtitleBlocks(pps, visibleStart, visibleEnd);
    }

    private int CountVisiblePrimarySubtitleBlocks(double visibleStart, double visibleEnd)
    {
        var cues = _subtitleSource?.Cues;
        if (cues is null || cues.Count == 0) return 0;
        var count = 0;
        foreach (var cue in cues)
        {
            if (!EditorTimelineLayout.IntersectsVisible(
                    TimelineTimeForSource(cue.Start),
                    TimelineTimeForSource(cue.End),
                    visibleStart,
                    visibleEnd)) continue;
            if (++count > MaxTimelineBlocks) return count;
        }
        return count;
    }

    private int CountVisibleClipSubtitleBlocks(double visibleStart, double visibleEnd)
    {
        if (_timelineClips.Count == 0 || _clipSubtitleSources.Count == 0) return 0;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var count = 0;
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (IsPrimaryTimelineClip(clip)) continue;
            if (!_clipSubtitleSources.TryGetValue(clip.Id, out var source) || source.Cues.Count == 0) continue;
            var clipStart = starts[index];
            if (!EditorTimelineLayout.IntersectsVisible(
                    clipStart,
                    clipStart + clip.Duration,
                    visibleStart,
                    visibleEnd)) continue;
            foreach (var cue in source.Cues)
            {
                if (!TryTimelineClipCueSpan(clip, clipStart, cue, out var timelineStart, out var timelineEnd)) continue;
                if (!EditorTimelineLayout.IntersectsVisible(
                        timelineStart,
                        timelineEnd,
                        visibleStart,
                        visibleEnd)) continue;
                if (++count > MaxTimelineBlocks) return count;
            }
        }
        return count;
    }

    /// <summary>
    /// An SRT bound to one non-primary clip is authored in that clip's source
    /// file, not the project's primary video. Map it onto the composed timeline
    /// and clamp it to the trimmed clip range so it lands under the right segment.
    /// </summary>
    private static bool TryTimelineClipCueSpan(
        EditorTimelineClip clip,
        double clipStart,
        EditorSubtitleCue cue,
        out double timelineStart,
        out double timelineEnd)
    {
        timelineStart = Math.Max(clipStart, clipStart + (cue.Start - clip.SourceIn));
        timelineEnd = Math.Min(clipStart + clip.Duration, clipStart + (cue.End - clip.SourceIn));
        return timelineEnd > timelineStart;
    }

    private void RenderTimelineClipSubtitleBlocks(double pps, double visibleStart, double visibleEnd)
    {
        if (_timelineClips.Count == 0 || _clipSubtitleSources.Count == 0) return;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (IsPrimaryTimelineClip(clip)) continue;
            if (!_clipSubtitleSources.TryGetValue(clip.Id, out var source) || source.Cues.Count == 0) continue;
            var clipStart = starts[index];
            if (!EditorTimelineLayout.IntersectsVisible(
                    clipStart,
                    clipStart + clip.Duration,
                    visibleStart,
                    visibleEnd)) continue;
            foreach (var cue in source.Cues)
            {
                if (!TryTimelineClipCueSpan(clip, clipStart, cue, out var timelineStart, out var timelineEnd)) continue;
                if (!EditorTimelineLayout.IntersectsVisible(timelineStart, timelineEnd, visibleStart, visibleEnd)) continue;
                var width = EditorTimelineLayout.ClipWidth(timelineEnd - timelineStart, pps);
                var hasVietnamese = !string.IsNullOrWhiteSpace(cue.VietnameseText);
                var block = new Border
                {
                    Width = width,
                    Height = EditorTimelineLayout.LaneHeight - 4,
                    CornerRadius = new CornerRadius(2),
                    Background = hasVietnamese ? TimelineCueBrush : TimelineCueMissingBrush,
                    BorderBrush = TimelineBrush(0, 0, 0, 0),
                    BorderThickness = new Thickness(0),
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
                Canvas.SetLeft(block, EditorTimelineLayout.XForTime(timelineStart, pps));
                Canvas.SetTop(block, EditorTimelineLayout.LaneTop(TimelineLaneSubtitle) + 2);
                ToolTipService.SetToolTip(block,
                    $"SRT riêng của đoạn “{clip.Label}” · #{cue.Number} · {cue.Timing}\n"
                    + $"{TrimPreview(cue.SourceText)}\nBấm để chọn đoạn và tua tới câu này.");
                var captured = index;
                var seekTime = timelineStart;
                block.PointerPressed += (_, args) => TimelineClipSubtitlePressed(captured, seekTime, args);
                TimelineTracksCanvas.Children.Add(block);
            }
        }
    }

    private void TimelineClipSubtitlePressed(int clipIndex, double timelineStart, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        FocusEditorKeyboardSurface();
        if (clipIndex >= 0 && clipIndex < _timelineClips.Count)
        {
            _timelineSelectedClipIndex = clipIndex;
            UpdateTimelineClipSelection();
            StatusText.Text =
                $"Đang chọn đoạn “{_timelineClips[clipIndex].Label}”. SRT riêng của đoạn đang hiển thị trên dải phụ đề.";
        }
        _ = SeekEditorToTimelineAsync(timelineStart);
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
        var width = EditorTimelineLayout.ClipWidth(timelineEnd - timelineStart, pps);
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
        RenderTimelineVoiceWaveform(track, left, width, pps, visibleStart, visibleEnd);

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
                Width = EditorTimelineLayout.ClipWidth(windowEnd - windowStart, pps),
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
    /// Peak envelope of the voice file as dark columns over the green voice
    /// block: one column every 2 px holding the loudest 20 ms bucket under it,
    /// so silence reads as a flat line and speech as spikes. The columns are
    /// clipped to the visible range, which keeps the element count bounded no
    /// matter how long the voice track is.
    /// </summary>
    private void RenderTimelineVoiceWaveform(
        EditorVoiceTrack track, double left, double width, double pps, double visibleStart, double visibleEnd)
    {
        var waveform = _voiceWaveform;
        if (waveform is null || waveform.Peaks.Length == 0 || width < 6 || pps <= 0) return;
        const double columnStep = 2;
        var innerHeight = EditorTimelineLayout.LaneHeight - 14;
        var innerTop = EditorTimelineLayout.LaneTop(TimelineLaneVoice) + 7;
        var firstX = Math.Max(left, EditorTimelineLayout.XForTime(visibleStart, pps));
        var lastX = Math.Min(left + width, EditorTimelineLayout.XForTime(visibleEnd, pps));
        if (lastX <= firstX) return;
        var peaks = waveform.Peaks;
        var bucketSeconds = waveform.BucketSeconds;
        for (var x = firstX; x < lastX; x += columnStep)
        {
            var from = track.Start + (x - left) / pps;
            var to = track.Start + (Math.Min(x + columnStep, left + width) - left) / pps;
            var firstBucket = Math.Max(0, (int)(from / bucketSeconds));
            if (firstBucket >= peaks.Length) break;
            var lastBucket = Math.Min(peaks.Length, Math.Max(firstBucket + 1, (int)Math.Ceiling(to / bucketSeconds)));
            var peak = 0;
            for (var i = firstBucket; i < lastBucket; i++)
                if (peaks[i] > peak) peak = peaks[i];
            var height = Math.Max(1.5, peak / 255.0 * innerHeight);
            var column = new Rectangle
            {
                Width = columnStep - .6,
                Height = height,
                Fill = TimelineWaveformBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(column, x);
            Canvas.SetTop(column, innerTop + (innerHeight - height) / 2);
            TimelineTracksCanvas.Children.Add(column);
        }
    }

    /// <summary>
    /// Converts a time in the source file to a time on the composed timeline. A
    /// single-video timeline only shifts by the clip's in point (after trimming
    /// the In edge the in point is no longer 0); a multi-clip timeline resolves
    /// it through the original clip containing that time (one source file can be split into several clips).
    /// </summary>
    private double TimelineTimeForSource(double sourceTime)
    {
        if (_timelineClips.Count == 0) return sourceTime;
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        var fallback = -1;
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (!IsPrimaryTimelineClip(clip)) continue;
            if (fallback < 0) fallback = index;
            if (sourceTime >= clip.SourceIn && sourceTime <= clip.End)
                return starts[index] + (sourceTime - clip.SourceIn);
        }
        if (fallback >= 0)
            return starts[fallback] + (sourceTime - _timelineClips[fallback].SourceIn);
        return sourceTime;
    }

    /// <summary>
    /// The ranges on the composed timeline excluded from the exported file. Kept
    /// ranges are coordinates in the original clip's source file, so each
    /// original clip is mapped separately to composed-timeline coordinates; clips that do not belong to that source file are always kept unchanged.
    /// </summary>
    private IReadOnlyList<(double Start, double End)> TrimExcludedTimelineRanges()
    {
        var trim = _project?.Trim;
        if (trim is null || _timelineClips.Count == 0) return [];
        var ranges = new List<(double, double)>();
        var starts = EditorTimelineComposition.Starts(_timelineClips);
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            var clip = _timelineClips[index];
            if (!IsPrimaryTimelineClip(clip)) continue;
            var timelineStart = starts[index];
            var keepStart = Math.Max(clip.SourceIn, trim.Start);
            var keepEnd = Math.Min(clip.End, trim.End);
            if (keepEnd - keepStart < .1)
            {
                ranges.Add((timelineStart, timelineStart + clip.Duration));
                continue;
            }
            if (keepStart > clip.SourceIn) ranges.Add((timelineStart, timelineStart + (keepStart - clip.SourceIn)));
            if (keepEnd < clip.End) ranges.Add((timelineStart + (keepEnd - clip.SourceIn), timelineStart + clip.Duration));
        }
        return ranges;
    }

    private void ApplyTimelineClips(IReadOnlyList<EditorTimelineClip> clips)
    {
        _timelineClips = clips;
        _timelineSelectedClipIndex = clips.Count > 0 ? Math.Clamp(_timelineSelectedClipIndex, 0, clips.Count - 1) : -1;
        if (_project is not null) _project = _project with { Timeline = new EditorTimelineProject(clips) };
        SyncTimelineDuration();
        RefreshRegionTimeLimits();
        QueueTimelineRender();
    }

    /// <summary>
    /// Records one timeline operation into the undo/redo history and then applies
    /// the new state. Every operation that changes the composed timeline (add,
    /// remove, split, edge trim, reorder drag) goes through here so it can be undone.
    /// </summary>
    /// <param name="clips">Clip list after the operation.</param>
    private void CommitTimelineClips(IReadOnlyList<EditorTimelineClip> clips)
    {
        _timelineHistory.Push(_timelineClips, clips);
        ApplyTimelineClips(clips);
        RefreshTimelineClipTools();
    }

    /// <summary>
    /// Undo (Ctrl+Z): always prefers the timeline history, only falling back to
    /// the effect-region history when the timeline history is empty. Reapplies
    /// the clip list from before the operation, keeps the selected clip, then refreshes the preview and saves the project.
    /// </summary>
    private async Task UndoTimelineAsync()
    {
        if (!TimelineEditingAllowed()) return;
        if (!_timelineHistory.CanUndo)
        {
            if (TryUndoDocument()) return;
            StatusText.Text = "Không có thao tác nào để hoàn tác.";
            return;
        }
        var selectedId = TimelineSelectedClipId();
        var previous = _timelineHistory.Undo();
        if (previous is null)
        {
            StatusText.Text = "Không có thao tác nào trên dải thời gian để hoàn tác.";
            RefreshTimelineClipTools();
            return;
        }
        ApplyTimelineClips(previous);
        SelectTimelineClipById(selectedId);
        RefreshTimelineClipTools();
        await SaveTimelineHistoryAsync("Đã hoàn tác thao tác trên dải thời gian.");
    }

    /// <summary>
    /// Redo (Ctrl+Y): always prefers the timeline history, only falling back to
    /// the effect-region history when the timeline history has no redo step
    /// left. Reapplies the clip list from after the operation, keeps the
    /// selected clip, then refreshes the preview and saves the project.
    /// </summary>
    private async Task RedoTimelineAsync()
    {
        if (!TimelineEditingAllowed()) return;
        if (!_timelineHistory.CanRedo)
        {
            if (TryRedoDocument()) return;
            StatusText.Text = "Không có thao tác nào để làm lại.";
            return;
        }
        var selectedId = TimelineSelectedClipId();
        var next = _timelineHistory.Redo();
        if (next is null)
        {
            StatusText.Text = "Không có thao tác nào trên dải thời gian để làm lại.";
            RefreshTimelineClipTools();
            return;
        }
        ApplyTimelineClips(next);
        SelectTimelineClipById(selectedId);
        RefreshTimelineClipTools();
        await SaveTimelineHistoryAsync("Đã làm lại thao tác trên dải thời gian.");
    }

    /// <summary>
    /// Runs one timeline-history command and reports an error if the command
    /// breaks, so a call coming from a shortcut does not crash the app.
    /// </summary>
    /// <param name="command">Command to run.</param>
    private async Task RunTimelineHistoryCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception error)
        {
            StartupDiagnostics.WriteException("editor-timeline-history", error);
            StatusText.Text = "Không thực hiện được lệnh lịch sử dải thời gian: " + error.Message;
        }
    }

    /// <summary>
    /// Reports a timeline command failure without letting an async void event
    /// handler take down the whole app.
    /// </summary>
    private void ReportEditorCommandFailure(string action, Exception error)
    {
        StartupDiagnostics.WriteException("editor-timeline-command", error);
        try { StatusText.Text = "Không thực hiện được thao tác " + action + ": " + error.Message; }
        catch (Exception statusError) { StartupDiagnostics.WriteException("editor-timeline-status", statusError); }
    }

    /// <summary>
    /// Whether the timeline may be edited right now: while the app runs a job
    /// (export/preview), clips cannot be added, split, removed or undone.
    /// </summary>
    private bool TimelineEditingAllowed()
    {
        if (EditorBusy || _playback.IsPreviewMode)
        {
            StatusText.Text = "Không chỉnh sửa dải thời gian được khi app đang chạy job. "
                + "Hãy đợi job xong rồi thử lại.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Updates the enable/disable state of the timeline buttons: the add, split
    /// and remove buttons are locked while the app runs a job, and the
    /// undo/redo buttons follow the timeline history exactly.
    /// </summary>
    private void RefreshTimelineClipTools()
    {
        var editable = !EditorBusy && _media is not null && !_playback.IsPreviewMode;
        TimelineAddClipButton.IsEnabled = editable;
        TimelineSplitClipButton.IsEnabled = editable;
        TimelineRemoveClipButton.IsEnabled = editable;
        TimelineClipSubtitleButton.IsEnabled = editable && _timelineSelectedClipIndex >= 0;
        var selectedClip = SelectedTimelineClip();
        TimelineClipSubtitleButton.Content = selectedClip is not null && IsPrimaryTimelineClip(selectedClip)
            ? "SRT gốc"
            : selectedClip is not null && HasClipSubtitle(selectedClip.Id) ? "SRT ✓" : "SRT đoạn";
        TimelineUndoButton.IsEnabled = editable && _timelineHistory.CanUndo;
        TimelineRedoButton.IsEnabled = editable && _timelineHistory.CanRedo;
    }

    /// <summary>Id of the clip selected on the timeline (null when no clip is selected).</summary>
    private string? TimelineSelectedClipId()
        => _timelineSelectedClipIndex >= 0 && _timelineSelectedClipIndex < _timelineClips.Count
            ? _timelineClips[_timelineSelectedClipIndex].Id
            : null;

    /// <summary>
    /// Reselects a clip by id after undo/redo so the user still sees exactly
    /// the clip they were working on.
    /// </summary>
    /// <param name="id">Id of the clip to reselect.</param>
    private void SelectTimelineClipById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        for (var index = 0; index < _timelineClips.Count; index++)
        {
            if (!string.Equals(_timelineClips[index].Id, id, StringComparison.Ordinal)) continue;
            _timelineSelectedClipIndex = index;
            return;
        }
    }

    /// <summary>
    /// Saves the project after the composed timeline changed and refreshes the
    /// preview: the static preview is redrawn, while a playing preview is
    /// scrubbed back to the playhead position so the playing segment matches the new clip order.
    /// </summary>
    /// <param name="message">Message reported to the user.</param>
    private async Task SaveTimelineHistoryAsync(string message)
    {
        await SaveProjectNowAsync();
        RefreshTimelinePreview();
        StatusText.Text = message;
    }

    /// <summary>Refreshes the preview after the clip order on the timeline changed.</summary>
    private void RefreshTimelinePreview()
    {
        if (_playback.IsPreviewMode)
        {
            _ = _playback.SeekAsync(Timeline.Value);
            return;
        }
        QueuePreviewRefresh();
    }

    private async void TimelineAddClipButton_Click(object sender, RoutedEventArgs args)
    {
        if (!TimelineEditingAllowed()) return;
        try
        {
            // Standard editor import: one dialog, many videos, sidecar SRTs come
            // along automatically (ImportVideosAsync), instead of one click per file.
            var picked = await _picker.PickVideosAsync();
            if (picked.Count == 0) return;
            var imported = await ImportVideosAsync(picked);
            if (imported is null) return;
            var clips = imported;
            CommitTimelineClips(clips);
            await SaveProjectNowAsync();
            RefreshTimelinePreview();
            NotifyEditorCompositeChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StartupDiagnostics.WriteException("editor-timeline-add-clip", error);
            StatusText.Text = "Không thêm được đoạn video: " + error.Message;
        }
    }

    private async void TimelineSplitClipButton_Click(object sender, RoutedEventArgs args)
    {
        try { await SplitTimelineClipAtPlayheadAsync(); }
        catch (Exception error) { ReportEditorCommandFailure("tách đoạn video", error); }
    }

    /// <summary>
    /// The timeline's undo button: it uses the same undo command as Ctrl+Z, so
    /// the button and the shortcut always produce the same result.
    /// </summary>
    private async void TimelineUndoButton_Click(object sender, RoutedEventArgs args)
    {
        await RunTimelineHistoryCommandAsync(UndoTimelineAsync);
    }

    /// <summary>
    /// The timeline's redo button: it uses the same redo command as Ctrl+Y, so
    /// the button and the shortcut always produce the same result.
    /// </summary>
    private async void TimelineRedoButton_Click(object sender, RoutedEventArgs args)
    {
        await RunTimelineHistoryCommandAsync(RedoTimelineAsync);
    }

    /// <summary>
    /// Splits a video segment at the playhead. The Split button and the Ctrl+B
    /// shortcut share this one command, so there is no second, separate clip split logic.
    /// </summary>
    private async Task SplitTimelineClipAtPlayheadAsync()
    {
        if (!TimelineEditingAllowed()) return;
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
        // Both halves of a split keep reading the same SRT/voice of their source clip.
        InheritClipAttachments(clip.Id, clips[index + 1].Id);
        _timelineSelectedClipIndex = index;
        CommitTimelineClips(clips);
        await SaveProjectNowAsync();
        StatusText.Text = "Đã tách đoạn video tại playhead.";
    }

    private async void TimelineRemoveClipButton_Click(object sender, RoutedEventArgs args)
    {
        try { await RemoveSelectedTimelineClipAsync(); }
        catch (Exception error) { ReportEditorCommandFailure("xoá đoạn video", error); }
    }

    /// <summary>
    /// Removes the clip selected on the timeline. The Remove button and the
    /// Delete shortcut share this one command, so there is no second, separate clip removal logic.
    /// </summary>
    private async Task RemoveSelectedTimelineClipAsync()
    {
        if (!TimelineEditingAllowed()) return;
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
        CommitTimelineClips(clips);
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
        ScrollTimelineToComposedTime(time);
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
        block.Width = EditorTimelineLayout.ClipWidth(updated.End - updated.Start, pps);
    }

    private void TimelineCuePointerReleased(int index, FrameworkElement block, PointerRoutedEventArgs args)
    {
        FocusEditorKeyboardSurface();
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

    /// <summary>
    /// Which effect-block edge is under the pointer. Narrow blocks split at the
    /// middle so both edges stay grabbable even at low zoom.
    /// </summary>
    private static EditorCueEdge? TimelineEffectEdgeAt(FrameworkElement block, double x)
    {
        var width = block.ActualWidth;
        if (width <= 0) return null;
        if (width <= TimelineEffectEdgeGrabPixels * 2)
            return x < width / 2 ? EditorCueEdge.Start : EditorCueEdge.End;
        if (x <= TimelineEffectEdgeGrabPixels) return EditorCueEdge.Start;
        if (x >= width - TimelineEffectEdgeGrabPixels) return EditorCueEdge.End;
        return null;
    }

    private void TimelineRegionPointerPressed(int index, TimelineEffectBlock block, PointerRoutedEventArgs args)
    {
        var regions = _document.Regions;
        if (index < 0 || index >= regions.Count) return;
        args.Handled = true;
        var position = args.GetCurrentPoint(block).Position;
        var edge = TimelineEffectEdgeAt(block, position.X);
        if (edge is null)
        {
            // RegionList is the sole owner of region selection (BLUR-03): the timeline
            // only assigns the index, it never calls _document.Select itself.
            RegionList.SelectedIndex = index;
            StatusText.Text = $"Đã chọn {EffectLabel(regions[index].Effect)}. Kéo mép trái hoặc mép phải để kéo dài hay thu ngắn, nhấn Delete để xoá.";
            return;
        }
        if (!TimelineEditingAllowed()) return;
        if (_media is null || _media.Duration <= 0 || TimelineContentGrid is null) return;
        var region = regions[index];
        var originStart = region.WholeVideo
            ? Math.Max(0, RegionTimeForTimeline(0) ?? 0)
            : region.Start;
        var originEnd = region.WholeVideo
            ? Math.Max(originStart, RegionTimeForTimeline(TimelineDuration) ?? _media.Duration)
            : region.End;
        _timelineDragRegionIndex = index;
        _timelineDragRegionEdge = edge.Value;
        _timelineDragRegionOrigin = region;
        _timelineDragRegionOriginStart = originStart;
        _timelineDragRegionOriginEnd = originEnd;
        _timelineDragRegionNewStart = originStart;
        _timelineDragRegionNewEnd = originEnd;
        _timelineDragRegionOriginContentX = args.GetCurrentPoint(TimelineContentGrid).Position.X;
        _timelineDragRegionOriginScreenX = Win32Cursor.TryGetScreenPosition(out var screenX, out _) ? screenX : 0d;
        _timelineDragRegionBlock = block;
        block.CapturePointer(args.Pointer);
        block.EdgeCursor = TimelineEdgeResizeCursor;
        block.HighlightEdge(edge);
        StatusText.Text = edge == EditorCueEdge.Start
            ? "Kéo mép trái để kéo dài hoặc thu ngắn vùng hiệu ứng."
            : "Kéo mép phải để kéo dài hoặc thu ngắn vùng hiệu ứng.";
    }

    private void TimelineRegionPointerMoved(int index, TimelineEffectBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragRegionIndex == index)
        {
            UpdateTimelineRegionDrag(index, block);
            return;
        }
        var edge = TimelineEffectEdgeAt(block, args.GetCurrentPoint(block).Position.X);
        block.EdgeCursor = edge is null ? null : TimelineEdgeResizeCursor;
        block.HighlightEdge(edge);
    }

    private void TimelineRegionPointerExited(int index, TimelineEffectBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragRegionIndex == index) return;
        block.EdgeCursor = null;
        block.HighlightEdge(null);
    }

    /// <summary>
    /// Live effect-region resize: the edge follows the cursor, clamped to the
    /// source file, and the block is repainted at the composed-timeline position
    /// so the width really changes while the mouse is down.
    /// </summary>
    private void UpdateTimelineRegionDrag(int index, TimelineEffectBlock block)
    {
        if (_timelineDragRegionBlock is null || _timelineDragRegionOrigin is null) return;
        if (_media is null || TimelineContentGrid is null) return;
        var pps = _timelinePixelsPerSecond;
        if (pps <= 0) return;
        var scale = TimelineContentGrid.XamlRoot?.RasterizationScale ?? 1d;
        var cursorContentX = _timelineDragRegionOriginContentX;
        if (Win32Cursor.TryGetScreenPosition(out var screenX, out _))
        {
            var originScreenX = _timelineDragRegionOriginScreenX - _timelineDragRegionOriginContentX * scale;
            cursorContentX = (screenX - originScreenX) / scale;
        }
        var deltaSeconds = (cursorContentX - _timelineDragRegionOriginContentX) / pps;
        var duration = RegionTimeLimit();
        var minDuration = Math.Min(TimelineEffectMinDuration, duration);
        var start = _timelineDragRegionOriginStart;
        var end = _timelineDragRegionOriginEnd;
        if (_timelineDragRegionEdge == EditorCueEdge.Start)
        {
            var maximum = Math.Max(0, end - minDuration);
            start = Math.Clamp(start + deltaSeconds, 0, maximum);
        }
        else
        {
            var minimum = Math.Min(duration, start + minDuration);
            end = Math.Clamp(end + deltaSeconds, minimum, duration);
        }
        _timelineDragRegionNewStart = start;
        _timelineDragRegionNewEnd = end;
        var left = EditorTimelineLayout.XForTime(TimelineTimeForSource(start), pps);
        var right = EditorTimelineLayout.XForTime(TimelineTimeForSource(end), pps);
        Canvas.SetLeft(block, left);
        block.Width = Math.Max(2, right - left);
        block.HighlightEdge(_timelineDragRegionEdge);
        StatusText.Text = $"{EffectLabel(_timelineDragRegionOrigin.Effect)} · {start:0.0}–{end:0.0}s · thả chuột để lưu.";
    }

    private void TimelineRegionPointerReleased(int index, TimelineEffectBlock block, PointerRoutedEventArgs args)
    {
        FocusEditorKeyboardSurface();
        if (_timelineDragRegionIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        var start = _timelineDragRegionNewStart;
        var end = _timelineDragRegionNewEnd;
        var changed = Math.Abs(start - _timelineDragRegionOriginStart) > .001
            || Math.Abs(end - _timelineDragRegionOriginEnd) > .001;
        ResetTimelineRegionDrag(block);
        if (!changed)
        {
            if (index >= 0 && index < _document.Regions.Count)
            {
                RegionList.SelectedIndex = index;
                StatusText.Text = $"Đã chọn {EffectLabel(_document.Regions[index].Effect)}. Kéo mép trái hoặc mép phải để kéo dài hay thu ngắn, nhấn Delete để xoá.";
            }
            QueueTimelineRender();
            return;
        }
        CommitTimelineRegionScope(index, start, end);
    }

    private void TimelineRegionPointerCanceled(int index, TimelineEffectBlock block, PointerRoutedEventArgs args)
    {
        if (_timelineDragRegionIndex != index) return;
        args.Handled = true;
        block.ReleasePointerCapture(args.Pointer);
        ResetTimelineRegionDrag(block);
        QueueTimelineRender();
    }

    private void ResetTimelineRegionDrag(TimelineEffectBlock? block)
    {
        _timelineDragRegionIndex = -1;
        _timelineDragRegionOrigin = null;
        _timelineDragRegionBlock = null;
        if (block is null) return;
        block.EdgeCursor = null;
        block.HighlightEdge(null);
    }

    /// <summary>
    /// Persists the new effect-region scope. Dragging either edge turns a
    /// whole-video effect into a timed region; the change goes through
    /// ReplaceSelected so undo/redo and the project autosave keep working.
    /// </summary>
    private void CommitTimelineRegionScope(int index, double start, double end)
    {
        var regions = _document.Regions;
        if (index < 0 || index >= regions.Count)
        {
            QueueTimelineRender();
            return;
        }
        if (RegionList.SelectedIndex != index) RegionList.SelectedIndex = index;
        var region = _document.Selected;
        if (region is null)
        {
            QueueTimelineRender();
            return;
        }
        var updated = region with { WholeVideo = false, Start = start, End = end };
        if (!_document.ReplaceSelected(updated))
        {
            QueueTimelineRender();
            return;
        }
        _draftRegion = null;
        DocumentChanged($"Đã đổi phạm vi {EffectLabel(updated.Effect)}: {start:0.0}–{end:0.0}s. Kéo mép để chỉnh tiếp.");
        NotifyEditorCompositeChanged();
    }

    private void TimelineCueListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subtitleCueSyncing) return;
        var cues = _subtitleSource?.Cues;
        if (cues is null) return;
        var index = SubtitleCueList.SelectedIndex;
        if (index < 0 || index >= cues.Count) return;
        var cue = cues[index];
        ScrollTimelineToComposedTime(TimelineTimeForSource(cue.Start), cue.End - cue.Start);
        QueueTimelineRender();
    }

    /// <summary>
    /// Scrolls the timeline to a time mark on the composed timeline.
    /// </summary>
    /// <param name="composedTime">Time mark on the composed timeline (seconds).</param>
    /// <param name="spanSeconds">Length of the mark that must be visible (seconds, composed-timeline coordinates).</param>
    private void ScrollTimelineToComposedTime(double composedTime, double spanSeconds = 0)
    {
        if (TimelineScrollViewer is null) return;
        // A cue too small to see while zoomed out: zoom to a readable level
        // first, then scroll to the cue, but do not disturb the zoom level the
        // user has already zoomed into.
        if (spanSeconds > 0 && _timelinePixelsPerSecond < ReadableTimelinePixelsPerSecond
            && spanSeconds * _timelinePixelsPerSecond < TimelineCueVisiblePixels)
        {
            ApplyTimelineZoom(TimelineZoomForPixelsPerSecond(ReadableTimelinePixelsPerSecond));
        }
        _timelinePendingScrollTime = composedTime;
        _timelinePendingScrollOffset = null;
        QueueTimelineRender();
    }

    /// <summary>
    /// Scrolls after rendering: changing zoom changes the content width, so
    /// scrolling before rendering makes the ScrollViewer clamp the offset back
    /// to 0.
    /// </summary>
    private void ApplyPendingTimelineScroll()
    {
        if (TimelineScrollViewer is null) return;
        var viewport = TimelineViewportWidth();
        if (viewport <= 1) return;
        if (_timelinePendingScrollTime is { } time)
        {
            _timelinePendingScrollTime = null;
            _timelinePendingScrollOffset = null;
            var pps = _timelinePixelsPerSecond;
            var x = EditorTimelineLayout.XForTime(time, pps);
            var timelineDuration = TimelineWorkingDuration;
            var maxOffset = EditorTimelineLayout.MaxScrollOffset(
                EditorTimelineLayout.ContentWidth(timelineDuration, pps),
                viewport);
            var offset = Math.Clamp(x - viewport / 2, 0, maxOffset);
            if (Math.Abs(offset - CurrentTimelineScrollOffset()) < 1) return;
            TimelineScrollViewer.UpdateLayout();
            TimelineScrollViewer.ChangeView(offset, null, null);
            return;
        }
        if (_timelinePendingScrollOffset is not { } pendingOffset) return;
        _timelinePendingScrollOffset = null;
        // The scroll offset must never be negative: when the timeline is shorter
        // than the viewport it is always forced to 0 so there is no blank gap at
        // the left edge.
        var clampedOffset = Math.Max(0, pendingOffset);
        if (Math.Abs(clampedOffset - CurrentTimelineScrollOffset()) < 1) return;
        TimelineScrollViewer.UpdateLayout();
        TimelineScrollViewer.ChangeView(clampedOffset, null, null);
    }


    private void UpdateTimelinePlayhead()
    {
        if (TimelinePlayhead is null) return;
        // Timeline.Value is a composed-timeline coordinate, so it is not mapped again.
        var x = EditorTimelineLayout.XForTime(Timeline.Value, _timelinePixelsPerSecond);
        // The indicator line is centered exactly on the time mark, and the round
        // head sits inside the ruler band so the user can grab it.
        Canvas.SetLeft(TimelinePlayhead, x - TimelinePlayhead.Width / 2);
        Canvas.SetTop(TimelinePlayhead, 0);
        if (TimelinePlayheadHead is null) return;
        Canvas.SetLeft(TimelinePlayheadHead, x - TimelinePlayheadHead.Width / 2);
        Canvas.SetTop(TimelinePlayheadHead, (EditorTimelineLayout.RulerHeight - TimelinePlayheadHead.Height) / 2);
    }

    /// <summary>
    /// The timeline's pixel density reading: exactly the pixel density that the
    /// ruler, clips, playhead and mouse hit-testing are using (the interface's
    /// logical unit, DIP; a screenshot at 125% display shows 1.25x).
    /// </summary>
    private void UpdateTimelineZoomText()
    {
        if (TimelineZoomText is null) return;
        var pps = EditorTimelineLayout.ClampPixelsPerSecond(_timelinePixelsPerSecond);
        TimelineZoomText.Text = pps >= 1
            ? $"{pps:0.##} px/giây"
            : $"{1 / pps:0.##} giây/px";
    }

    private void TimelineZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTimelineZoom) return;
        _timelineFitPending = false;
        ApplyTimelineZoomPixels(TimelinePixelsPerSecondForZoom(e.NewValue));
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
    /// The WinUI 3 ScrollViewer does not expose a readable offset, so it is
    /// measured by placing a marker at x=0 in the content and asking its
    /// position relative to the ScrollViewer.
    /// </summary>
    private double CurrentTimelineScrollOffset()
    {
        if (TimelineOriginMarker is null || TimelineScrollViewer is null) return Math.Max(0, _timelineScrollOffset);
        try
        {
            var point = TimelineOriginMarker.TransformToVisual(TimelineScrollViewer).TransformPoint(new Point(0, 0));
            if (double.IsFinite(point.X)) return Math.Max(0, -point.X);
        }
        catch (Exception)
        {
            // The marker is not in the visual tree yet (the panel is hidden) -> keep the last offset.
        }
        return Math.Max(0, _timelineScrollOffset);
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

    /// <summary>
    /// Gives the Editor page the keyboard focus after the user clicks the player
    /// or a timeline block. Without this, focus can stay inside a NumberBox
    /// (X/Y/W/H), so Delete/Backspace edits that box instead of deleting the
    /// clip or effect region the user just selected. Text boxes still keep their
    /// own keys while they are focused; focus only moves on an explicit click.
    /// </summary>
    private void FocusEditorKeyboardSurface()
    {
        if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this)) return;
        Focus(FocusState.Programmatic);
    }

    private void TimelineRuler_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    private void TimelineTracks_PointerPressed(object sender, PointerRoutedEventArgs e) => SeekTimelineFromPointer(e);

    /// <summary>
    /// Clicking the round playhead handle: this control sits ON TOP of the
    /// ruler, so it swallows the press event (it is a direct child of the
    /// content canvas, not a child of the ruler). If it does not accept the
    /// event itself, the first click lands on empty space and dragging the
    /// playhead can never begin.
    /// </summary>
    private void TimelinePlayheadHead_PointerPressed(object sender, PointerRoutedEventArgs e)
        => SeekTimelineFromPointer(e);

    /// <summary>
    /// Clicking on the timeline: clicking the area around the playhead handle
    /// (on the ruler band) GRABS the playhead for dragging, while clicking
    /// elsewhere scrubs to the clicked position.
    /// </summary>
    private void SeekTimelineFromPointer(PointerRoutedEventArgs e)
    {
        if (_media is null || TimelineContentGrid is null) return;
        var position = e.GetCurrentPoint(TimelineContentGrid).Position;
        var playheadX = EditorTimelineLayout.XForTime(Timeline.Value, _timelinePixelsPerSecond);
        // The playhead grab zone only opens on the ruler band: a click in the
        // lane area belongs to clip edge trimming and cue dragging and must not
        // be taken away.
        if (EditorTimelinePlayhead.IsInRulerBand(position.Y, EditorTimelineLayout.RulerHeight)
            && EditorTimelinePlayhead.IsWithinGrabRange(position.X, playheadX))
        {
            e.Handled = true;
            StartTimelinePlayheadDrag(e);
            return;
        }
        // The timeline background spans the full viewport width, so clicking
        // on it must not scrub past the working extent of the strip (the
        // empty tail counts, it is part of the strip).
        var contentWidth = EditorTimelineLayout.ContentWidth(TimelineWorkingDuration, _timelinePixelsPerSecond);
        if (position.X < 0 || position.X > contentWidth) return;
        var timelineTime = EditorTimelineLayout.TimeForX(position.X, _timelinePixelsPerSecond);
        _ = SeekEditorToTimelineAsync(timelineTime);
    }

    /// <summary>
    /// Grabs the playhead for dragging: hold the mouse on the timeline and drag
    /// horizontally to change the playhead position; the player only scrubs on
    /// mouse release. The pointer is captured on the content canvas, so
    /// dragging outside the timeline's edge still receives events.
    /// </summary>
    private void StartTimelinePlayheadDrag(PointerRoutedEventArgs e)
    {
        if (_media is null || TimelineContentGrid is null) return;
        if (_timelinePlayheadDragging) return;
        var position = e.GetCurrentPoint(TimelineContentGrid).Position;
        _timelinePlayheadDragging = true;
        _timelinePlayheadDragMoved = false;
        _timelinePlayheadDragOriginX = position.X;
        _timelinePlayheadDragOriginScreenX = Win32Cursor.TryGetScreenPosition(out var screenX, out _) ? screenX : 0d;
        _timelinePlayheadDragTime = Timeline.Value;
        TimelineContentGrid.CapturePointer(e.Pointer);
        _timelinePlayheadDragCapture = TimelineContentGrid;
    }

    /// <summary>
    /// The cursor's current X coordinate in the timeline content while dragging
    /// the playhead: measured from the real screen position of the cursor.
    /// While the pointer is captured, the coordinates attached to pointer
    /// events freeze at the position from the moment of pressing, so reading
    /// straight from the operating system is the only way to track the cursor
    /// (the same approach used by clip edge dragging and cue dragging).
    /// </summary>
    private double CurrentTimelinePlayheadCursorContentX()
    {
        if (TimelineContentGrid is null) return _timelinePlayheadDragOriginX;
        var scale = TimelineContentGrid.XamlRoot?.RasterizationScale ?? 1d;
        if (scale <= 0) return _timelinePlayheadDragOriginX;
        if (!Win32Cursor.TryGetScreenPosition(out var screenX, out _)) return _timelinePlayheadDragOriginX;
        var originScreenX = _timelinePlayheadDragOriginScreenX - _timelinePlayheadDragOriginX * scale;
        return (screenX - originScreenX) / scale;
    }

    /// <summary>
    /// Dragging the playhead: the new position snaps to the media's frame grid,
    /// and the player is not scrubbed until the mouse is released so the drag
    /// does not stutter.
    /// </summary>
    private void TimelineContentGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelinePlayheadDragging || TimelineContentGrid is null) return;
        var contentX = CurrentTimelinePlayheadCursorContentX();
        if (Math.Abs(contentX - _timelinePlayheadDragOriginX) < TimelinePlayheadDragThresholdPixels) return;
        SetTimelinePlayheadTime(EditorTimelineLayout.TimeForX(contentX, _timelinePixelsPerSecond));
    }

    /// <summary>
    /// Releasing the playhead: scrub the player to the new position, save the
    /// position per project, and bring the timeline back to the playhead if it
    /// is outside the viewport.
    /// </summary>
    private async void TimelineContentGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FocusEditorKeyboardSurface();
        if (!_timelinePlayheadDragging) return;
        _timelinePlayheadDragging = false;
        var capture = _timelinePlayheadDragCapture;
        _timelinePlayheadDragCapture = null;
        if (capture is not null) capture.ReleasePointerCapture(e.Pointer);
        else if (TimelineContentGrid is not null) TimelineContentGrid.ReleasePointerCapture(e.Pointer);
        if (!_timelinePlayheadDragMoved) return;
        try { await SeekEditorToTimelineAsync(_timelinePlayheadDragTime); }
        catch (Exception error) { ReportEditorCommandFailure("tua playhead", error); }
    }

    /// <summary>
    /// The pointer was canceled midway (monitor change, focus loss): drop the
    /// drag, do not scrub.
    /// </summary>
    private void TimelineContentGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _timelinePlayheadDragging = false;
        _timelinePlayheadDragCapture = null;
    }

    /// <summary>
    /// Pointer capture lost midway: drop the drag so the timeline does not get
    /// stuck in the "dragging" state (only dropped when this exact drag's
    /// capture is lost).
    /// </summary>
    private void TimelineContentGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelinePlayheadDragging) return;
        if (_timelinePlayheadDragCapture is not null && !ReferenceEquals(sender, _timelinePlayheadDragCapture)) return;
        _timelinePlayheadDragging = false;
        _timelinePlayheadDragCapture = null;
    }

    /// <summary>
    /// Sets the playhead while dragging by hand: only the position on the
    /// timeline is changed; the player is not scrubbed yet.
    /// </summary>
    private void SetTimelinePlayheadTime(double timelineTime)
    {
        var snapped = SnapTimelinePlayhead(timelineTime);
        var target = Math.Clamp(snapped, Timeline.Minimum, Timeline.Maximum);
        if (Math.Abs(target - Timeline.Value) < 1e-9) return;
        _timelinePlayheadDragMoved = true;
        _timelinePlayheadDragTime = target;
        _syncingTimeline = true;
        try { Timeline.Value = target; }
        finally { _syncingTimeline = false; }
    }
}

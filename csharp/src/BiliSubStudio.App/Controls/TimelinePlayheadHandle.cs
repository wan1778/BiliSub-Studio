using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BiliSubStudio.App.Controls;

/// <summary>
/// The timeline playhead handle: a round dot that snaps to time marks on the
/// ruler. The mouse grab region is wider than the round dot (exactly Core's
/// grab radius) so the user can click and hold-drag the playhead, and the
/// cursor switches to the horizontal drag arrow when the mouse moves over the
/// playhead handle.
/// </summary>
public sealed class TimelinePlayheadHandle : Grid
{
    private readonly Ellipse _dot;

    public TimelinePlayheadHandle()
    {
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = new SolidColorBrush(Color.FromArgb(255, 76, 141, 255)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 232, 241, 255)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Add(_dot);
    }

    /// <summary>Cursor shown when the mouse moves over the playhead handle (null = default cursor).</summary>
    public InputCursor? DragCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }

    /// <summary>Stroke of the round dot (used when the playhead handle should be emphasized).</summary>
    public Brush DotStroke
    {
        get => _dot.Stroke;
        set => _dot.Stroke = value;
    }
}

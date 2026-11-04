using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Core;

namespace BiliSubStudio.App.Controls;

/// <summary>
/// A self-built workspace splitter: it consists only of an 8px mouse grab
/// region and a small bar in the middle. No external docking library is used;
/// EditorPage receives the delta through <see cref="DragDelta"/> and clamps the
/// column/row sizes itself.
/// </summary>
public enum WorkspaceSplitterOrientation
{
    Horizontal,
    Vertical,
}

public sealed class WorkspaceSplitter : Grid
{
    private readonly Rectangle _bar;
    private bool _dragging;
    private double _last;
    private WorkspaceSplitterOrientation _orientation = WorkspaceSplitterOrientation.Horizontal;

    public WorkspaceSplitter()
    {
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _bar = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(255, 42, 58, 82)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Add(_bar);
        ApplyOrientation();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <summary>Drag event: the value is the logical pixel delta since the previous move.</summary>
    public event EventHandler<double>? DragDelta;

    /// <summary>Drag-ended event: used to persist the workspace layout.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Drag direction. Horizontal: changes the width; Vertical: changes the height.</summary>
    public WorkspaceSplitterOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            ApplyOrientation();
        }
    }

    private static InputCursor? CursorFor(WorkspaceSplitterOrientation orientation) => orientation == WorkspaceSplitterOrientation.Horizontal
        ? InputCursor.CreateFromCoreCursor(new CoreCursor(CoreCursorType.SizeWestEast, 0))
        : InputCursor.CreateFromCoreCursor(new CoreCursor(CoreCursorType.SizeNorthSouth, 0));

    private void ApplyOrientation()
    {
        if (_orientation == WorkspaceSplitterOrientation.Horizontal)
        {
            Width = 8;
            Height = double.NaN;
            MinWidth = 8;
            MinHeight = 0;
            _bar.Width = 1;
            _bar.Height = 28;
        }
        else
        {
            Width = double.NaN;
            Height = 8;
            MinWidth = 0;
            MinHeight = 8;
            _bar.Width = 28;
            _bar.Height = 1;
        }
        ProtectedCursor = CursorFor(_orientation);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _last = CurrentPosition(e);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var position = CurrentPosition(e);
        var delta = position - _last;
        if (Math.Abs(delta) <= 0.01) return;
        _last = position;
        DragDelta?.Invoke(this, delta);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag(e);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag(e);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void FinishDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private double CurrentPosition(PointerRoutedEventArgs e)
    {
        // Measure against the PARENT, never against this element: while dragging,
        // this splitter moves with the very boundary it resizes, so a self-relative
        // point feeds back into the delta (2x amplification and reversed direction).
        var space = Parent as FrameworkElement ?? this;
        var point = e.GetCurrentPoint(space);
        return _orientation == WorkspaceSplitterOrientation.Horizontal ? point.Position.X : point.Position.Y;
    }
}

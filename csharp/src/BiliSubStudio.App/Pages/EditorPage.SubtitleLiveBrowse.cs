using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private ScrollViewer? _subtitleCueBrowseScrollViewer;
    private bool _subtitleCueBrowseActive;
    private bool _subtitleCueBrowsePointerActive;
    private bool _subtitleCueBrowseCaptureNextView;
    private bool _subtitleCueBrowseRestoring;
    private double _subtitleCueBrowseOffset;

    private void EnsureSubtitleCueLiveBrowseBound()
    {
        if (_translationJobId is not null && _subtitleSource is not null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_translationJobId is null || _subtitleSource is null) return;
                SubtitleCueList.IsEnabled = true;
                SubtitleCueList.IsHitTestVisible = true;
            });
        }
        var scrollViewer = FindSubtitleCueDescendant<ScrollViewer>(SubtitleCueList);
        if (scrollViewer is null || ReferenceEquals(scrollViewer, _subtitleCueBrowseScrollViewer)) return;
        DetachSubtitleCueLiveBrowse();
        _subtitleCueBrowseScrollViewer = scrollViewer;
        scrollViewer.ViewChanged += SubtitleCueBrowse_ViewChanged;
        scrollViewer.PointerPressed += SubtitleCueBrowse_PointerPressed;
        scrollViewer.PointerReleased += SubtitleCueBrowse_PointerReleased;
        scrollViewer.PointerCanceled += SubtitleCueBrowse_PointerCanceled;
        scrollViewer.PointerWheelChanged += SubtitleCueBrowse_PointerWheelChanged;
        scrollViewer.KeyDown += SubtitleCueBrowse_KeyDown;
    }

    private void DetachSubtitleCueLiveBrowse()
    {
        var scrollViewer = _subtitleCueBrowseScrollViewer;
        if (scrollViewer is not null)
        {
            scrollViewer.ViewChanged -= SubtitleCueBrowse_ViewChanged;
            scrollViewer.PointerPressed -= SubtitleCueBrowse_PointerPressed;
            scrollViewer.PointerReleased -= SubtitleCueBrowse_PointerReleased;
            scrollViewer.PointerCanceled -= SubtitleCueBrowse_PointerCanceled;
            scrollViewer.PointerWheelChanged -= SubtitleCueBrowse_PointerWheelChanged;
            scrollViewer.KeyDown -= SubtitleCueBrowse_KeyDown;
        }
        _subtitleCueBrowseScrollViewer = null;
        _subtitleCueBrowseActive = false;
        _subtitleCueBrowsePointerActive = false;
        _subtitleCueBrowseCaptureNextView = false;
        _subtitleCueBrowseRestoring = false;
    }

    private void SubtitleCueBrowse_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_translationJobId is null) return;
        _subtitleCueBrowsePointerActive = true;
        CaptureSubtitleCueBrowseOffset();
    }

    private void SubtitleCueBrowse_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_translationJobId is not null) CaptureSubtitleCueBrowseOffset();
        _subtitleCueBrowsePointerActive = false;
    }

    private void SubtitleCueBrowse_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_translationJobId is not null) CaptureSubtitleCueBrowseOffset();
        _subtitleCueBrowsePointerActive = false;
    }

    private void SubtitleCueBrowse_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_translationJobId is null) return;
        _subtitleCueBrowseActive = true;
        _subtitleCueBrowseCaptureNextView = true;
        DispatcherQueue.TryEnqueue(CaptureSubtitleCueBrowseOffset);
    }

    private void SubtitleCueBrowse_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_translationJobId is null || e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)) return;
        _subtitleCueBrowseActive = true;
        _subtitleCueBrowseCaptureNextView = true;
        DispatcherQueue.TryEnqueue(CaptureSubtitleCueBrowseOffset);
    }

    private void SubtitleCueBrowse_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_subtitleCueBrowseRestoring) return;
        if (_translationJobId is null)
        {
            _subtitleCueBrowseActive = false;
            _subtitleCueBrowseCaptureNextView = false;
            return;
        }
        if (_subtitleCueBrowsePointerActive || _subtitleCueBrowseCaptureNextView)
        {
            CaptureSubtitleCueBrowseOffset();
            _subtitleCueBrowseCaptureNextView = false;
            return;
        }
        if (_subtitleCueBrowseActive) RestoreSubtitleCueBrowseOffset();
    }

    private void CaptureSubtitleCueBrowseOffset()
    {
        var scrollViewer = _subtitleCueBrowseScrollViewer;
        if (scrollViewer is null || _translationJobId is null) return;
        _subtitleCueBrowseOffset = scrollViewer.VerticalOffset;
        _subtitleCueBrowseActive = true;
    }

    private void RestoreSubtitleCueBrowseOffset()
    {
        var scrollViewer = _subtitleCueBrowseScrollViewer;
        if (scrollViewer is null || _subtitleCueBrowseRestoring) return;
        var target = Math.Clamp(_subtitleCueBrowseOffset, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(scrollViewer.VerticalOffset - target) < 0.75) return;
        _subtitleCueBrowseRestoring = true;
        try { scrollViewer.ChangeView(null, target, null, disableAnimation: true); }
        finally { _subtitleCueBrowseRestoring = false; }
    }

    private static T? FindSubtitleCueDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = FindSubtitleCueDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}

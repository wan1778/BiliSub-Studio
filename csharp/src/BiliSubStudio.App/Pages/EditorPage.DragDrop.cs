using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// Drop a video file onto the preview to open it. This is the SAME open path as
/// the "Mở video" button (probe, project create/restore, preview restore), so
/// the button keeps working and nothing new can be opened inconsistently.
/// </summary>
public sealed partial class EditorPage
{
    // What Windows Explorer hands over when a video file is dragged in.
    private static readonly string[] DroppableVideoExtensions =
    [
        ".mp4", ".mkv", ".mov", ".m4v", ".ts", ".m2ts", ".avi", ".flv", ".wmv", ".webm",
    ];

    private static readonly string[] DroppableSubtitleExtensions = [".srt"];

    private static bool IsDroppedVideo(string path) =>
        DroppableVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsDroppedSubtitle(string path) =>
        DroppableSubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dropping files on the timeline is the editor-style import: videos are
    /// appended (with their sidecar SRTs) and SRTs are bound to the matching
    /// clip. Dropping a single video on the preview still opens it as a project.
    /// </summary>
    private void TimelinePanel_DragOver(object sender, DragEventArgs e)
    {
        if (!AcceptsDroppedVideo(e.DataView)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _project is null
            ? "Thả để mở project"
            : "Thả để thêm video / gắn SRT";
    }

    private void TimelinePanel_DragLeave(object sender, RoutedEventArgs e) { }

    private async void TimelinePanel_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<string> files;
            try
            {
                files = await DroppedMediaPathsAsync(e.DataView);
            }
            catch (Exception error)
            {
                StatusText.Text = "Không đọc được file kéo vào: " + error.Message;
                return;
            }
            var videos = files.Where(IsDroppedVideo).ToArray();
            var subtitles = files.Where(IsDroppedSubtitle).ToArray();
            if (videos.Length == 0 && subtitles.Length == 0)
            {
                StatusText.Text = "Chỉ nhận file video (MP4, MKV, MOV…) hoặc SRT.";
                return;
            }
            try
            {
                if (videos.Length > 0)
                {
                    // A batch of videos and their SRTs dropped together is ONE import:
                    // ImportVideosAsync binds each explicit SRT to its own video.
                    if (_project is null)
                    {
                        await OpenVideoSetAsync(videos);
                        if (subtitles.Length > 0) await AttachDroppedSubtitlesAsync(subtitles);
                    }
                    else await AppendVideosAsync(videos, subtitles);
                    return;
                }
                if (subtitles.Length > 0) await AttachDroppedSubtitlesAsync(subtitles);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                StatusText.Text = "Không nhập được file kéo vào: " + error.Message;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async Task<IReadOnlyList<string>> DroppedMediaPathsAsync(DataPackageView view)
    {
        if (!AcceptsDroppedVideo(view)) return [];
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await view.GetStorageItemsAsync();
        }
        catch (Exception)
        {
            return [];
        }
        return items.OfType<StorageFile>()
            .Select(file => file.Path)
            .Where(path => IsDroppedVideo(path) || IsDroppedSubtitle(path))
            .ToArray();
    }

    private void PreviewSurface_DragEnter(object sender, DragEventArgs e)
    {
        if (!AcceptsDroppedVideo(e.DataView)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Thả để mở video";
        PreviewDropHint.Visibility = Visibility.Visible;
    }

    private void PreviewSurface_DragOver(object sender, DragEventArgs e)
    {
        if (!AcceptsDroppedVideo(e.DataView)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        PreviewDropHint.Visibility = Visibility.Visible;
    }

    private void PreviewSurface_DragLeave(object sender, RoutedEventArgs e) =>
        PreviewDropHint.Visibility = Visibility.Collapsed;

    private async void PreviewSurface_Drop(object sender, DragEventArgs e)
    {
        PreviewDropHint.Visibility = Visibility.Collapsed;
        string? path;
        try
        {
            path = await FirstDroppedVideoPathAsync(e.DataView);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không đọc được file kéo vào: " + error.Message;
            return;
        }
        if (path is null)
        {
            StatusText.Text = "Chỉ nhận file video (MP4, MKV, MOV...); dùng nút Mở video nếu cần chọn lại.";
            return;
        }
        try
        {
            await OpenVideoPathAsync(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được video: " + error.Message;
        }
    }

    private static bool AcceptsDroppedVideo(DataPackageView view)
    {
        try
        {
            return view.Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string?> FirstDroppedVideoPathAsync(DataPackageView view)
    {
        if (!AcceptsDroppedVideo(view)) return null;
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await view.GetStorageItemsAsync();
        }
        catch (Exception)
        {
            return null;
        }
        foreach (var item in items)
        {
            if (item is not StorageFile file) continue;
            var extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (DroppableVideoExtensions.Contains(extension)) return file.Path;
        }
        return null;
    }
}

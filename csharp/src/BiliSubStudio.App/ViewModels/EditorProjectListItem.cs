using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BiliSubStudio.App.ViewModels;

public sealed class EditorProjectListItem(EditorProjectSummary summary) : BindableBase
{
    private ImageSource? _thumbnail;

    public EditorProjectSummary Summary { get; } = summary;
    public string Id => Summary.Id;
    public string Name => Summary.Name;
    public string SourcePath => Summary.SourcePath;
    public string FileName => Path.GetFileName(Summary.SourcePath);
    public string SrtStatus => Summary.HasSubtitle ? "Có SRT Việt" : "Chưa có SRT";
    public string DurationText => FormatDuration(Summary.Duration);
    public string FileSizeText => FormatBytes(Summary.SourceSize);
    public string ResolutionText => Summary.Width > 0 && Summary.Height > 0
        ? $"{Summary.Width}×{Summary.Height}"
        : "Chưa có kích thước";
    public string UpdatedText => "Sửa lúc " + Summary.UpdatedUtc.ToLocalTime().ToString("HH:mm dd/MM/yyyy");
    public string SourceStatus => Summary.SourceExists ? "Video nguồn sẵn sàng" : "Không tìm thấy video nguồn";
    public Visibility SourceWarningVisibility => Summary.SourceExists ? Visibility.Collapsed : Visibility.Visible;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "--:--";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "--";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

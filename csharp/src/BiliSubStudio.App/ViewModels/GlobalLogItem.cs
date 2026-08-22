using BiliSubStudio.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace BiliSubStudio.App.ViewModels;

public sealed class GlobalLogItem
{
    public GlobalLogItem(AppLogEntry entry)
    {
        Sequence = entry.Sequence;
        Time = entry.Timestamp.ToString("HH:mm:ss");
        Source = entry.Source;
        Message = entry.Message;
        Level = entry.Level;
    }

    public long Sequence { get; }
    public string Time { get; }
    public string Source { get; }
    public string Message { get; }
    public AppLogLevel Level { get; }
    public Visibility InfoVisibility => Level == AppLogLevel.Info ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => Level == AppLogLevel.Warning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => Level == AppLogLevel.Error ? Visibility.Visible : Visibility.Collapsed;
}

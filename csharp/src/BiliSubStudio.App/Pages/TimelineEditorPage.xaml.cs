using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Timeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

// G2 dev shell page. It owns only V2 session/store state and never touches
// EditorPage mutable state, schema-7 persistence, or production Data/Projects.
public sealed partial class TimelineEditorPage : Page
{
    private readonly TimelineEditorSession _session;

    public TimelineEditorPage(BiliSubApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _session = new TimelineEditorSession(TimelineProjectStoreV2.FromAppPaths(application.Paths));
        InitializeComponent();
        RootText.Text = "Test root: " + _session.Store.Root;
        _ = RefreshAsync();
    }

    internal TimelineEditorSession Session => _session;

    private async void CreateTestProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var project = await _session.CreateAsync("Timeline thử nghiệm", CancellationToken.None);
            var plan = TimelineCompiler.Compile(project);
            ShellStatusText.Text = $"Đã tạo {project.Name} ({project.Id[..8]}…). Plan: preview/export dùng chung timeline plan {plan.CreatedUtcTicks}.";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShellStatusText.Text = "Không tạo được project thử nghiệm: " + error.Message;
        }
    }

    private async void RefreshList_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var projects = await _session.Store.ListAsync(CancellationToken.None);
            ProjectList.ItemsSource = projects
                .Select(project => $"{project.Name} · {project.Id[..8]}… · {project.UpdatedUtc:yyyy-MM-dd HH:mm:ss} UTC")
                .ToArray();
            if (string.IsNullOrWhiteSpace(ShellStatusText.Text))
                ShellStatusText.Text = $"Có {projects.Count} project thử nghiệm trong test root tách biệt.";
        }
        catch (Exception error)
        {
            ShellStatusText.Text = "Không đọc được test root: " + error.Message;
        }
    }
}

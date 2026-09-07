using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Timeline;
using BiliSubStudio.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

// G14 promotion page. It owns only V2 session/store state plus a real export
// path, and never touches EditorPage mutable state, schema-7 persistence, or
// production Data/Projects. The classic Editor stays the default fallback.
public sealed partial class TimelineEditorPage : Page
{
    private readonly TimelineEditorSession _session;
    private readonly BiliSubApplication _application;
    private readonly IFolderPickerService _folderPicker;
    private IReadOnlyList<EditorProjectSummary> _classicProjects = [];
    private IReadOnlyList<TimelineProjectV2> _v2Projects = [];

    public TimelineEditorPage(BiliSubApplication application, IFolderPickerService folderPicker)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(folderPicker);
        _application = application;
        _folderPicker = folderPicker;
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

    private async void RefreshClassic_Click(object sender, RoutedEventArgs e)
    {
        await RefreshClassicAsync();
    }

    private async Task RefreshClassicAsync()
    {
        try
        {
            _classicProjects = await _application.ListEditorProjectsAsync(CancellationToken.None);
            ClassicProjectCombo.ItemsSource = _classicProjects
                .Select(summary => $"{summary.Name} · {summary.Id} · {(summary.SourceExists ? "media OK" : "thiếu media")}")
                .ToArray();
            if (_classicProjects.Count == 0)
                PromotionStatusText.Text = "Không có project classic nào để copy.";
        }
        catch (Exception error)
        {
            PromotionStatusText.Text = "Không đọc được danh sách classic: " + error.Message;
        }
    }

    // G11 readiness entry. Copies one real schema-7 project into the isolated
    // V2 store with hash evidence; the classic project, its file and the
    // media are never written. The classic Editor stays the fallback: open it
    // from the Chỉnh video tab as before.
    private async void CopyClassicProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClassicProjectCombo.SelectedIndex < 0 || ClassicProjectCombo.SelectedIndex >= _classicProjects.Count)
            {
                PromotionStatusText.Text = "Hãy chọn một project classic trước khi copy.";
                return;
            }
            var summary = _classicProjects[ClassicProjectCombo.SelectedIndex];
            if (!summary.SourceExists)
            {
                PromotionStatusText.Text = "Thiếu source media; relink project classic trước khi copy sang V2.";
                return;
            }
            var source = await _application.LoadEditorProjectByIdAsync(summary.Id, CancellationToken.None);
            var probe = await _application.Media.ProbeAsync(summary.SourcePath, CancellationToken.None);
            var hasAudio = await _application.Media.ProbeHasAudioAsync(summary.SourcePath, CancellationToken.None);
            var jsonPath = _application.GetEditorProjectPath(summary.SourcePath);
            var promotion = new TimelinePromotionService(_session.Store);
            var report = await promotion.PromoteAsync(
                source,
                jsonPath,
                summary.SourcePath,
                probe.Width,
                probe.Height,
                probe.Duration,
                hasAudio,
                new TimelinePromotionRequest(BackupTagBox.Text, BackupManifestBox.Text),
                CancellationToken.None);
            var warnings = report.Warnings.Count == 0
                ? "không có cảnh báo"
                : string.Join(", ", report.Warnings);
            PromotionStatusText.Text =
                $"Đã copy {source.Name} sang V2 ({report.Project.Id[..8]}…). " +
                $"Cảnh báo: {warnings}. Nguồn và bản classic nguyên vẹn; mở fallback ở tab Chỉnh video.";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            PromotionStatusText.Text = "Không copy được project classic: " + error.Message;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            _v2Projects = await _session.Store.ListAsync(CancellationToken.None);
            ProjectList.ItemsSource = _v2Projects
                .Select(project => $"{project.Name} · {project.Id[..8]}… · {project.UpdatedUtc:yyyy-MM-dd HH:mm:ss} UTC")
                .ToArray();
            if (string.IsNullOrWhiteSpace(ShellStatusText.Text))
                ShellStatusText.Text = $"Có {_v2Projects.Count} project thử nghiệm trong test root tách biệt.";
        }
        catch (Exception error)
        {
            ShellStatusText.Text = "Không đọc được test root: " + error.Message;
        }
    }

    // G14 promotion export. Exports the selected V2 project through the real
    // segment/concat/mix pipeline into a user-picked folder. Mirrors the
    // BiliSubApplication.RunJobAsync ownership: the wrapper below owns the
    // terminal job state, the service only unwinds renderer/process/cleanup.
    private async void ExportProject_Click(object sender, RoutedEventArgs e)
    {
        AppJob? job = null;
        try
        {
            if (ProjectList.SelectedIndex < 0 || ProjectList.SelectedIndex >= _v2Projects.Count)
            {
                ExportStatusText.Text = "Hãy chọn một project V2 trong danh sách trước khi xuất.";
                return;
            }
            var summary = _v2Projects[ProjectList.SelectedIndex];
            var outputDirectory = await _folderPicker.PickFolderAsync(
                _application.Paths.DefaultDownloads);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                ExportStatusText.Text = "Đã hủy chọn thư mục xuất.";
                return;
            }
            ExportStatusText.Text = $"Đang chuẩn bị xuất {summary.Name}…";
            var project = await _session.OpenAsync(summary.Id, CancellationToken.None);
            var plan = TimelineCompiler.Compile(project);
            var ffmpeg = await _application.Tools.EnsureFfmpegAsync(CancellationToken.None);
            var ffprobe = await _application.Tools.EnsureFfprobeAsync(CancellationToken.None);
            var service = new TimelineExportService(
                _application.Paths,
                new TimelineFfmpegSegmentRenderer(ffmpeg),
                new TimelineExportConcatList(_application.Paths),
                new TimelineFfmpegConcatenator(ffmpeg, ffprobe),
                new TimelineFfmpegFinalMixer(ffmpeg, ffprobe));
            job = _application.Jobs.Create("timeline-export", cleanupAwareCancel: true);
            var fileName = FileNamePolicy.Sanitize(summary.Name + "_timeline.mp4", "Timeline.mp4");
            var result = await service.ExportAsync(
                job, plan, project, outputDirectory, fileName, CancellationToken.None);
            job.Finish(null, "Xuất Timeline hoàn tất.");
            ExportStatusText.Text = $"Đã xuất {result.SegmentCount} segment " +
                $"({result.OutputDurationUs / 1_000_000.0:0.###}s) ra {result.OutputPath}.";
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (job is not null && job.CancellationToken.IsCancellationRequested)
        {
            if (job is not null && !job.Snapshot().Done) job.CancelComplete();
            ExportStatusText.Text = "Đã hủy xuất Timeline và dọn dữ liệu dở.";
        }
        catch (Exception error)
        {
            if (job is not null && !job.Snapshot().Done) job.Finish(error, error.Message);
            ExportStatusText.Text = "Không xuất được project Timeline: " + error.Message;
        }
    }
}

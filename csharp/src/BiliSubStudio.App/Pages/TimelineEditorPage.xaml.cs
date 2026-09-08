using BiliSubStudio.Core.Application;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.IO;
using BiliSubStudio.Core.Jobs;
using BiliSubStudio.Core.Media;
using BiliSubStudio.Core.Timeline;
using BiliSubStudio.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace BiliSubStudio.App.Pages;

// G14 promotion page. It owns only V2 session/store state plus a real export
// path, and never touches EditorPage mutable state, schema-7 persistence, or
// production Data/Projects. The classic Editor stays the default fallback.
public sealed partial class TimelineEditorPage : Page
{
    private readonly TimelineEditorSession _session;
    private readonly BiliSubApplication _application;
    private readonly IFolderPickerService _folderPicker;
    private readonly IFilePickerService? _filePicker;
    private readonly TimelineEditingController _editor;
    private IReadOnlyList<EditorProjectSummary> _classicProjects = [];
    private IReadOnlyList<TimelineProjectV2> _v2Projects = [];
    private TimelinePointerGesture? _timelineDrag;
    private bool _syncingClipSelection;
    private bool _syncingProjectSelection;
    private bool _savingProject;
    private bool _openingProject;
    private readonly DispatcherTimer _timelineAutoScrollTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50),
    };
    private double? _timelineDragViewportX;
    private readonly DispatcherTimer _timelineAutosaveTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private bool _timelineAutosavePending;
    private bool _timelineAutosaveBusy;
    private bool _timelineViewportRenderQueued;
    private long? _pendingTimelineZoomCenterUs;
    private double _timelinePixelsPerSecond = TimelineViewport.DefaultPixelsPerSecond;

    private const double TimelineTrackTop = 28;
    private const double TimelineTrackHeight = 110;
    private const double TimelineHandleTolerance = 10;
    private const double TimelineDragThreshold = 6;
    private const double TimelineSecondaryTrackHeight = 34;
    private const double TimelineSecondaryTop = TimelineTrackTop + TimelineTrackHeight + 12;
    private const double TimelineTrackLabelWidth = 108;

    private sealed record TimelinePointerGesture(
        uint PointerId,
        string ClipId,
        string ProjectId,
        long Revision,
        TimelineDragKind Kind,
        double PressX,
        int OriginalPosition,
        int CandidatePosition,
        TimelineTrimProposal OriginalTrim,
        TimelineTrimProposal CandidateTrim,
        bool Dragged);

    public TimelineEditorPage(BiliSubApplication application, IFolderPickerService folderPicker, IFilePickerService? filePicker = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(folderPicker);
        _application = application;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _session = new TimelineEditorSession(TimelineProjectStoreV2.FromAppPaths(application.Paths));
        _editor = new TimelineEditingController(_session.Store);
        InitializeComponent();
        TimelineZoomSlider.ValueChanged += TimelineZoomSlider_ValueChanged;
        TimelineScroller.ViewChanged += TimelineScroller_ViewChanged;
        TimelineSurface.SizeChanged += TimelineSurface_SizeChanged;
        InitializeTimelinePlayback();
        _timelineAutoScrollTimer.Tick += (_, _) =>
        {
            if (_timelineDragViewportX is { } x)
            {
                AutoScrollTimeline(x);
                UpdateTimelineDragProposal(TimelineScroller.HorizontalOffset + x);
            }
        };
        RootText.Text = "Test root: " + _session.Store.Root;
        _timelineAutosaveTimer.Tick += TimelineAutosave_Tick;
        _ = RefreshAsync();
    }

    internal TimelineEditorSession Session => _session;

    private async void CreateTestProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var kind = TemplateList.SelectedIndex switch
            {
                1 => TimelineTemplateKind.PiPOverlay,
                2 => TimelineTemplateKind.TextOverVideo,
                3 => TimelineTemplateKind.Reaction,
                _ => TimelineTemplateKind.Default,
            };
            var project = await _session.CreateAsync("Timeline thử nghiệm", CancellationToken.None);
            if (kind is not TimelineTemplateKind.Default)
            {
                var layout = TimelineTemplates.Create(
                    kind, project.Name, 1920, 1080, 30);
                project = project with
                {
                    Tracks = layout.Tracks,
                };
                project.Validate();
                await _session.SaveAsync(project, CancellationToken.None);
            }
            var plan = TimelineCompiler.Compile(project);
            ShellStatusText.Text = $"Đã tạo {project.Name} ({project.Id[..8]}…) với {project.Tracks.Count} track phụ. Plan: preview/export dùng chung timeline plan {plan.CreatedUtcTicks}.";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShellStatusText.Text = "Không tạo được project thử nghiệm: " + error.Message;
        }
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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

    // Empty-state shell: hide editor workspace until a project is open so the
    // production Timeline tab never presents a wall of disabled controls.
    private void UpdateShellVisibility()
    {
        var hasProject = _editor.HasOpenProject;
        EmptyStatePanel.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
        InspectorClipLabel.Text = hasProject
            ? (_editor.SelectedClipId is null
                ? "Chưa chọn clip."
                : "Clip đang chọn: " + _editor.SelectedClipId[..8] + "…")
            : "Chưa có project để mở.";
        UndoButton.IsEnabled = hasProject && _editor.UndoCount > 0;
        RedoButton.IsEnabled = hasProject && _editor.RedoCount > 0;
        SaveProjectButton.IsEnabled = hasProject;
        ExportProjectButton.IsEnabled = hasProject;
    }

    // G15 editing interactions. Clip edits run through TimelineEditingController
    // (selection, atomic undo/redo, dirty save); rejected edits keep state and
    // report the reason instead of committing partial work.
    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var ownsOpen = false;
        try
        {
            if (_syncingProjectSelection || _timelineClosing
                || ProjectList.SelectedIndex < 0
                || ProjectList.SelectedIndex >= _v2Projects.Count)
                return;
            if (_openingProject)
            {
                RestoreSelectedProject();
                return;
            }
            var target = _v2Projects[ProjectList.SelectedIndex];
            if (_editor.HasOpenProject
                && string.Equals(_editor.Project.Id, target.Id, StringComparison.Ordinal))
                return;
            _openingProject = true;
            ownsOpen = true;
            ProjectList.IsEnabled = false;
            var discard = false;
            CancelActiveTimelineDrag("Đã hủy thao tác kéo để đổi project.");
            if (_editor.HasOpenProject && _editor.IsDirty
                && !string.Equals(_editor.Project.Id, target.Id, StringComparison.Ordinal))
            {
                var choice = await ConfirmProjectSwitchAsync();
                if (choice == ContentDialogResult.None)
                {
                    RestoreSelectedProject();
                    EditStatusText.Text = "Đã giữ project hiện tại và các thay đổi chưa lưu.";
                    return;
                }
                if (choice == ContentDialogResult.Primary)
                    await SaveOpenProjectAsync();
                else
                    discard = true;
            }
            await StopTimelinePreviewAsync(updateStatus: false);
            await _editor.OpenAsync(target.Id, CancellationToken.None, discard);
            CancelActiveTimelineDrag(null);
            RestoreSelectedProject();
            RefreshClips();
            EditStatusText.Text = $"Đã mở {_editor.Project.Name} để sửa " +
                $"({_editor.Clips.Count} clip, undo {_editor.UndoCount}, redo {_editor.RedoCount}).";
        }
        catch (Exception error)
        {
            RestoreSelectedProject();
            EditStatusText.Text = "Không mở được project để sửa: " + error.Message;
        }
        finally
        {
            if (ownsOpen)
            {
                _openingProject = false;
                ProjectList.IsEnabled = true;
            }
        }
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_syncingClipSelection || _openingProject || _timelineClosing
                || !_editor.HasOpenProject
                || ClipList.SelectedIndex < 0
                || ClipList.SelectedIndex >= _editor.Clips.Count)
                return;
            CancelActiveTimelineDrag("Đã hủy thao tác kéo để đổi clip.");
            _editor.SelectClip(_editor.Clips[ClipList.SelectedIndex].Id);
            ReportEditState("Đã chọn clip.");
        }
        catch (Exception error)
        {
            EditStatusText.Text = "Không chọn được clip: " + error.Message;
        }
    }

    private void TrimClip_Click(object sender, RoutedEventArgs e) =>
        RunEdit("Trim xong.", () =>
        {
            _editor.TrimSelected(
                TimelineTime.FromSeconds(ParseSeconds(TrimInBox.Text, "SourceIn")),
                TimelineTime.FromSeconds(ParseSeconds(TrimOutBox.Text, "SourceOut")));
        });

    private void SplitClip_Click(object sender, RoutedEventArgs e) =>
        RunEdit("Split xong.", () =>
        {
            _editor.SplitSelected(
                TimelineTime.FromSeconds(ParseSeconds(SplitAtBox.Text, "điểm cắt")));
        });

    private void DeleteClip_Click(object sender, RoutedEventArgs e) =>
        RunEdit("Đã xóa clip.", () => _editor.DeleteSelected());

    private void MoveUp_Click(object sender, RoutedEventArgs e) =>
        RunEdit("Đã chuyển clip lên.", () => MoveSelectedBy(-1));

    private void MoveDown_Click(object sender, RoutedEventArgs e) =>
        RunEdit("Đã chuyển clip xuống.", () => MoveSelectedBy(1));

    private void MoveSelectedBy(int delta)
    {
        var clips = _editor.Clips;
        var index = clips.ToList().FindIndex(clip =>
            string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException("Chưa chọn clip Timeline để edit.");
        _editor.MoveSelected(Math.Clamp(index + delta, 0, clips.Count - 1));
    }

    private void Undo_Click(object sender, RoutedEventArgs e) =>
        RunHistoryEdit(undo: true);

    private void Redo_Click(object sender, RoutedEventArgs e) =>
        RunHistoryEdit(undo: false);

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_savingProject || _openingProject || _timelineClosing)
            return;
        try
        {
            CancelActiveTimelineDrag("Đã hủy thao tác kéo trước khi lưu.");
            await SaveOpenProjectAsync();
            await RefreshAsync();
            RestoreSelectedProject();
            ReportEditState(_editor.IsDirty
                ? "Đã lưu snapshot; có thay đổi mới chưa lưu."
                : "Đã lưu project.");
        }
        catch (Exception error)
        {
            EditStatusText.Text = "Không lưu được project: " + error.Message;
        }
    }

    private bool RunEdit(string successMessage, Action operation)
    {
        try
        {
            if (_savingProject || _openingProject || _timelineClosing || TimelinePreviewLocked)
                throw new InvalidOperationException(
                    TimelinePreviewLocked
                        ? "Hãy dừng processed preview trước khi chỉnh sửa."
                        : "Project đang được lưu hoặc mở; hãy đợi thao tác hoàn tất.");
            CancelActiveTimelineDrag("Đã hủy thao tác kéo để chạy lệnh khác.");
            operation();
            RefreshClips();
            ReportEditState(successMessage);
            return true;
        }
        catch (Exception error)
        {
            EditStatusText.Text = "Không sửa được clip: " + error.Message;
            return false;
        }
    }

    private void RunHistoryEdit(bool undo)
    {
        try
        {
            if (_savingProject || _openingProject || _timelineClosing || TimelinePreviewLocked)
                throw new InvalidOperationException(
                    TimelinePreviewLocked
                        ? "Hãy dừng processed preview trước khi undo/redo."
                        : "Project đang được lưu hoặc mở; hãy đợi thao tác hoàn tất.");
            CancelActiveTimelineDrag("Đã hủy thao tác kéo để chạy undo/redo.");
            var applied = undo ? _editor.Undo() : _editor.Redo();
            RefreshClips();
            ReportEditState(applied
                ? (undo ? "Đã undo." : "Đã redo.")
                : (undo ? "Hết lịch sử undo." : "Hết lịch sử redo."));
        }
        catch (Exception error)
        {
            EditStatusText.Text = "Không sửa được clip: " + error.Message;
        }
    }

    private async Task SaveOpenProjectAsync()
    {
        if (_savingProject)
            throw new InvalidOperationException("Project Timeline đang được lưu.");
        _savingProject = true;
        try
        {
            await _editor.SaveAsync(CancellationToken.None);
        }
        finally
        {
            _savingProject = false;
        }
        _timelineAutosavePending = false;
        UpdateTimelineAutosaveText();
    }

    // G19 autosave: one debounce timer mirrors the classic PROJECT-02 owner.
    // It never runs while preview playback locks edits, while a save/open is
    // in flight, or while the app is closing; a missed tick just stays pending.
    private void QueueTimelineAutosave()
    {
        if (!_editor.HasOpenProject || !_editor.IsDirty || _timelineClosing)
            return;
        _timelineAutosavePending = true;
        if (!_timelineAutosaveTimer.IsEnabled)
            _timelineAutosaveTimer.Start();
        UpdateTimelineAutosaveText();
    }

    private async void TimelineAutosave_Tick(object? sender, object e)
    {
        if (_timelineAutosaveBusy
            || !_timelineAutosavePending
            || !_editor.HasOpenProject
            || !_editor.IsDirty
            || _savingProject
            || _openingProject
            || TimelinePreviewLocked
            || _timelineClosing)
        {
            if (!_timelineAutosavePending && _timelineAutosaveTimer.IsEnabled)
                _timelineAutosaveTimer.Stop();
            return;
        }
        _timelineAutosaveBusy = true;
        if (_timelinePageLoaded)
            RefreshTimelinePreviewControls();
        try
        {
            await _editor.SaveAsync(CancellationToken.None);
            _timelineAutosavePending = false;
            if (_timelinePageLoaded)
                EditStatusText.Text = "Tự động lưu project Timeline.";
        }
        catch
        {
            // Stay dirty and pending; the next tick retries and the explicit
            // Save button surfaces the real error.
        }
        finally
        {
            _timelineAutosaveBusy = false;
            if (!_timelineAutosavePending && _timelineAutosaveTimer.IsEnabled)
                _timelineAutosaveTimer.Stop();
            if (_timelinePageLoaded)
            {
                RefreshClips();
                UpdateTimelineAutosaveText();
            }
        }
    }

    private void UpdateTimelineAutosaveText()
    {
        if (!_editor.HasOpenProject)
        {
            TimelineAutosaveText.Text = "Tự động lưu: chưa có project.";
            return;
        }
        TimelineAutosaveText.Text = _timelineAutosavePending
            ? "Tự động lưu: đang chờ ghi project..."
            : _editor.IsDirty
                ? "Tự động lưu: có thay đổi chưa ghi."
                : "Tự động lưu: project đã ghi trên đĩa.";
    }

    private async Task<ContentDialogResult> ConfirmProjectSwitchAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Project có thay đổi chưa lưu",
            Content = "Lưu thay đổi trước khi mở project khác?",
            PrimaryButtonText = "Lưu",
            SecondaryButtonText = "Bỏ thay đổi",
            CloseButtonText = "Hủy",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync();
    }

    private void RestoreSelectedProject()
    {
        if (!_editor.HasOpenProject)
            return;
        var index = _v2Projects.ToList().FindIndex(project =>
            string.Equals(project.Id, _editor.Project.Id, StringComparison.Ordinal));
        _syncingProjectSelection = true;
        try
        {
            ProjectList.SelectedIndex = index;
        }
        finally
        {
            _syncingProjectSelection = false;
        }
    }

    private void CancelActiveTimelineDrag(string? message)
    {
        if (_timelineDrag is null)
            return;
        _timelineDrag = null;
        _timelineDragViewportX = null;
        _timelineAutoScrollTimer.Stop();
        TimelineSurface.ReleasePointerCaptures();
        RenderTimeline();
        if (!string.IsNullOrWhiteSpace(message))
            TimelineDragStatusText.Text = message;
    }

    private void RefreshClips()
    {
        _syncingClipSelection = true;
        try
        {
            if (!_editor.HasOpenProject)
            {
                ClipList.ItemsSource = Array.Empty<string>();
                ClipList.SelectedIndex = -1;
                RenderTimeline();
                return;
            }
            ClipList.ItemsSource = _editor.Clips
                .Select((clip, index) =>
                    $"[{index}] {clip.Id[..8]}… seq {TimelineTime.ToSeconds(clip.TimelineStartUs):0.###}s" +
                    $"..{TimelineTime.ToSeconds(clip.TimelineEndUs):0.###}s" +
                    $" src {TimelineTime.ToSeconds(clip.SourceInUs):0.###}s" +
                    $"..{TimelineTime.ToSeconds(clip.SourceOutUs):0.###}s x{clip.PlaybackRate:0.###}" +
                    (string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal) ? " ●" : string.Empty))
                .ToArray();
            ClipList.SelectedIndex = _editor.Clips.ToList().FindIndex(clip =>
                string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal));
            var selected = _editor.Clips.FirstOrDefault(clip =>
                string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal));
            if (selected is not null)
            {
                TrimInBox.Text = TimelineTime.FormatSeconds(selected.SourceInUs);
                TrimOutBox.Text = TimelineTime.FormatSeconds(selected.SourceOutUs);
                InspectorClipLabel.Text =
                    $"[{_editor.Clips.ToList().IndexOf(selected)}] {selected.Id[..8]}… " +
                    $"{TimelineTime.ToSeconds(selected.TimelineStartUs):0.###}s..{TimelineTime.ToSeconds(selected.TimelineEndUs):0.###}s " +
                    $"x{selected.PlaybackRate:0.###}";
            }
            else
            {
                InspectorClipLabel.Text = "Chưa chọn clip.";
            }
            UpdateShellVisibility();
            RenderTimeline();
        }
        finally
        {
            _syncingClipSelection = false;
            RefreshTimelinePreviewControls();
        }
    }

    // G17 direct manipulation. Move events only update transient proposals;
    // release commits one controller transaction and cancel commits nothing.
    private void TimelineSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_editor.HasOpenProject || _timelineDrag is not null || _savingProject
            || _openingProject || _timelineClosing || TimelinePreviewLocked)
            return;
        var point = e.GetCurrentPoint(TimelineSurface);
        var pointerType = e.Pointer.PointerDeviceType;
        if (pointerType == Microsoft.UI.Input.PointerDeviceType.Mouse
            ? !point.Properties.IsLeftButtonPressed
            : !point.IsInContact)
            return;
        var touch = pointerType == Microsoft.UI.Input.PointerDeviceType.Touch;
        var touchGrip = touch ? HitTestTimelineTouchGrip(point.Position) : null;
        (TimelineClip? Clip, TimelineDragKind Kind) hit = touchGrip is null
            ? HitTestTimeline(point.Position, touch ? 22 : TimelineHandleTolerance)
            : (Clip: touchGrip, Kind: TimelineDragKind.Reorder);
        if (hit.Clip is null || hit.Kind == TimelineDragKind.None)
            return;
        if (touch && hit.Kind == TimelineDragKind.Reorder && touchGrip is null)
            return;

        _editor.SelectClip(hit.Clip.Id);
        var position = _editor.Clips.ToList().FindIndex(clip =>
            string.Equals(clip.Id, hit.Clip.Id, StringComparison.Ordinal));
        var trim = new TimelineTrimProposal(hit.Clip.SourceInUs, hit.Clip.SourceOutUs);
        _timelineDrag = new TimelinePointerGesture(
            e.Pointer.PointerId,
            hit.Clip.Id,
            _editor.Project.Id,
            _editor.Revision,
            hit.Kind,
            point.Position.X,
            position,
            position,
            trim,
            trim,
            false);
        if (!TimelineSurface.CapturePointer(e.Pointer))
        {
            _timelineDrag = null;
            return;
        }
        RefreshClips();
        TimelineDragStatusText.Text = hit.Kind == TimelineDragKind.Reorder
            ? "Đang kéo clip; thả để đổi thứ tự."
            : "Đang kéo mép clip; trim sẽ snap theo frame của sequence.";
        e.Handled = true;
    }

    private void TimelineSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var gesture = _timelineDrag;
        if (gesture is null || gesture.PointerId != e.Pointer.PointerId)
            return;
        if (!_editor.HasOpenProject
            || !string.Equals(_editor.Project.Id, gesture.ProjectId, StringComparison.Ordinal)
            || _editor.Revision != gesture.Revision
            || !_editor.Clips.Any(item => string.Equals(item.Id, gesture.ClipId, StringComparison.Ordinal)))
        {
            FinishTimelineDrag(e, commit: false);
            return;
        }
        var point = e.GetCurrentPoint(TimelineSurface);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            ? !point.Properties.IsLeftButtonPressed
            : !point.IsInContact)
            return;
        if (!gesture.Dragged && Math.Abs(point.Position.X - gesture.PressX) < TimelineDragThreshold)
            return;

        _timelineDragViewportX = e.GetCurrentPoint(TimelineScroller).Position.X;
        if (!_timelineAutoScrollTimer.IsEnabled)
            _timelineAutoScrollTimer.Start();
        AutoScrollTimeline(_timelineDragViewportX.Value);
        UpdateTimelineDragProposal(TimelineScroller.HorizontalOffset + _timelineDragViewportX.Value);
        e.Handled = true;
    }

    private void UpdateTimelineDragProposal(double pointerCanvasX)
    {
        var gesture = _timelineDrag;
        if (gesture is null)
            return;
        var firstMove = !gesture.Dragged;
        var clip = _editor.Clips.FirstOrDefault(item =>
            string.Equals(item.Id, gesture.ClipId, StringComparison.Ordinal));
        if (clip is null)
            return;
        gesture = gesture with { Dragged = true };
        _timelineDrag = gesture;
        if (gesture.Kind == TimelineDragKind.Reorder)
        {
            var target = TimelineDragMath.ReorderPosition(
                _editor.Clips, gesture.ClipId, PointerSequenceUs(pointerCanvasX));
            if (!firstMove && target == gesture.CandidatePosition)
                return;
            gesture = gesture with { CandidatePosition = target };
            TimelineDragStatusText.Text = $"Vị trí mới: {target + 1}/{_editor.Clips.Count}.";
        }
        else
        {
            var asset = _editor.Project.Assets.Single(item =>
                string.Equals(item.Id, clip.AssetId, StringComparison.Ordinal));
            var proposal = TimelineDragMath.TrimProposal(
                clip,
                asset,
                _editor.Project.Sequence!.OutputFrameRate,
                gesture.Kind,
                PointerSequenceUs(pointerCanvasX));
            if (!firstMove && proposal == gesture.CandidateTrim)
                return;
            gesture = gesture with { CandidateTrim = proposal };
            TimelineDragStatusText.Text =
                $"Source {TimelineTime.ToSeconds(proposal.SourceInUs):0.###}s" +
                $"..{TimelineTime.ToSeconds(proposal.SourceOutUs):0.###}s.";
        }
        _timelineDrag = gesture;
        RenderTimeline();
    }

    private void TimelineSurface_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        FinishTimelineDrag(e, commit: true);

    private void TimelineSurface_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        FinishTimelineDrag(e, commit: false);

    private void TimelineSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        FinishTimelineDrag(e, commit: false);

    private void FinishTimelineDrag(PointerRoutedEventArgs e, bool commit)
    {
        var gesture = _timelineDrag;
        if (gesture is null || gesture.PointerId != e.Pointer.PointerId)
            return;
        if (!_editor.HasOpenProject
            || !string.Equals(_editor.Project.Id, gesture.ProjectId, StringComparison.Ordinal)
            || _editor.Revision != gesture.Revision)
            commit = false;
        _timelineDrag = null;
        _timelineDragViewportX = null;
        _timelineAutoScrollTimer.Stop();
        TimelineSurface.ReleasePointerCapture(e.Pointer);

        if (!commit || !gesture.Dragged)
        {
            RenderTimeline();
            TimelineDragStatusText.Text = commit
                ? "Đã chọn clip; không có thay đổi để ghi."
                : "Đã hủy thao tác kéo; project không thay đổi.";
            e.Handled = true;
            return;
        }

        var changed = gesture.Kind == TimelineDragKind.Reorder
            ? gesture.CandidatePosition != gesture.OriginalPosition
            : gesture.CandidateTrim != gesture.OriginalTrim;
        if (!changed)
        {
            RenderTimeline();
            TimelineDragStatusText.Text = "Không có thay đổi để ghi.";
            e.Handled = true;
            return;
        }

        var applied = RunEdit(
            gesture.Kind == TimelineDragKind.Reorder ? "Đã kéo clip sang vị trí mới." : "Đã trim clip bằng kéo mép.",
            () =>
            {
                _editor.SelectClip(gesture.ClipId);
                if (gesture.Kind == TimelineDragKind.Reorder)
                    _editor.MoveSelected(gesture.CandidatePosition);
                else
                    _editor.TrimSelected(
                        gesture.CandidateTrim.SourceInUs,
                        gesture.CandidateTrim.SourceOutUs);
            });
        TimelineDragStatusText.Text = applied
            ? "Đã ghi một bước undo; bấm Lưu để persist project."
            : "Không thể ghi thao tác kéo; project giữ nguyên.";
        e.Handled = true;
    }

    private (TimelineClip? Clip, TimelineDragKind Kind) HitTestTimeline(Point point, double handleTolerance)
    {
        if (point.Y < TimelineTrackTop - TimelineHandleTolerance
            || point.Y > TimelineTrackTop + TimelineTrackHeight + TimelineHandleTolerance)
            return (null, TimelineDragKind.None);
        var selected = _editor.Clips.FirstOrDefault(clip =>
            string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal));
        if (selected is not null)
        {
            var left = SequenceX(selected.TimelineStartUs);
            var right = SequenceX(selected.TimelineEndUs);
            if (Math.Abs(point.X - left) <= handleTolerance)
                return (selected, TimelineDragKind.TrimIn);
            if (Math.Abs(point.X - right) <= handleTolerance)
                return (selected, TimelineDragKind.TrimOut);
        }
        var clip = _editor.Clips.LastOrDefault(item =>
            point.X >= SequenceX(item.TimelineStartUs)
            && point.X <= SequenceX(item.TimelineEndUs));
        return clip is null
            ? (null, TimelineDragKind.None)
            : (clip, TimelineDragKind.Reorder);
    }

    private TimelineClip? HitTestTimelineTouchGrip(Point point)
    {
        if (point.Y < TimelineTrackTop + TimelineTrackHeight - 24
            || point.Y > TimelineTrackTop + TimelineTrackHeight)
            return null;
        return _editor.Clips.LastOrDefault(clip =>
        {
            var left = SequenceX(clip.TimelineStartUs);
            var right = SequenceX(clip.TimelineEndUs);
            return right - left >= 52 && point.X >= right - 44 && point.X <= right;
        });
    }

    private void RenderTimeline()
    {
        TimelineSurface.Children.Clear();
        if (!_editor.HasOpenProject || _editor.Clips.Count == 0)
        {
            TimelineSurface.Width = 700;
            var empty = new TextBlock
            {
                Text = "Timeline chưa có clip.",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            };
            Canvas.SetLeft(empty, 18);
            Canvas.SetTop(empty, 40);
            TimelineSurface.Children.Add(empty);
            return;
        }

        var clips = _editor.Clips;
        var trackEndUs = _editor.Project.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.TimelineEndUs)
            .DefaultIfEmpty(0).Max();
        var textEndUs = _editor.Project.TextItems.Select(text => text.TimelineEndUs)
            .DefaultIfEmpty(0).Max();
        var sequenceEnd = Math.Max(Math.Max(clips[^1].TimelineEndUs, trackEndUs), textEndUs);
        TimelineSurface.Width = TimelineViewport.SurfaceWidth(
            sequenceEnd, _timelinePixelsPerSecond, 700);
        var viewportWidth = TimelineScroller.ViewportWidth > 0
            ? TimelineScroller.ViewportWidth
            : 700;
        var visible = TimelineViewport.VisibleClipRange(
            clips,
            TimelineScroller.HorizontalOffset,
            viewportWidth,
            _timelinePixelsPerSecond);
        RenderTrackLabel("Video", TimelineTrackTop, TimelineTrackHeight);
        for (var index = visible.StartIndex; index < visible.EndExclusive; index++)
        {
            var clip = clips[index];
            var selected = string.Equals(clip.Id, _editor.SelectedClipId, StringComparison.Ordinal);
            var left = SequenceX(clip.TimelineStartUs);
            var width = Math.Max(2, SequenceX(clip.TimelineEndUs) - left);
            var card = new Border
            {
                Width = Math.Max(1, width - 2),
                Height = TimelineTrackHeight,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = new SolidColorBrush(selected ? Microsoft.UI.Colors.DeepSkyBlue : Microsoft.UI.Colors.SlateGray),
                Background = new SolidColorBrush(selected
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 31, 91, 153)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 40, 52, 68)),
                Child = new TextBlock
                {
                    Margin = new Thickness(10, 8, 10, 6),
                    Text = $"{index + 1}. {clip.Id[..6]}\n" +
                        $"{TimelineTime.ToSeconds(clip.DurationUs):0.###}s · x{clip.PlaybackRate:0.###}",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            AutomationProperties.SetName(card, $"Clip {index + 1}, {TimelineTime.ToSeconds(clip.DurationUs):0.###} giây");
            Canvas.SetLeft(card, left + 1);
            Canvas.SetTop(card, TimelineTrackTop);
            TimelineSurface.Children.Add(card);

            if (width >= 52)
            {
                var grip = new Border
                {
                    Width = 42,
                    Height = 20,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(55, 255, 215, 0)),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "KÉO",
                        FontSize = 9,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Canvas.SetLeft(grip, left + width - 44);
                Canvas.SetTop(grip, TimelineTrackTop + TimelineTrackHeight - 22);
                TimelineSurface.Children.Add(grip);
            }

            var time = new TextBlock
            {
                Text = TimelineTime.ToSeconds(clip.TimelineStartUs).ToString("0.###") + "s",
                FontSize = 10,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGray),
            };
            Canvas.SetLeft(time, left + 2);
            Canvas.SetTop(time, 4);
            TimelineSurface.Children.Add(time);

            if (selected)
            {
                AddTimelineHandle(left);
                AddTimelineHandle(left + width);
            }
        }

        RenderSecondaryTracks();
        RenderTimelineDragProposal();
    }

    // G31: schema-5/6 multi-track rendering. Secondary tracks (overlay,
    // music, audio, text) render read-only below the primary row; editing
    // them arrives with the next G31 step. Hidden and muted tracks dim out.
    private void RenderSecondaryTracks()
    {
        var top = TimelineSecondaryTop;
        foreach (var track in _editor.Project.Tracks
            .OrderBy(track => track.ZIndex)
            .ThenBy(track => track.Id, StringComparer.Ordinal))
        {
            RenderTrackLabel(
                track.Name + (track.IsMuted ? " (tắt tiếng)" : string.Empty),
                top, TimelineSecondaryTrackHeight, dimmed: !track.IsVisible);
            var items = track.Kind is TimelineTrackKind.Text
                ? _editor.Project.TextItems
                    .Where(text => string.Equals(text.TrackId, track.Id, StringComparison.Ordinal))
                    .Select(text => (ElementId: text.Id, StartUs: text.TimelineStartUs,
                        EndUs: text.TimelineEndUs, Label: text.Content, IsText: true))
                    .ToList()
                : track.Clips
                    .Select(clip => (ElementId: clip.Id, StartUs: clip.TimelineStartUs,
                        EndUs: clip.TimelineEndUs,
                        Label: $"{clip.Id[..6]} x{clip.PlaybackRate:0.###}", IsText: false))
                    .ToList();
            foreach (var element in items
                .Where(element => element.EndUs > TimelineScroller.HorizontalOffset
                    && element.StartUs < TimelineScroller.HorizontalOffset
                        + Math.Max(TimelineScroller.ViewportWidth, 0) + TimelineViewport.OverscanPixels)
                .OrderBy(element => element.StartUs)
                .ThenBy(element => element.ElementId, StringComparer.Ordinal))
            {
                var left = SequenceX(element.StartUs);
                var width = Math.Max(2, SequenceX(element.EndUs) - left);
                var card = new Border
                {
                    Width = Math.Max(1, width - 2),
                    Height = TimelineSecondaryTrackHeight,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(element.IsText
                        ? Microsoft.UI.Colors.MediumOrchid
                        : Microsoft.UI.Colors.DarkSeaGreen),
                    Background = new SolidColorBrush(element.IsText
                        ? Microsoft.UI.ColorHelper.FromArgb(255, 74, 44, 94)
                        : Microsoft.UI.ColorHelper.FromArgb(255, 44, 66, 54)),
                    Opacity = track.IsVisible ? 1 : 0.45,
                    Child = new TextBlock
                    {
                        Margin = new Thickness(6, 2, 6, 2),
                        Text = element.Label,
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                AutomationProperties.SetName(card, $"{track.Name}: {element.Label}");
                Canvas.SetLeft(card, left + 1);
                Canvas.SetTop(card, top);
                TimelineSurface.Children.Add(card);
            }
            top += TimelineSecondaryTrackHeight + 6;
        }
        TimelineSurface.Height = Math.Max(280, top + 8);
    }

    private void RenderTrackLabel(string name, double top, double height, bool dimmed = false)
    {
        var label = new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = new SolidColorBrush(dimmed
                ? Microsoft.UI.Colors.Gray
                : Microsoft.UI.Colors.LightGray),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = TimelineTrackLabelWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, top + (height - 14) / 2);
        TimelineSurface.Children.Add(label);
    }

    private void RenderTimelineDragProposal()
    {
        var gesture = _timelineDrag;
        if (gesture is null || !gesture.Dragged)
            return;
        var clip = _editor.Clips.First(item =>
            string.Equals(item.Id, gesture.ClipId, StringComparison.Ordinal));
        if (gesture.Kind == TimelineDragKind.Reorder)
        {
            var target = _editor.Clips[gesture.CandidatePosition];
            var x = gesture.CandidatePosition <= gesture.OriginalPosition
                ? SequenceX(target.TimelineStartUs)
                : SequenceX(target.TimelineEndUs);
            var marker = new Rectangle
            {
                Width = 4,
                Height = TimelineTrackHeight + 16,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Gold),
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(marker, x - 2);
            Canvas.SetTop(marker, TimelineTrackTop - 8);
            TimelineSurface.Children.Add(marker);
            return;
        }

        var sourceSpanUs = clip.SourceOutUs - clip.SourceInUs;
        var startRatio = (gesture.CandidateTrim.SourceInUs - clip.SourceInUs) / (double)sourceSpanUs;
        var endRatio = (gesture.CandidateTrim.SourceOutUs - clip.SourceInUs) / (double)sourceSpanUs;
        var left = SequenceX(clip.TimelineStartUs);
        var width = SequenceX(clip.TimelineEndUs) - left;
        var proposal = new Rectangle
        {
            Width = Math.Max(2, width * (endRatio - startRatio)),
            Height = TimelineTrackHeight - 12,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gold),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(60, 255, 215, 0)),
        };
        Canvas.SetLeft(proposal, left + width * startRatio);
        Canvas.SetTop(proposal, TimelineTrackTop + 6);
        TimelineSurface.Children.Add(proposal);
    }

    private void AddTimelineHandle(double x)
    {
        var handle = new Rectangle
        {
            Width = 8,
            Height = TimelineTrackHeight + 8,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue),
        };
        Canvas.SetLeft(handle, x - 4);
        Canvas.SetTop(handle, TimelineTrackTop - 4);
        TimelineSurface.Children.Add(handle);
    }

    private long TimelineSequenceEndUs()
    {
        if (!_editor.HasOpenProject)
            return 0;
        var clips = _editor.Clips;
        return Math.Max(
            clips.Count == 0 ? 0 : clips[^1].TimelineEndUs,
            Math.Max(
                _editor.Project.Tracks.SelectMany(track => track.Clips)
                    .Select(clip => clip.TimelineEndUs)
                    .DefaultIfEmpty(0).Max(),
                _editor.Project.TextItems.Select(text => text.TimelineEndUs)
                    .DefaultIfEmpty(0).Max()));
    }

    private long PointerSequenceUs(double x)
    {
        var total = _editor.Clips[^1].TimelineEndUs;
        return TimelineViewport.SequenceUs(x, _timelinePixelsPerSecond, total);
    }

    private double SequenceX(long sequenceUs) =>
        TimelineViewport.SequenceX(sequenceUs, _timelinePixelsPerSecond);

    private void TimelineZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var maximumUs = TimelineSequenceEndUs();
        _pendingTimelineZoomCenterUs ??= TimelineViewport.SequenceUs(
            TimelineScroller.HorizontalOffset + TimelineScroller.ViewportWidth / 2,
            _timelinePixelsPerSecond,
            maximumUs);
        _timelinePixelsPerSecond = TimelineViewport.ClampPixelsPerSecond(e.NewValue);
        TimelineZoomText.Text = $"{_timelinePixelsPerSecond:0} px/s";
        QueueTimelineViewportRender();
    }

    private void TimelineScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        QueueTimelineViewportRender();

    private void TimelineSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyPendingTimelineZoomCenter();
    }

    private void ApplyPendingTimelineZoomCenter()
    {
        if (_pendingTimelineZoomCenterUs is not { } centerUs)
            return;
        _pendingTimelineZoomCenterUs = null;
        var targetOffset = Math.Max(
            0,
            TimelineViewport.SequenceX(centerUs, _timelinePixelsPerSecond)
                - TimelineScroller.ViewportWidth / 2);
        TimelineScroller.ChangeView(targetOffset, null, null, disableAnimation: true);
    }

    private void QueueTimelineViewportRender()
    {
        if (_timelineViewportRenderQueued)
            return;
        _timelineViewportRenderQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _timelineViewportRenderQueued = false;
                var previousWidth = TimelineSurface.Width;
                RenderTimeline();
                if (_pendingTimelineZoomCenterUs is not null
                    && Math.Abs(previousWidth - TimelineSurface.Width) < 0.5)
                    ApplyPendingTimelineZoomCenter();
            }))
            _timelineViewportRenderQueued = false;
    }

    private void AutoScrollTimeline(double pointerViewportX)
    {
        const double edge = 44;
        const double step = 36;
        var left = TimelineScroller.HorizontalOffset;
        double? target = null;
        if (pointerViewportX < edge)
            target = Math.Max(0, left - step);
        else if (pointerViewportX > TimelineScroller.ViewportWidth - edge)
            target = Math.Min(TimelineScroller.ScrollableWidth, left + step);
        if (target is not null)
            TimelineScroller.ChangeView(target, null, null, disableAnimation: true);
    }

    private void ReportEditState(string message)
    {
        RefreshClips();
        EditStatusText.Text = $"{message} " +
            $"({_editor.Clips.Count} clip, undo {_editor.UndoCount}, redo {_editor.RedoCount}" +
            $"{(_editor.IsDirty ? ", chưa lưu" : ", đã lưu")}).";
        if (_editor.IsDirty)
            QueueTimelineAutosave();
        else if (!_timelineAutosaveBusy)
        {
            _timelineAutosavePending = false;
            UpdateTimelineAutosaveText();
        }
        UpdateRelinkMediaButton();
    }

    // G19 relink (kiểu Adobe): nguồn video mất khỏi đường dẫn cũ thì báo và
    // cho user chỉ lại file. Project/asset/clip ID giữ nguyên; chỉ path +
    // fingerprint của asset được đổi, và mọi clip vẫn trỏ cùng asset ID.
    private async void RelinkMedia_Click(object sender, RoutedEventArgs e)
    {
        if (!_editor.HasOpenProject)
            return;
        try
        {
            if (_savingProject || _openingProject || _timelineClosing || TimelinePreviewLocked)
            {
                EditStatusText.Text = "Hãy đợi thao tác đang chạy hoàn tất trước khi nối lại nguồn.";
                return;
            }
            var missing = _editor.FindMissingMediaAssets();
            if (missing.Count == 0)
            {
                EditStatusText.Text = "Tất cả nguồn video đều tìm thấy; không cần nối lại.";
                return;
            }
            var target = missing[0];
            EditStatusText.Text = $"Không tìm thấy nguồn ở: {target.Path}. Hãy chọn lại file đó ở vị trí mới.";
            var pickedPath = _filePicker?.PickVideoAsync();
            string? candidate = pickedPath is null ? null : await pickedPath;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                EditStatusText.Text = "Đã hủy nối lại nguồn; project giữ nguyên.";
                return;
            }
            var normalized = EditorSourceSelection.NormalizeCandidatePath(candidate);
            MediaPreviewInfo media;
            try
            {
                media = await _application.Media.ProbeAsync(normalized, CancellationToken.None);
            }
            catch (Exception probeError)
            {
                EditStatusText.Text = "File được chọn không đọc được: " + probeError.Message;
                return;
            }
            if (_savingProject || _openingProject || _timelineClosing)
            {
                EditStatusText.Text = "Project đang bận; hãy thử nối lại nguồn sau.";
                return;
            }
            var size = new FileInfo(normalized).Length;
            var lastWrite = new FileInfo(normalized).LastWriteTimeUtc.Ticks;
            await _editor.RelinkMediaAssetAsync(
                target.Id,
                normalized,
                media.Width,
                media.Height,
                media.Duration,
                await _application.Media.ProbeHasAudioAsync(normalized, CancellationToken.None),
                size,
                lastWrite,
                CancellationToken.None);
            await RefreshAsync();
            RestoreSelectedProject();
            ReportEditState("Đã nối lại nguồn video: " + normalized);
        }
        catch (Exception error)
        {
            EditStatusText.Text = "Không nối được nguồn: " + error.Message;
        }
    }

    private void UpdateRelinkMediaButton()
    {
        if (_filePicker is null || !_editor.HasOpenProject || _savingProject || _openingProject)
        {
            RelinkMediaButton.IsEnabled = false;
            return;
        }
        try
        {
            RelinkMediaButton.IsEnabled = !TimelinePreviewLocked
                && _editor.FindMissingMediaAssets().Count > 0;
        }
        catch
        {
            RelinkMediaButton.IsEnabled = false;
        }
    }

    private static double ParseSeconds(string? text, string field)
    {
        if (!double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds < 0)
            throw new InvalidDataException($"Giá trị giây không hợp lệ: {field}.");
        return seconds;
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
            if (_timelineClosing)
                return;
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

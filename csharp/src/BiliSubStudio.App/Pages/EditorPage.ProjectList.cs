using System.Collections.ObjectModel;
using BiliSubStudio.App.ViewModels;
using BiliSubStudio.Core.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private readonly ObservableCollection<EditorProjectListItem> _editorProjects = [];
    private CancellationTokenSource? _projectListCancellation;
    private bool _projectOpenInProgress;

    private void InitializeProjectList()
    {
        ProjectGridView.ItemsSource = _editorProjects;
        ShowProjectListSurface();
    }

    private async Task ShowProjectListAsync(bool saveCurrent)
    {
        if (EditorBusy)
        {
            ShowWorkspaceSurface();
            StatusText.Text = "Hãy hoàn tất hoặc hủy tác vụ đang chạy trước khi quay lại danh sách project.";
            return;
        }

        if (_playback.IsPreviewMode) await _playback.SetModeAsync(enabled: false, play: false);
        if (saveCurrent && _project is not null)
        {
            StopProjectSaveTimer();
            _pendingProjectSave = null;
            await WaitForProjectSaveIdleAsync();
            await SaveCurrentSourceStateForSwitchAsync();
        }

        ShowProjectListSurface();
        await RefreshProjectListAsync();
    }

    private void ShowProjectListSurface()
    {
        ProjectListPanel.Visibility = Visibility.Visible;
        WorkspaceGrid.Visibility = Visibility.Collapsed;
        BackToProjectsButton.Visibility = Visibility.Collapsed;
        NewProjectButton.Visibility = Visibility.Visible;
        EditorHeaderBadges.Visibility = Visibility.Collapsed;
        EditorBreadcrumbText.Text = "BiliSub Studio / Chỉnh video";
        EditorTitleText.Text = "Chỉnh video";
    }

    private void ShowWorkspaceSurface()
    {
        CancelProjectThumbnailLoading();
        ProjectListPanel.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Visibility = Visibility.Visible;
        BackToProjectsButton.Visibility = Visibility.Visible;
        NewProjectButton.Visibility = Visibility.Collapsed;
        EditorHeaderBadges.Visibility = Visibility.Visible;
        var projectName = string.IsNullOrWhiteSpace(_project?.Name) ? "Project" : _project.Name;
        EditorBreadcrumbText.Text = $"BiliSub Studio / Chỉnh video / {projectName}";
        EditorTitleText.Text = projectName;
        CurrentProjectNameText.Text = projectName;
    }

    private async Task RefreshProjectListAsync()
    {
        CancelProjectThumbnailLoading();
        var cancellation = new CancellationTokenSource();
        _projectListCancellation = cancellation;
        RefreshProjectsButton.IsEnabled = false;
        try
        {
            var summaries = await _application.ListEditorProjectsAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _editorProjects.Clear();
            foreach (var summary in summaries) _editorProjects.Add(new EditorProjectListItem(summary));
            UpdateProjectListState();
            _ = LoadProjectThumbnailsAsync(_editorProjects.ToArray(), cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ProjectListSummaryText.Text = "Không đọc được danh sách project: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_projectListCancellation, cancellation)) RefreshProjectsButton.IsEnabled = true;
        }
    }

    private async Task LoadProjectThumbnailsAsync(IReadOnlyList<EditorProjectListItem> projects, CancellationToken cancellationToken)
    {
        foreach (var item in projects)
        {
            if (!item.Summary.SourceExists) continue;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachePath = ProjectThumbnailPath(item);
                byte[] bytes;
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                }
                else
                {
                    if (!_application.Tools.Status.FfmpegReady) continue;
                    bytes = await _application.Media.GetFrameJpegAsync(item.SourcePath, 0, cancellationToken);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                item.Thumbnail = await DecodeProjectThumbnailAsync(bytes);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // A missing/corrupt thumbnail falls back to the card's video icon.
            }
        }
    }

    private string ProjectThumbnailPath(EditorProjectListItem item) => Path.Combine(
        _application.Paths.Cache,
        "Editor",
        "ProjectThumbnails",
        $"{item.Id}.{item.Summary.SourceSize}.{item.Summary.SourceLastWriteUtcTicks}.jpg");

    private static async Task<BitmapImage> DecodeProjectThumbnailAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void CancelProjectThumbnailLoading()
    {
        _projectListCancellation?.Cancel();
        _projectListCancellation?.Dispose();
        _projectListCancellation = null;
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        try { await OpenVideoAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Không tạo được project: " + error.Message; }
    }

    private async void BackToProjects_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowProjectListAsync(saveCurrent: true); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Không thể quay lại danh sách project: " + error.Message; }
    }

    private async void RefreshProjects_Click(object sender, RoutedEventArgs e) => await RefreshProjectListAsync();

    private async void ProjectGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is EditorProjectListItem item) await OpenProjectItemAsync(item);
        }
        catch (Exception error) { StatusText.Text = "Không mở được project: " + error.Message; }
    }

    private async void OpenProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: EditorProjectListItem item }) await OpenProjectItemAsync(item);
        }
        catch (Exception error) { StatusText.Text = "Không mở được project: " + error.Message; }
    }

    private async Task OpenProjectItemAsync(EditorProjectListItem item)
    {
        if (_projectOpenInProgress) return;
        _projectOpenInProgress = true;
        try
        {
        if (!File.Exists(item.SourcePath))
        {
            var result = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Không tìm thấy video nguồn",
                Content = $"Project “{item.Name}” vẫn được giữ nguyên, nhưng video đã bị di chuyển hoặc xóa:\n\n{item.SourcePath}",
                PrimaryButtonText = "Nối lại video",
                CloseButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
            if (result == ContentDialogResult.Primary) await RelinkProjectItemAsync(item);
            return;
        }

        try { await OpenVideoPathAsync(item.SourcePath); }
        catch (OperationCanceledException) { }
        catch (Exception error) { StatusText.Text = "Không mở được project: " + error.Message; }
        }
        finally
        {
            _projectOpenInProgress = false;
        }
    }

    private async Task RelinkProjectItemAsync(EditorProjectListItem item)
    {
        var picked = await _picker.PickVideoAsync();
        if (string.IsNullOrWhiteSpace(picked)) return;
        try
        {
            var candidate = EditorSourceSelection.NormalizeCandidatePath(picked);
            var media = await _application.Media.ProbeAsync(candidate, CancellationToken.None);
            var project = await _application.LoadEditorProjectByIdAsync(item.Id, CancellationToken.None);
            var relinked = await _application.RelinkEditorProjectSourceAsync(
                project, candidate, media, CancellationToken.None);
            await RefreshProjectListAsync();
            await OpenVideoPathAsync(relinked.Source.Path);
            StatusText.Text = "Đã nối lại video nguồn. Word timing và voice cũ đã được xóa để tránh dùng cache theo đường dẫn cũ.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            StatusText.Text = "Không nối lại được video nguồn: " + error.Message;
        }
    }

    private async void RenameProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EditorProjectListItem item }) return;
        var nameBox = new TextBox
        {
            Text = item.Name,
            MaxLength = 120,
            SelectionStart = 0,
            SelectionLength = item.Name.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Đổi tên project",
            Content = nameBox,
            PrimaryButtonText = "Lưu tên",
            CloseButtonText = "Hủy",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _application.RenameEditorProjectAsync(item.Id, nameBox.Text, CancellationToken.None);
            if (_project is { } activeProject && string.Equals(activeProject.Id, item.Id, StringComparison.Ordinal))
                _project = activeProject with { Name = nameBox.Text.Trim() };
            await RefreshProjectListAsync();
            StatusText.Text = "Đã đổi tên project.";
        }
        catch (Exception error) { StatusText.Text = "Không đổi được tên project: " + error.Message; }
    }

    private async void DeleteProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EditorProjectListItem item }) return;
        var dialog = CreateDeleteProjectDialog(item);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        CancelProjectThumbnailLoading();
        _editorProjects.Remove(item);
        UpdateProjectListState();
        StatusText.Text = $"Đã gỡ “{item.Name}” khỏi danh sách; đang dọn cache nội bộ ở nền...";
        try
        {
            if (string.Equals(_project?.Id, item.Id, StringComparison.Ordinal)) await ClearDeletedActiveProjectAsync();
            await _application.DeleteEditorProjectAsync(item.Id, CancellationToken.None);
            StatusText.Text = $"Đã xóa project “{item.Name}”. File nguồn không bị thay đổi.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Không xóa được project: " + error.Message;
            await RefreshProjectListAsync();
        }
    }

    private void UpdateProjectListState()
    {
        ProjectEmptyState.Visibility = _editorProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectGridView.Visibility = _editorProjects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProjectListSummaryText.Text = _editorProjects.Count == 0
            ? "Tạo project đầu tiên từ một video nguồn."
            : $"{_editorProjects.Count} project · sắp xếp theo lần sửa gần nhất";
    }

    private ContentDialog CreateDeleteProjectDialog(EditorProjectListItem item)
    {
        var dangerBrush = (Brush)Application.Current.Resources["DangerBrush"];
        var dangerSoftBrush = (Brush)Application.Current.Resources["DangerSoftBrush"];
        var primaryTextBrush = (Brush)Application.Current.Resources["PrimaryTextBrush"];
        var secondaryTextBrush = (Brush)Application.Current.Resources["SecondaryTextBrush"];
        var mutedTextBrush = (Brush)Application.Current.Resources["MutedTextBrush"];
        var borderBrush = (Brush)Application.Current.Resources["BorderBrush"];
        var raisedSurfaceBrush = (Brush)Application.Current.Resources["RaisedSurfaceBrush"];

        var projectName = new TextBlock
        {
            Text = item.Name,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primaryTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var sourceName = new TextBlock
        {
            Text = item.FileName,
            FontSize = 11,
            Foreground = mutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var identity = new StackPanel { Spacing = 3 };
        identity.Children.Add(projectName);
        identity.Children.Add(sourceName);

        var icon = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = dangerSoftBrush,
            Child = new FontIcon
            {
                Glyph = "\uE74D",
                FontSize = 20,
                Foreground = dangerBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var identityGrid = new Grid { ColumnSpacing = 12 };
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(identity, 1);
        identityGrid.Children.Add(icon);
        identityGrid.Children.Add(identity);

        var sourceSafety = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            Background = raisedSurfaceBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Video, SRT, ảnh/logo nguồn và file đã xuất vẫn được giữ nguyên trên ổ đĩa.",
                FontSize = 11,
                Foreground = secondaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        var content = new StackPanel { Width = 430, Spacing = 14 };
        content.Children.Add(identityGrid);
        content.Children.Add(new TextBlock
        {
            Text = "Project và dữ liệu làm việc nội bộ sẽ bị xóa khỏi BiliSub Studio. Hành động này không thể hoàn tác.",
            FontSize = 13,
            Foreground = secondaryTextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        content.Children.Add(sourceSafety);

        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Xóa project?",
            Content = content,
            PrimaryButtonText = "Xóa project",
            CloseButtonText = "Giữ project",
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)Application.Current.Resources["DialogDangerButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["DialogSecondaryButtonStyle"],
        };
    }

    private async Task ClearDeletedActiveProjectAsync()
    {
        StopProjectSaveTimer();
        _pendingProjectSave = null;
        await WaitForProjectSaveIdleAsync();
        await DisposePreviewForSourceChangeAsync();
        _project = null;
        _path = null;
        _media = null;
        _subtitleSource = null;
        _subtitlePlacement = BiliSubStudio.Core.Editor.EditorSubtitlePlacement.Default;
        _document.Reset([]);
        _imageProjectId = null;
        _imageOverlays.Clear();
        _imageBitmaps.Clear();
        _selectedImageIndex = -1;
        ClearVoiceTrackState();
        _cueSpeechTiming = [];
    }

    private async void OpenProjectFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EditorProjectListItem item }) return;
        try
        {
            var directory = Path.GetDirectoryName(item.SourcePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("Thư mục chứa video nguồn không còn tồn tại.");
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception error) { StatusText.Text = "Không mở được thư mục: " + error.Message; }
    }
}

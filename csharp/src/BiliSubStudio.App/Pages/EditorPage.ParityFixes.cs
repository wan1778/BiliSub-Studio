using BiliSubStudio.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private bool _editorParityInitialized;
    private TextBlock? _editorOutputPathText;
    private Button? _editorUseCurrentStartButton;
    private Button? _editorUseCurrentEndButton;
    private Button? _editorChooseOutputButton;
    private Button? _editorOpenOutputButton;
    private ToggleSwitch? _editorAutoCompositeToggle;
    private CancellationTokenSource? _editorAutoCompositeCancellation;
    private bool _editorAutoCompositeRebuilding;

    private void EnsureEditorParityInitialized()
    {
        if (_editorParityInitialized) return;
        _editorParityInitialized = true;
        StrengthBox.Maximum = 40;
        if (StrengthBox.Value > 40) StrengthBox.Value = 40;
        BuildEditorTimestampControls();
        BuildEditorOutputControls();
        BuildEditorLivePreviewControls();
        RefreshEditorParityControls();
    }

    private void BuildEditorTimestampControls()
    {
        if (StartBox.Parent is not Grid timeGrid || timeGrid.Parent is not StackPanel host) return;
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        _editorUseCurrentStartButton = new Button
        {
            Content = "Lấy vị trí hiện tại → Bắt đầu",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _editorUseCurrentStartButton.Click += EditorUseCurrentStart_Click;
        _editorUseCurrentEndButton = new Button
        {
            Content = "Lấy vị trí hiện tại → Kết thúc",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _editorUseCurrentEndButton.Click += EditorUseCurrentEnd_Click;
        Grid.SetColumn(_editorUseCurrentEndButton, 1);
        row.Children.Add(_editorUseCurrentStartButton);
        row.Children.Add(_editorUseCurrentEndButton);
        var index = host.Children.IndexOf(timeGrid);
        host.Children.Insert(index < 0 ? host.Children.Count : index + 1, row);
    }

    private void BuildEditorOutputControls()
    {
        if (FileNameBox.Parent is not StackPanel host) return;
        var label = new TextBlock
        {
            Text = "Nơi lưu",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        _editorOutputPathText = new TextBlock
        {
            Text = _application.Config.OutputDirectory,
            TextWrapping = TextWrapping.Wrap,
            Opacity = .72,
        };
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        _editorChooseOutputButton = new Button
        {
            Content = "Chọn thư mục",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _editorChooseOutputButton.Click += EditorChooseOutput_Click;
        _editorOpenOutputButton = new Button
        {
            Content = "Mở thư mục",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _editorOpenOutputButton.Click += EditorOpenOutput_Click;
        Grid.SetColumn(_editorOpenOutputButton, 1);
        row.Children.Add(_editorChooseOutputButton);
        row.Children.Add(_editorOpenOutputButton);
        var index = host.Children.IndexOf(FileNameBox);
        if (index < 0) index = host.Children.Count;
        host.Children.Insert(index, label);
        host.Children.Insert(index + 1, _editorOutputPathText);
        host.Children.Insert(index + 2, row);
    }

    private void BuildEditorLivePreviewControls()
    {
        if (PlaybackButton.Parent is not Grid playbackGrid || playbackGrid.Parent is not StackPanel host) return;
        var panel = new StackPanel { Spacing = 3 };
        _editorAutoCompositeToggle = new ToggleSwitch
        {
            Header = "Tự cập nhật bản xem trước khi đang phát",
            IsOn = true,
        };
        _editorAutoCompositeToggle.Toggled += EditorAutoComposite_Toggled;
        panel.Children.Add(_editorAutoCompositeToggle);
        panel.Children.Add(new TextBlock
        {
            Text = "Thay đổi hiệu ứng, âm thanh hoặc ảnh/logo sẽ dùng cùng pipeline preview; không cần xuất video để kiểm tra.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .72,
            FontSize = 11,
        });
        var index = host.Children.IndexOf(playbackGrid);
        host.Children.Insert(index < 0 ? host.Children.Count : index + 1, panel);
    }

    private void EditorUseCurrentStart_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || WholeToggle.IsOn || EditorBusy) return;
        _syncingInputs = true;
        try { StartBox.Value = Math.Clamp(Timeline.Value, 0, _media.Duration); }
        finally { _syncingInputs = false; }
        ApplyInputsToDocument();
        NotifyEditorCompositeChanged();
    }

    private void EditorUseCurrentEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || WholeToggle.IsOn || EditorBusy) return;
        _syncingInputs = true;
        try { EndBox.Value = Math.Clamp(Timeline.Value, 0, _media.Duration); }
        finally { _syncingInputs = false; }
        ApplyInputsToDocument();
        NotifyEditorCompositeChanged();
    }

    private async void EditorChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as BiliSubStudio.App.App
                ?? throw new InvalidOperationException("Không lấy được cửa sổ chính.");
            var window = app.MainWindow ?? throw new InvalidOperationException("Cửa sổ chính chưa sẵn sàng.");
            var picker = new FolderPickerService(() => window);
            var path = await picker.PickFolderAsync(_application.Config.OutputDirectory);
            if (string.IsNullOrWhiteSpace(path)) return;
            await _application.Settings.SetOutputDirectoryAsync(path, CancellationToken.None);
            if (_editorOutputPathText is not null) _editorOutputPathText.Text = _application.Config.OutputDirectory;
            StatusText.Text = "Đã đổi nơi lưu Editor: " + _application.Config.OutputDirectory;
            RefreshEditorActions();
        }
        catch (Exception error)
        {
            StatusText.Text = "Không đổi được nơi lưu: " + error.Message;
        }
    }

    private async void EditorOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as BiliSubStudio.App.App
                ?? throw new InvalidOperationException("Không lấy được cửa sổ chính.");
            var window = app.MainWindow ?? throw new InvalidOperationException("Cửa sổ chính chưa sẵn sàng.");
            var picker = new FolderPickerService(() => window);
            await picker.OpenFolderAsync(_application.Config.OutputDirectory);
        }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được nơi lưu: " + error.Message;
        }
    }

    private void NotifyEditorCompositeChanged()
    {
        RefreshEditorParityControls();
        QueueEditorCompositeRefresh();
    }

    private void EditorAutoComposite_Toggled(object sender, RoutedEventArgs e)
    {
        if (_editorAutoCompositeToggle?.IsOn != true)
        {
            _editorAutoCompositeCancellation?.Cancel();
            _editorAutoCompositeCancellation?.Dispose();
            _editorAutoCompositeCancellation = null;
        }
        RefreshEditorParityControls();
    }

    private void QueueEditorCompositeRefresh()
    {
        if (_editorAutoCompositeToggle?.IsOn != true || !_playerMode || _previewRendering || _editorAutoCompositeRebuilding) return;
        _editorAutoCompositeCancellation?.Cancel();
        _editorAutoCompositeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _editorAutoCompositeCancellation = cancellation;
        _ = RebuildEditorCompositePreviewAsync(cancellation);
    }

    private async Task RebuildEditorCompositePreviewAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(320, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_playerMode || _media is null) return;
            var resumePlayback = _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            var sourcePosition = Math.Clamp(Timeline.Value, Timeline.Minimum, Timeline.Maximum);
            _editorAutoCompositeRebuilding = true;
            StatusText.Text = "Đang cập nhật bản xem trước theo thay đổi mới...";
            await SetPlaybackModeAsync(enabled: false, play: false);
            cancellation.Token.ThrowIfCancellationRequested();
            _syncingTimeline = true;
            try { Timeline.Value = sourcePosition; }
            finally { _syncingTimeline = false; }
            await SetPlaybackModeAsync(enabled: true, play: resumePlayback);
            cancellation.Token.ThrowIfCancellationRequested();
            StatusText.Text = "Bản xem trước đã cập nhật.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            StatusText.Text = "Không tự cập nhật được preview: " + error.Message;
        }
        finally
        {
            _editorAutoCompositeRebuilding = false;
            if (ReferenceEquals(_editorAutoCompositeCancellation, cancellation)) _editorAutoCompositeCancellation = null;
            cancellation.Dispose();
            RefreshEditorParityControls();
        }
    }

    private void RefreshEditorParityControls()
    {
        if (!_editorParityInitialized) return;
        if (_editorOutputPathText is not null) _editorOutputPathText.Text = _application.Config.OutputDirectory;
        var timeEnabled = _media is not null && !WholeToggle.IsOn && !EditorBusy && !_playerMode;
        if (_editorUseCurrentStartButton is not null) _editorUseCurrentStartButton.IsEnabled = timeEnabled;
        if (_editorUseCurrentEndButton is not null) _editorUseCurrentEndButton.IsEnabled = timeEnabled;
        if (_editorChooseOutputButton is not null) _editorChooseOutputButton.IsEnabled = !EditorBusy && !_playerMode;
        if (_editorOpenOutputButton is not null) _editorOpenOutputButton.IsEnabled = Directory.Exists(_application.Config.OutputDirectory);
        if (_editorAutoCompositeToggle is not null) _editorAutoCompositeToggle.IsEnabled = !EditorBusy;
    }

    private void CleanupEditorParity()
    {
        _editorAutoCompositeCancellation?.Cancel();
        _editorAutoCompositeCancellation?.Dispose();
        _editorAutoCompositeCancellation = null;
        _editorAutoCompositeRebuilding = false;
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private sealed class EditorWorkspaceState
    {
        public int Version { get; set; } = 1;
        public double SourceWidth { get; set; }
        public double DetailsWidth { get; set; }
        public double TimelineHeight { get; set; }
        public double TimelineHeaderWidth { get; set; }
        public bool SourceCollapsed { get; set; }
        public bool TimelineCollapsed { get; set; }
        public double SourceRestoreWidth { get; set; }
        public double TimelineRestoreHeight { get; set; }
    }

    private readonly EditorWorkspaceState _workspaceLayout = new()
    {
        SourceWidth = 170,
        DetailsWidth = 340,
        TimelineHeight = 206,
        TimelineHeaderWidth = 78,
    };
    private bool _workspaceInitialized;
    private bool _sourceCollapsed;
    private bool _timelineCollapsed;
    private double _sourceRestoreWidth = 170;
    private double _timelineRestoreHeight = 206;
    private double _sourceColumnMinWidth = 120;
    private double _sourceColumnMaxWidth = 200;
    // NAV-01: the floor equals what the six-tool strip needs (6 x 56 + 30 padding).
    private double _detailsColumnMinWidth = 366;
    private double _detailsColumnMaxWidth = 380;
    private double _timelineRowMinHeight = 160;
    private double _timelineRowMaxHeight = 420;
    private double _timelineHeaderMinWidth = 56;
    private double _timelineHeaderMaxWidth = 150;

    private void InitializeWorkspaceLayout()
    {
        if (_workspaceInitialized) return;
        _workspaceInitialized = true;
        CaptureWorkspaceBounds();
        _workspaceLayout.SourceWidth = 170;
        _workspaceLayout.DetailsWidth = 340;
        _workspaceLayout.TimelineHeight = 206;
        _workspaceLayout.TimelineHeaderWidth = 78;
        _workspaceLayout.SourceCollapsed = false;
        _workspaceLayout.TimelineCollapsed = false;
        _workspaceLayout.SourceRestoreWidth = 170;
        _workspaceLayout.TimelineRestoreHeight = 206;
        LoadWorkspaceLayout();
        // A collapsed panel is SESSION-only. Persisting the collapse made the
        // timeline (and Source) come back hidden on every reopen, which no one
        // asked for; widths/heights stay persisted, the collapse does not.
        _workspaceLayout.SourceCollapsed = false;
        _workspaceLayout.TimelineCollapsed = false;
        ApplyWorkspaceLayout(_workspaceLayout);
    }

    private void CaptureWorkspaceBounds()
    {
        _sourceColumnMinWidth = SourceColumn.MinWidth;
        _sourceColumnMaxWidth = SourceColumn.MaxWidth is double.NaN ? 200 : SourceColumn.MaxWidth;
        _detailsColumnMinWidth = DetailsColumn.MinWidth;
        _detailsColumnMaxWidth = DetailsColumn.MaxWidth is double.NaN ? 380 : DetailsColumn.MaxWidth;
        _timelineRowMinHeight = TimelineRow.MinHeight;
        _timelineRowMaxHeight = TimelineRow.MaxHeight is double.NaN ? 420 : TimelineRow.MaxHeight;
        _timelineHeaderMinWidth = TimelineHeaderColumn.MinWidth;
        _timelineHeaderMaxWidth = TimelineHeaderColumn.MaxWidth is double.NaN ? 150 : TimelineHeaderColumn.MaxWidth;
        _sourceRestoreWidth = Math.Clamp(_sourceRestoreWidth, _sourceColumnMinWidth, _sourceColumnMaxWidth);
        _timelineRestoreHeight = Math.Clamp(_timelineRestoreHeight, _timelineRowMinHeight, _timelineRowMaxHeight);
    }

    private string WorkspaceLayoutPath => Path.Combine(_application.Paths.Data, "Editor", "workspace.json");

    private void LoadWorkspaceLayout()
    {
        try
        {
            var path = WorkspaceLayoutPath;
            if (!File.Exists(path)) return;
            var state = JsonSerializer.Deserialize<EditorWorkspaceState>(File.ReadAllText(path));
            if (state is null || state.Version != 1) return;
            _workspaceLayout.SourceWidth = state.SourceWidth;
            _workspaceLayout.DetailsWidth = state.DetailsWidth;
            _workspaceLayout.TimelineHeight = state.TimelineHeight;
            _workspaceLayout.TimelineHeaderWidth = state.TimelineHeaderWidth;
            _workspaceLayout.SourceCollapsed = state.SourceCollapsed;
            _workspaceLayout.TimelineCollapsed = state.TimelineCollapsed;
            _workspaceLayout.SourceRestoreWidth = state.SourceRestoreWidth;
            _workspaceLayout.TimelineRestoreHeight = state.TimelineRestoreHeight;
        }
        catch (Exception)
        {
            // Layout JSON is a convenience, not an ownership document: corrupt state
            // falls back to defaults instead of blocking the editor.
        }
    }

    private bool _workspaceSaveInFlight;
    private bool _workspaceSaveQueued;

    private void SaveWorkspaceLayoutAsync()
    {
        // Overlapping saves used to share one temp path and delete each other's
        // result, losing the newest state (the file said collapsed while the
        // screen showed expanded). Queue instead of dropping: the last gesture
        // always lands on disk.
        if (_workspaceSaveInFlight)
        {
            _workspaceSaveQueued = true;
            return;
        }
        _ = SaveWorkspaceLayoutCoreAsync();
    }

    private async Task SaveWorkspaceLayoutCoreAsync()
    {
        _workspaceSaveInFlight = true;
        try
        {
            var directory = Path.GetDirectoryName(WorkspaceLayoutPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(_workspaceLayout);
            var temporary = WorkspaceLayoutPath + ".tmp";
            await File.WriteAllTextAsync(temporary, payload);
            File.Move(temporary, WorkspaceLayoutPath, true);
        }
        catch (Exception)
        {
            // A failed layout save must never break a resize/collapse gesture.
        }
        finally
        {
            _workspaceSaveInFlight = false;
            if (_workspaceSaveQueued)
            {
                _workspaceSaveQueued = false;
                SaveWorkspaceLayoutAsync();
            }
        }
    }

    private void ApplyWorkspaceLayout(EditorWorkspaceState state)
    {
        ApplySideColumn(SourceColumn, state.SourceWidth, _sourceColumnMinWidth, _sourceColumnMaxWidth);
        ApplySideColumn(DetailsColumn, state.DetailsWidth, _detailsColumnMinWidth, _detailsColumnMaxWidth);
        ApplyTimelineHeight(state.TimelineHeight);
        ApplyTimelineHeaderWidth(state.TimelineHeaderWidth);
        _sourceRestoreWidth = Math.Clamp(state.SourceRestoreWidth, _sourceColumnMinWidth, _sourceColumnMaxWidth);
        _timelineRestoreHeight = Math.Clamp(state.TimelineRestoreHeight, _timelineRowMinHeight, _timelineRowMaxHeight);
        SetSourceCollapsed(state.SourceCollapsed);
        SetTimelineCollapsed(state.TimelineCollapsed);
    }

    private static void ApplySideColumn(ColumnDefinition column, double width, double minimum, double maximum)
    {
        var value = double.IsFinite(width) && width > 1 ? width : minimum;
        column.Width = new GridLength(Math.Clamp(value, minimum, maximum));
    }

    private void ApplyTimelineHeight(double height)
    {
        var value = double.IsFinite(height) && height > 1 ? height : _timelineRowMinHeight;
        _timelineRestoreHeight = Math.Clamp(value, _timelineRowMinHeight, _timelineRowMaxHeight);
        if (_timelineCollapsed) return;
        TimelineRow.Height = new GridLength(_timelineRestoreHeight);
    }

    private void ApplyTimelineHeaderWidth(double width)
    {
        var value = double.IsFinite(width) && width > 1 ? width : _timelineHeaderMinWidth;
        TimelineHeaderColumn.Width = new GridLength(Math.Clamp(value, _timelineHeaderMinWidth, _timelineHeaderMaxWidth));
    }

    private void NormalizeSideColumnToPixels(ColumnDefinition column, double fallback)
    {
        if (column.Width.GridUnitType == GridUnitType.Pixel) return;
        var width = column.ActualWidth > 1 ? column.ActualWidth : fallback;
        column.Width = new GridLength(Math.Clamp(width, column.MinWidth is double.NaN ? fallback : column.MinWidth, column.MaxWidth is double.NaN ? fallback : column.MaxWidth));
    }

    private void SourcePlayerSplitter_DragDelta(object sender, double delta)
    {
        // A collapsed column is zero-wide: dragging its splitter would stretch
        // it back out on screen while the collapsed flag still said the
        // opposite, so the next save restored the wrong state.
        if (_sourceCollapsed) return;
        NormalizeSideColumnToPixels(SourceColumn, _workspaceLayout.SourceWidth);
        SourceColumn.Width = new GridLength(Math.Clamp(SourceColumn.Width.Value + delta, _sourceColumnMinWidth, _sourceColumnMaxWidth));
        _workspaceLayout.SourceWidth = SourceColumn.Width.Value;
        _workspaceLayout.SourceRestoreWidth = _workspaceLayout.SourceWidth;
    }

    private void PlayerDetailsSplitter_DragDelta(object sender, double delta)
    {
        NormalizeSideColumnToPixels(DetailsColumn, _workspaceLayout.DetailsWidth);
        DetailsColumn.Width = new GridLength(Math.Clamp(DetailsColumn.Width.Value - delta, _detailsColumnMinWidth, _detailsColumnMaxWidth));
        _workspaceLayout.DetailsWidth = DetailsColumn.Width.Value;
    }

    private void NormalizeTimelineRowToPixels()
    {
        if (TimelineRow.Height.GridUnitType == GridUnitType.Pixel) return;
        var height = TimelineRow.ActualHeight > 1 ? TimelineRow.ActualHeight : _timelineRestoreHeight;
        TimelineRow.Height = new GridLength(Math.Clamp(height, _timelineRowMinHeight, _timelineRowMaxHeight));
    }

    private void NormalizeTimelineHeaderToPixels()
    {
        if (TimelineHeaderColumn.Width.GridUnitType == GridUnitType.Pixel) return;
        var width = TimelineHeaderColumn.ActualWidth > 1 ? TimelineHeaderColumn.ActualWidth : _timelineHeaderMinWidth;
        TimelineHeaderColumn.Width = new GridLength(Math.Clamp(width, _timelineHeaderMinWidth, _timelineHeaderMaxWidth));
    }

    private void WorkspaceTimelineSplitter_DragDelta(object sender, double delta)
    {
        if (_timelineCollapsed) return;
        // Read the already-applied pixel height (not ActualHeight, which lags one
        // layout pass and swallows deltas during fast drags).
        NormalizeTimelineRowToPixels();
        // The timeline row sits BELOW the splitter: pulling the splitter down must
        // SHRINK the row, so the delta is subtracted (mirrors Player|Details).
        TimelineRow.Height = new GridLength(Math.Clamp(TimelineRow.Height.Value - delta, _timelineRowMinHeight, _timelineRowMaxHeight));
        _timelineRestoreHeight = TimelineRow.Height.Value;
        _workspaceLayout.TimelineHeight = _timelineRestoreHeight;
        _workspaceLayout.TimelineRestoreHeight = _timelineRestoreHeight;
    }

    private void TimelineHeaderSplitter_DragDelta(object sender, double delta)
    {
        // Header sits LEFT of its splitter: pull right widens it (delta added),
        // and the already-applied pixel width avoids the one-pass layout lag.
        NormalizeTimelineHeaderToPixels();
        TimelineHeaderColumn.Width = new GridLength(Math.Clamp(TimelineHeaderColumn.Width.Value + delta, _timelineHeaderMinWidth, _timelineHeaderMaxWidth));
        _workspaceLayout.TimelineHeaderWidth = TimelineHeaderColumn.Width.Value;
    }

    private async void WorkspaceSplitter_DragCompleted(object sender, EventArgs e)
    {
        await Task.Yield();
        CaptureWorkspaceLayout();
        SaveWorkspaceLayoutAsync();
    }

    private void CaptureWorkspaceLayout()
    {
        _workspaceLayout.SourceWidth = SourceColumn.ActualWidth > 1 ? SourceColumn.ActualWidth : _workspaceLayout.SourceWidth;
        _workspaceLayout.DetailsWidth = DetailsColumn.ActualWidth > 1 ? DetailsColumn.ActualWidth : _workspaceLayout.DetailsWidth;
        _workspaceLayout.TimelineHeight = _timelineRestoreHeight;
        _workspaceLayout.TimelineHeaderWidth = TimelineHeaderColumn.ActualWidth > 1 ? TimelineHeaderColumn.ActualWidth : _workspaceLayout.TimelineHeaderWidth;
        _workspaceLayout.SourceCollapsed = _sourceCollapsed;
        _workspaceLayout.TimelineCollapsed = _timelineCollapsed;
        _workspaceLayout.SourceRestoreWidth = _sourceRestoreWidth;
        _workspaceLayout.TimelineRestoreHeight = _timelineRestoreHeight;
    }

    private void SourceCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSourceCollapsed(!_sourceCollapsed);
        CaptureWorkspaceLayout();
        SaveWorkspaceLayoutAsync();
    }

    private void TimelineCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        SetTimelineCollapsed(!_timelineCollapsed);
        CaptureWorkspaceLayout();
        SaveWorkspaceLayoutAsync();
    }

    private void SetSourceCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            if (SourceColumn.ActualWidth > 1) _sourceRestoreWidth = Math.Clamp(SourceColumn.ActualWidth, _sourceColumnMinWidth, _sourceColumnMaxWidth);
            SourceColumn.MinWidth = 0;
            SourceColumn.Width = new GridLength(0);
            _sourceCollapsed = true;
        }
        else
        {
            SourceColumn.MinWidth = _sourceColumnMinWidth;
            SourceColumn.Width = new GridLength(_sourceRestoreWidth);
            _sourceCollapsed = false;
        }
        SourceCollapseButton.Content = collapsed ? "»" : "«";
    }

    private void SetTimelineCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            if (TimelineRow.ActualHeight > 1) _timelineRestoreHeight = Math.Clamp(TimelineRow.ActualHeight, _timelineRowMinHeight, _timelineRowMaxHeight);
            TimelineRow.MinHeight = 0;
            TimelineRow.Height = new GridLength(0);
            _timelineCollapsed = true;
        }
        else
        {
            TimelineRow.MinHeight = _timelineRowMinHeight;
            TimelineRow.Height = new GridLength(_timelineRestoreHeight);
            _timelineCollapsed = false;
        }
        TimelineCollapseButton.Content = collapsed ? "▔" : "▁";
    }
}

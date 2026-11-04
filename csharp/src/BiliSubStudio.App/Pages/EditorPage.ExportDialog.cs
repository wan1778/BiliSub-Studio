using System.Globalization;
using BiliSubStudio.Core.Editor;
using BiliSubStudio.Core.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class EditorPage
{
    private EditorExportSettings _lastExportSettings = EditorExportSettings.Default;
    private EditorOutputTarget? _confirmedExportTarget;
    private bool _syncingExportDialog;

    private async Task<EditorOutputTarget?> ShowExportDialogAsync()
    {
        if (_path is null || _media is null || _project is null)
        {
            StatusText.Text = "Chưa chọn video để xuất.";
            return null;
        }

        _confirmedExportTarget = null;
        ExportVideoDialog.XamlRoot = XamlRoot;
        // While a dialog is open, the page's shortcuts must not touch the
        // timeline behind it.
        using var dialogScope = BeginEditorDialogScope();
        await ExportVideoDialog.ShowAsync();
        if (_confirmedExportTarget is not { } target) return null;

        await _application.Settings.SetEditorOutputDirectoryAsync(target.Directory, CancellationToken.None);
        // The file name confirmed by the dialog (the tab's box has been removed):
        // save it into the project so it is still shown the next time the dialog
        // opens, and the Export button still knows the name before the dialog is
        // opened.
        _project = _project with { FileName = target.FileName };
        _lastExportSettings = target.Settings;
        QueueProjectSave();
        return target;
    }

    private void ExportVideoDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _syncingExportDialog = true;
        try
        {
            var sourceName = string.IsNullOrWhiteSpace(_project?.FileName) ? "video_edited.mp4" : _project.FileName;
            ExportDialogFileNameBox.Text = FileNamePolicy.NormalizeVideoOutputName(sourceName);
            ExportDialogDirectoryBox.Text = _application.Config.EditorOutputDirectory;
            SelectByTag(ExportDialogContainerBox, Path.GetExtension(ExportDialogFileNameBox.Text).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "mkv" : "mp4");
            SelectByTag(ExportDialogCodecBox, _lastExportSettings.Codec);
            SetExportQuality(_lastExportSettings.Quality);
            SelectByTag(ExportDialogResolutionBox, _lastExportSettings.TargetHeight?.ToString(CultureInfo.InvariantCulture) ?? "source");
            SelectByTag(ExportDialogFpsBox, _lastExportSettings.FrameRate?.ToString("0", CultureInfo.InvariantCulture) ?? "source");
            SelectByTag(ExportDialogAudioBitrateBox, _lastExportSettings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
            ExportDialogGpuToggle.IsOn = _lastExportSettings.PreferHardwareAcceleration;
            SyncDialogTrimBoxes();
            UpdateDialogTrimSummary();
            if (_media is not null)
            {
                ((ComboBoxItem)ExportDialogResolutionBox.Items[0]).Content = $"Giữ nguyên nguồn · {_media.Width} × {_media.Height}";
                ((ComboBoxItem)ExportDialogFpsBox.Items[0]).Content = _media.FrameRate > 0
                    ? $"Giữ nguyên nguồn · {_media.FrameRate:0.###} FPS"
                    : "Giữ nguyên FPS nguồn";
            }
            ExportDialogValidationText.Text = string.Empty;
        }
        finally { _syncingExportDialog = false; }
        UpdateExportDialogSummary();
    }

    private void ExportDialogStart_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var directoryText = ExportDialogDirectoryBox.Text?.Trim() ?? string.Empty;
            if (directoryText.Length == 0) throw new InvalidDataException("Hãy chọn thư mục lưu video.");
            var directory = Path.GetFullPath(directoryText);
            var rawName = ExportDialogFileNameBox.Text?.Trim() ?? string.Empty;
            if (rawName.Length == 0) throw new InvalidDataException("Tên file đầu ra không được để trống.");
            if (!_trimRangeValid) throw new InvalidDataException("Khoảng cắt chưa hợp lệ; sửa giờ Bắt đầu/Kết thúc (hh:mm:ss).");
            var container = SelectedTag(ExportDialogContainerBox, "mp4");
            var fileName = FileNamePolicy.Sanitize(rawName, "BiliSub_edited." + container);
            fileName = Path.GetFileNameWithoutExtension(fileName) + "." + container;
            var settings = ReadExportDialogSettings();
            _confirmedExportTarget = new EditorOutputTarget(directory, fileName, settings);
            ExportDialogValidationText.Text = string.Empty;
            ExportVideoDialog.Hide();
        }
        catch (Exception error)
        {
            _confirmedExportTarget = null;
            ExportDialogValidationText.Text = error.Message;
        }
    }

    private void ExportDialogCancel_Click(object sender, RoutedEventArgs args)
    {
        _confirmedExportTarget = null;
        ExportDialogValidationText.Text = string.Empty;
        ExportVideoDialog.Hide();
    }

    private void ExportDialogSetting_Changed(object sender, RoutedEventArgs args)
    {
        if (_syncingExportDialog || !IsLoaded) return;
        if (ReferenceEquals(sender, ExportDialogContainerBox)) NormalizeDialogFileExtension();
        UpdateExportDialogSummary();
    }

    private void ExportDialogQuality_Click(object sender, RoutedEventArgs args)
    {
        if (_syncingExportDialog) return;
        SetExportQuality(ReferenceEquals(sender, ExportDialogQualityStandardButton)
            ? "standard"
            : ReferenceEquals(sender, ExportDialogQualityCompactButton) ? "compact" : "high");
        UpdateExportDialogSummary();
    }

    private void SetExportQuality(string quality)
    {
        var previous = _syncingExportDialog;
        _syncingExportDialog = true;
        try
        {
            ExportDialogQualityHighButton.IsChecked = quality == "high";
            ExportDialogQualityStandardButton.IsChecked = quality == "standard";
            ExportDialogQualityCompactButton.IsChecked = quality == "compact";
        }
        finally { _syncingExportDialog = previous; }
    }

    private void NormalizeDialogFileExtension()
    {
        var container = SelectedTag(ExportDialogContainerBox, "mp4");
        var raw = ExportDialogFileNameBox.Text?.Trim() ?? "video_edited";
        if (raw.Length == 0) raw = "video_edited";
        ExportDialogFileNameBox.Text = Path.GetFileNameWithoutExtension(raw) + "." + container;
    }

    // Trimming video inside the dialog (standard hh:mm:ss times instead of raw
    // seconds): the tab's box has been removed, so the dialog is the sole owner.
    // It writes straight to _project.Trim and saves the project immediately like
    // the old tab did (so the next dialog opening still sees it).
    private static string FormatClockFull(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.ToString(@"hh\:mm\:ss");
    }

    internal static double TryParseClockToSeconds(string? text, double duration)
    {
        var cleaned = (text ?? string.Empty).Trim().Replace(',', '.');
        if (cleaned.Length == 0)
            throw new InvalidDataException("Giờ cắt không được để trống (hh:mm:ss).");
        var parts = cleaned.Split(':', StringSplitOptions.RemoveEmptyEntries);
        double seconds;
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            seconds = hours * 3600 + minutes * 60 + secs;
        }
        else if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutesOnly)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secsOnly))
        {
            seconds = minutesOnly * 60 + secsOnly;
        }
        else if (parts.Length == 1
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rawSeconds))
        {
            seconds = rawSeconds;
        }
        else
        {
            throw new InvalidDataException($"Giờ không hợp lệ: {text} (dùng hh:mm:ss).");
        }
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > duration)
            throw new InvalidDataException($"Giờ phải trong 00:00:00–{TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}.");
        return seconds;
    }

    private void SyncDialogTrimBoxes()
    {
        if (_media is null || _project is null) return;
        var trim = CurrentTrimRange();
        ExportDialogTrimStartBox.Text = FormatClockFull(trim.Start);
        ExportDialogTrimEndBox.Text = FormatClockFull(trim.End);
    }

    private void ExportDialogTrimBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingExportDialog || !IsLoaded) return;
        CommitDialogTrim(note: null);
    }

    private void CommitDialogTrim(string? note)
    {
        if (_media is null || _project is null) return;
        try
        {
            var start = TryParseClockToSeconds(ExportDialogTrimStartBox.Text, _media.Duration);
            var end = TryParseClockToSeconds(ExportDialogTrimEndBox.Text, _media.Duration);
            var trim = EditorProjectStore.NormalizeTrim(new EditorTrimRange(start, end), _media.Duration);
            _trimRangeValid = true;
            _project = _project with { Trim = trim };
            UpdateDialogTrimSummary(trim);
            QueueProjectSave();
            RefreshEditorActions();
            UpdateExportDialogSummary();
            if (note is not null) ExportDialogValidationText.Text = string.Empty;
        }
        catch (Exception error)
        {
            _trimRangeValid = false;
            ExportDialogTrimSummaryText.Text = error.Message;
            RefreshEditorActions();
        }
    }

    private void UpdateDialogTrimSummary() => UpdateDialogTrimSummary(CurrentTrimRange());

    private void UpdateDialogTrimSummary(EditorTrimRange trim)
    {
        if (_media is null)
        {
            ExportDialogTrimSummaryText.Text = "Chưa chọn video.";
            return;
        }
        var removedStart = trim.Start;
        var removedEnd = Math.Max(0, _media.Duration - trim.End);
        ExportDialogTrimSummaryText.Text = EditorProjectStore.HasTrim(trim, _media.Duration)
            ? $"Giữ {FormatClockFull(trim.Start)} → {FormatClockFull(trim.End)} · dài {FormatClockFull(trim.Duration)} · bỏ đầu {FormatClockFull(removedStart)} · bỏ cuối {FormatClockFull(removedEnd)}."
            : $"Giữ toàn bộ video · {FormatClockFull(_media.Duration)}.";
    }

    private void ExportDialogTrimStartCurrent_Click(object sender, RoutedEventArgs e) =>
        SetDialogTrimBoundaryFromCurrent(setStart: true);

    private void ExportDialogTrimEndCurrent_Click(object sender, RoutedEventArgs e) =>
        SetDialogTrimBoundaryFromCurrent(setStart: false);

    private void SetDialogTrimBoundaryFromCurrent(bool setStart)
    {
        if (_media is null || _project is null || EditorBusy || _playback.IsPreviewMode) return;
        // Trim marks live in the primary clip's source-file coordinates, while
        // the timeline's current position is composed-timeline coordinates: it
        // must be mapped through the clip containing it.
        var sourceTime = CurrentPrimarySourceTime();
        if (sourceTime is not { } current0)
        {
            ExportDialogValidationText.Text = "Chỉ đặt được mốc giữ lại khi con trỏ đang ở clip gốc của dự án.";
            return;
        }
        var value = current0;
        var current = CurrentTrimRange();
        var candidate = EditorProjectStore.NormalizeTrim(
            setStart ? new EditorTrimRange(value, current.End) : new EditorTrimRange(current.Start, value),
            _media.Duration);
        _syncingExportDialog = true;
        try
        {
            ExportDialogTrimStartBox.Text = FormatClockFull(candidate.Start);
            ExportDialogTrimEndBox.Text = FormatClockFull(candidate.End);
        }
        finally { _syncingExportDialog = false; }
        CommitDialogTrim(note: null);
        ExportDialogValidationText.Text = $"Đã đặt {(setStart ? "đầu" : "cuối")} đoạn giữ lại tại {FormatClockFull(value)}.";
    }

    private void ExportDialogTrimReset_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || _project is null || EditorBusy || _playback.IsPreviewMode) return;
        var trim = new EditorTrimRange(0, _media.Duration);
        _syncingExportDialog = true;
        try
        {
            ExportDialogTrimStartBox.Text = FormatClockFull(trim.Start);
            ExportDialogTrimEndBox.Text = FormatClockFull(trim.End);
        }
        finally { _syncingExportDialog = false; }
        CommitDialogTrim(note: null);
        ExportDialogValidationText.Text = "Đã đặt lại: giữ toàn bộ video.";
    }

    private EditorExportSettings ReadExportDialogSettings()
    {
        int? targetHeight = int.TryParse(SelectedTag(ExportDialogResolutionBox, "source"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? height
            : null;
        double? frameRate = double.TryParse(SelectedTag(ExportDialogFpsBox, "source"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
            ? fps
            : null;
        var bitrate = int.Parse(SelectedTag(ExportDialogAudioBitrateBox, "192"), CultureInfo.InvariantCulture);
        var quality = ExportDialogQualityStandardButton.IsChecked == true
            ? "standard"
            : ExportDialogQualityCompactButton.IsChecked == true ? "compact" : "high";
        return EditorExportPolicy.Normalize(new EditorExportSettings(
            SelectedTag(ExportDialogCodecBox, "h264"), quality, targetHeight, frameRate,
            ExportDialogGpuToggle.IsOn, bitrate));
    }

    private void UpdateExportDialogSummary()
    {
        if (_media is null || _path is null) return;
        try
        {
            var settings = ReadExportDialogSettings();
            var trim = CurrentTrimRange();
            var exportDuration = ComposedExportDuration(trim);
            ExportDialogDurationText.Text = "Thời lượng: " + TimeSpan.FromSeconds(exportDuration).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var inputBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (inputBytes <= 0)
            {
                ExportDialogEstimateText.Text = "Ước tính: chưa đủ dữ liệu";
                return;
            }

            var sourceGiB = inputBytes / 1024d / 1024 / 1024;
            var durationFactor = _media.Duration > 0 ? exportDuration / _media.Duration : 1;
            var dimensions = EditorExportPolicy.ResolveDimensions(settings, _media.Width, _media.Height);
            var pixelFactor = dimensions.Width * (double)dimensions.Height / (_media.Width * (double)_media.Height);
            var fpsFactor = settings.FrameRate is double fps && _media.FrameRate > 0 ? fps / _media.FrameRate : 1;
            var (lowFactor, highFactor) = settings.Quality switch
            {
                "standard" => (.9, 2.2),
                "compact" => (.55, 1.3),
                _ => (1.5, 4.0),
            };
            var codecFactor = settings.Codec == "hevc" ? .68 : 1;
            var contentBase = sourceGiB * durationFactor * Math.Max(.12, pixelFactor) * Math.Max(.5, fpsFactor) * codecFactor;
            var audioGiB = exportDuration * settings.AudioBitrateKbps * 1000 / 8 / 1024d / 1024 / 1024;
            var low = Math.Max(audioGiB, contentBase * lowFactor);
            var high = Math.Max(low + .1, contentBase * highFactor);
            ExportDialogEstimateText.Text = $"Ước tính: {low:0.0}–{high:0.0} GB";
        }
        catch (Exception error)
        {
            ExportDialogEstimateText.Text = "Ước tính: không khả dụng";
            ExportDialogValidationText.Text = error.Message;
        }
    }

    /// <summary>
    /// Video duration after exporting: a single-clip timeline uses the kept
    /// range; a multi-clip timeline sums each clip's duration (the primary clip
    /// uses the kept range).
    /// </summary>
    private double ComposedExportDuration(EditorTrimRange trim)
    {
        if (_timelineClips.Count <= 1) return trim.Duration;
        var total = 0d;
        foreach (var clip in _timelineClips)
        {
            if (!IsPrimaryTimelineClip(clip))
            {
                total += clip.Duration;
                continue;
            }
            var start = Math.Max(clip.SourceIn, trim.Start);
            var end = Math.Min(clip.End, trim.End);
            total += end - start >= .1 ? end - start : clip.Duration;
        }
        return total;
    }

    private static string SelectedTag(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag.Length > 0 ? tag : fallback;

    private static void SelectByTag(ComboBox box, string tag)
    {
        for (var index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                return;
            }
        }
        box.SelectedIndex = 0;
    }
}

using System.Globalization;
using BiliSubStudio.App.Services;
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
        await ExportVideoDialog.ShowAsync();
        if (_confirmedExportTarget is not { } target) return null;

        await _application.Settings.SetOutputDirectoryAsync(target.Directory, CancellationToken.None);
        EditorOutputPathText.Text = target.Directory;
        FileNameBox.Text = target.FileName;
        _lastExportSettings = target.Settings;
        QueueProjectSave();
        return target;
    }

    private void ExportVideoDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _syncingExportDialog = true;
        try
        {
            var sourceName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? "video_edited.mp4" : FileNameBox.Text;
            ExportDialogFileNameBox.Text = FileNamePolicy.NormalizeVideoOutputName(sourceName);
            ExportDialogDirectoryBox.Text = _application.Config.OutputDirectory;
            SelectByTag(ExportDialogContainerBox, Path.GetExtension(ExportDialogFileNameBox.Text).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "mkv" : "mp4");
            SelectByTag(ExportDialogCodecBox, _lastExportSettings.Codec);
            SetExportQuality(_lastExportSettings.Quality);
            SelectByTag(ExportDialogResolutionBox, _lastExportSettings.TargetHeight?.ToString(CultureInfo.InvariantCulture) ?? "source");
            SelectByTag(ExportDialogFpsBox, _lastExportSettings.FrameRate?.ToString("0", CultureInfo.InvariantCulture) ?? "source");
            SelectByTag(ExportDialogAudioBitrateBox, _lastExportSettings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
            ExportDialogGpuToggle.IsOn = _lastExportSettings.PreferHardwareAcceleration;
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
            var container = SelectedTag(ExportDialogContainerBox, "mp4");
            var rawName = ExportDialogFileNameBox.Text?.Trim() ?? string.Empty;
            if (rawName.Length == 0) throw new InvalidDataException("Tên file đầu ra không được để trống.");
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

    private async void ExportDialogChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = Microsoft.UI.Xaml.Application.Current as BiliSubStudio.App.App
                ?? throw new InvalidOperationException("Không lấy được cửa sổ chính.");
            var window = app.MainWindow ?? throw new InvalidOperationException("Cửa sổ chính chưa sẵn sàng.");
            var picker = new FolderPickerService(() => window);
            var path = await picker.PickFolderAsync(ExportDialogDirectoryBox.Text);
            if (string.IsNullOrWhiteSpace(path)) return;
            ExportDialogDirectoryBox.Text = path;
            ExportDialogValidationText.Text = string.Empty;
        }
        catch (Exception error)
        {
            ExportDialogValidationText.Text = "Không chọn được thư mục: " + error.Message;
        }
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
            ExportDialogDurationText.Text = "Thời lượng: " + TimeSpan.FromSeconds(trim.Duration).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var inputBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (inputBytes <= 0)
            {
                ExportDialogEstimateText.Text = "Ước tính: chưa đủ dữ liệu";
                return;
            }

            var sourceGiB = inputBytes / 1024d / 1024 / 1024;
            var durationFactor = _media.Duration > 0 ? trim.Duration / _media.Duration : 1;
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
            var audioGiB = trim.Duration * settings.AudioBitrateKbps * 1000 / 8 / 1024d / 1024 / 1024;
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

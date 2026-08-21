using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Application;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.ViewModels;

public sealed class SettingsViewModel : BindableBase
{
    private readonly ISettingsApplicationService _settings;
    private readonly IFolderPickerService _folderPicker;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _checkUpdates;
    private string _theme = "dark";
    private string _outputDirectory = string.Empty;
    private string _root = string.Empty;
    private string _drive = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isStatusOpen;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private string _dataSize = "0 B";
    private string _toolsSize = "0 B";
    private string _ocrSize = "0 B";
    private string _tempSize = "0 B";
    private string _cacheSize = "0 B";
    private string _totalSize = "0 B";
    private double _dataRatio;
    private double _toolsRatio;
    private double _ocrRatio;
    private double _tempRatio;
    private double _cacheRatio;

    public SettingsViewModel(ISettingsApplicationService settings, IFolderPickerService folderPicker)
    {
        _settings = settings;
        _folderPicker = folderPicker;
        ChooseOutputDirectoryCommand = new AsyncCommand(ChooseOutputDirectoryAsync, () => !IsBusy);
        OpenOutputDirectoryCommand = new AsyncCommand(OpenOutputDirectoryAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
    }

    public bool IsInitialized { get => _isInitialized; private set => SetProperty(ref _isInitialized, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public bool CheckUpdates { get => _checkUpdates; private set => SetProperty(ref _checkUpdates, value); }
    public string Theme { get => _theme; private set => SetProperty(ref _theme, value); }
    public string OutputDirectory { get => _outputDirectory; private set => SetProperty(ref _outputDirectory, value); }
    public string Root { get => _root; private set => SetProperty(ref _root, value); }
    public string Drive { get => _drive; private set => SetProperty(ref _drive, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsStatusOpen { get => _isStatusOpen; private set => SetProperty(ref _isStatusOpen, value); }
    public InfoBarSeverity StatusSeverity { get => _statusSeverity; private set => SetProperty(ref _statusSeverity, value); }
    public string DataSize { get => _dataSize; private set => SetProperty(ref _dataSize, value); }
    public string ToolsSize { get => _toolsSize; private set => SetProperty(ref _toolsSize, value); }
    public string OcrSize { get => _ocrSize; private set => SetProperty(ref _ocrSize, value); }
    public string TempSize { get => _tempSize; private set => SetProperty(ref _tempSize, value); }
    public string CacheSize { get => _cacheSize; private set => SetProperty(ref _cacheSize, value); }
    public string TotalSize { get => _totalSize; private set => SetProperty(ref _totalSize, value); }
    public double DataRatio { get => _dataRatio; private set => SetProperty(ref _dataRatio, value); }
    public double ToolsRatio { get => _toolsRatio; private set => SetProperty(ref _toolsRatio, value); }
    public double OcrRatio { get => _ocrRatio; private set => SetProperty(ref _ocrRatio, value); }
    public double TempRatio { get => _tempRatio; private set => SetProperty(ref _tempRatio, value); }
    public double CacheRatio { get => _cacheRatio; private set => SetProperty(ref _cacheRatio, value); }

    public AsyncCommand ChooseOutputDirectoryCommand { get; }
    public AsyncCommand OpenOutputDirectoryCommand { get; }
    public AsyncCommand RefreshCommand { get; }

    public async Task<SettingsSnapshot> InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var snapshot = await _settings.InitializeAsync();
            Apply(snapshot);
            IsInitialized = true;
            SetStatus(
                snapshot.ConfigWarning ?? "Settings/Config C# đã tải và giữ tương thích với config.json của bản Go.",
                snapshot.ConfigWarning is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            return snapshot;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SetThemeAsync(string theme)
    {
        if (!IsInitialized || string.Equals(theme, Theme, StringComparison.Ordinal))
        {
            return true;
        }

        return await ExecuteUpdateAsync(
            () => _settings.SetThemeAsync(theme),
            snapshot => $"Đã chuyển giao diện sang {(snapshot.Config.Theme == "light" ? "Light" : "Dark")}.");
    }

    public async Task<bool> SetUpdateCheckAsync(bool enabled)
    {
        if (!IsInitialized || enabled == CheckUpdates)
        {
            return true;
        }

        return await ExecuteUpdateAsync(
            () => _settings.SetUpdateCheckAsync(enabled),
            snapshot => snapshot.Config.CheckUpdates
                ? "Đã bật tự kiểm tra cập nhật."
                : "Đã tắt tự kiểm tra cập nhật.");
    }

    private async Task ChooseOutputDirectoryAsync()
    {
        try
        {
            var selected = await _folderPicker.PickFolderAsync(OutputDirectory);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            await ExecuteUpdateAsync(
                () => _settings.SetOutputDirectoryAsync(selected),
                snapshot => $"Đã đổi thư mục lưu mặc định: {snapshot.Config.OutputDirectory}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task OpenOutputDirectoryAsync()
    {
        try
        {
            await _folderPicker.OpenFolderAsync(OutputDirectory);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Apply(await _settings.RefreshAsync());
            SetStatus("Đã cập nhật dung lượng ứng dụng.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ExecuteUpdateAsync(
        Func<Task<SettingsSnapshot>> update,
        Func<SettingsSnapshot, string> successMessage)
    {
        IsBusy = true;
        try
        {
            var snapshot = await update();
            Apply(snapshot);
            SetStatus(successMessage(snapshot), InfoBarSeverity.Success);
            return true;
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(SettingsSnapshot snapshot)
    {
        Theme = snapshot.Config.Theme;
        OutputDirectory = snapshot.Config.OutputDirectory;
        CheckUpdates = snapshot.Config.CheckUpdates;
        Root = snapshot.Root;
        Drive = snapshot.Drive;

        DataSize = FormatBytes(snapshot.Storage.Data);
        ToolsSize = FormatBytes(snapshot.Storage.Tools);
        OcrSize = FormatBytes(snapshot.Storage.Ocr);
        TempSize = FormatBytes(snapshot.Storage.Temp);
        CacheSize = FormatBytes(snapshot.Storage.Cache);
        TotalSize = FormatBytes(snapshot.Storage.Total);

        var maximum = Math.Max(1L, new[]
        {
            snapshot.Storage.Data,
            snapshot.Storage.Tools,
            snapshot.Storage.Ocr,
            snapshot.Storage.Temp,
            snapshot.Storage.Cache,
        }.Max());
        DataRatio = snapshot.Storage.Data / (double)maximum;
        ToolsRatio = snapshot.Storage.Tools / (double)maximum;
        OcrRatio = snapshot.Storage.Ocr / (double)maximum;
        TempRatio = snapshot.Storage.Temp / (double)maximum;
        CacheRatio = snapshot.Storage.Cache / (double)maximum;
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    private void RaiseCommandStates()
    {
        ChooseOutputDirectoryCommand.RaiseCanExecuteChanged();
        OpenOutputDirectoryCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{display:0.##} {units[unit]}";
    }
}

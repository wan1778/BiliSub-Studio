using Microsoft.UI.Xaml;
using BiliSubStudio.Core.Maintenance;
using BiliSubStudio.App.Services;

namespace BiliSubStudio.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const int OperationAbortedHResult = unchecked((int)0x80004004);

    public App()
    {
        StartupDiagnostics.Initialize();
        try
        {
            InitializeComponent();
            StartupDiagnostics.Write("app-xaml-ready");
        }
        catch (Exception error)
        {
            StartupDiagnostics.ShowFatalError("app-xaml-failed", error);
            throw;
        }
        UnhandledException += OnUnhandledException;
    }

    public MainWindow? MainWindow { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupDiagnostics.Write("launch-enter");
            if (await UpdateService.TryApplyFromCommandLineAsync(Environment.GetCommandLineArgs(), CancellationToken.None))
            {
                StartupDiagnostics.Write("update-handoff-complete");
                Exit();
                return;
            }
            MainWindow = new MainWindow();
            StartupDiagnostics.Write("main-window-constructed");
            MainWindow.Activate();
            StartupDiagnostics.Write("main-window-activated");

            if (StartupDiagnostics.IsSmokeTest)
            {
                await MainWindow.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
                await MainWindow.RunLayoutSmokeAsync();
                await StartupDiagnostics.WriteSmokeSentinelAsync();
                Exit();
            }
        }
        catch (Exception error)
        {
            StartupDiagnostics.ShowFatalError("launch-failed", error);
            Exit();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        if (MainWindow is not null && args.Exception.HResult == OperationAbortedHResult)
        {
            StartupDiagnostics.WriteException("winui-operation-aborted", args.Exception);
            return;
        }
        StartupDiagnostics.ShowFatalError("winui-unhandled", args.Exception);
        Exit();
    }
}

using Microsoft.UI.Xaml;
using BiliSubStudio.Core.Maintenance;

namespace BiliSubStudio.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public MainWindow? MainWindow { get; private set; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (await UpdateService.TryApplyFromCommandLineAsync(Environment.GetCommandLineArgs(), CancellationToken.None))
        {
            Exit();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine(args.Exception);
    }
}

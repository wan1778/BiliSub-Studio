using System.Reflection;
using BiliSubStudio.App.Pages;
using BiliSubStudio.Core.Maintenance;

namespace BiliSubStudio.App;

public sealed partial class MainWindow
{
    public string PublicVersionLabel
    {
        get
        {
            var technicalVersion = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
                ?? "4.0.0";
            return $"Phiên bản hiện tại {SupportPage.DisplayVersion(technicalVersion)}";
        }
    }
}

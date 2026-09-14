using System.Runtime.Versioning;
using Cadence.App.Launch;
using Velopack;

namespace Cadence.App;

/// <summary>
/// The entry point, replacing the one WPF generates so the installer's hooks run first.
/// </summary>
/// <remarks>
/// Setup and the updater start Cadence with hook arguments during install, update and uninstall.
/// <see cref="VelopackApp.Run"/> handles those and exits before any window, tray icon or
/// single-instance lock exists. On an ordinary launch it returns at once, after first applying any
/// update that finished downloading while the previous copy was running.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installation.OnInstalled())
            .OnBeforeUninstallFastCallback(_ => Installation.OnUninstalling())
            .OnFirstRun(_ => Installation.IsFirstRunAfterInstall = true)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

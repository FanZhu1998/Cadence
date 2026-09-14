using Cadence.App.Launch;
using Microsoft.Extensions.Logging;

namespace Cadence.App;

/// <summary>
/// Launch behaviour: bringing the running copy forward, and the one-time welcome.
/// </summary>
public partial class App
{
    private WelcomeWindow? _welcome;

    /// <summary>
    /// Shows Cadence in response to a launch: a second double-click, the Start menu entry, or the
    /// end of the welcome.
    /// </summary>
    private void ShowPanelFromLaunch()
    {
        if (Dispatcher.HasShutdownStarted) return;

        // While the welcome is up it is the thing to show; opening the panel over it would bury it.
        if (_welcome is { IsLoaded: true })
        {
            _welcome.Activate();
            return;
        }

        if (_flyout is null) return;

        RefreshUi();
        _flyout.ShowAnchored(null);
    }

    /// <summary>
    /// Screenshot runs render one surface and exit. They skip the single-instance lock, so they
    /// work while the real copy is running, and they must never show the welcome: a capture run is
    /// not a person seeing Cadence for the first time.
    /// </summary>
    private static bool IsAutomationLaunch()
        => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Once for a new user, and again after every install: Setup finishes by starting Cadence in the
    /// tray, where nothing shows that it worked.
    /// </summary>
    private bool ShouldShowWelcome()
        => (!_settings.FirstRunCompleted || Installation.IsFirstRunAfterInstall) && !IsAutomationLaunch();

    private void ShowWelcome()
    {
        var welcome = new WelcomeWindow(
            launchAtSignIn: true, offerStartMenu: _updates?.IsInstalled is not true, dark: _theme?.IsAppDark ?? true);

        welcome.Closed += async (_, _) =>
        {
            _welcome = null;
            await CompleteFirstRunAsync(welcome.ApplyChoices, welcome.LaunchAtSignIn, welcome.AddToStartMenu)
                .ConfigureAwait(true);
        };

        _welcome = welcome;
        welcome.Show();
        welcome.Activate();
    }

    private async Task CompleteFirstRunAsync(bool applyChoices, bool launchAtSignIn, bool addToStartMenu)
    {
        if (Dispatcher.HasShutdownStarted) return;

        if (applyChoices)
        {
            StartupIntegration.SetLaunchAtSignIn(launchAtSignIn);
            var shortcut = addToStartMenu && StartupIntegration.EnsureStartMenuShortcut();

            _settings = _settings with { LaunchAtStartup = launchAtSignIn };

            _loggerFactory.CreateLogger<App>().LogInformation(
                "First run: start at sign-in {SignIn}, Start menu entry {Shortcut}", launchAtSignIn, shortcut);
        }

        // Seen is seen. A welcome that keeps coming back until the user picks the "right" button is
        // a nag, not onboarding.
        _settings = _settings with { FirstRunCompleted = true };
        if (_settingsStore is not null) await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        ShowPanelFromLaunch();
    }
}

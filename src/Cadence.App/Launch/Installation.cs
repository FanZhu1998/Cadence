using System.Runtime.Versioning;

namespace Cadence.App.Launch;

/// <summary>
/// What the installer's hooks change outside the install folder.
/// </summary>
/// <remarks>
/// Setup owns the install folder, the shortcuts and the uninstall entry. The start-at-sign-in entry
/// belongs to Cadence, so Cadence keeps it pointing at a real exe at the two moments the exe's
/// location changes: when it is installed and when it is removed.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Installation
{
    /// <summary>
    /// Set on the first launch after Setup, so the welcome appears even for someone who already saw
    /// it while running a portable copy. Setup gives no other sign that Cadence started.
    /// </summary>
    public static bool IsFirstRunAfterInstall { get; set; }

    /// <summary>
    /// Setup has just put Cadence in its permanent home. A sign-in entry left by a portable copy
    /// still points at the copy the user is moving away from, so it moves to the installed exe.
    /// </summary>
    public static void OnInstalled()
    {
        if (StartupIntegration.IsLaunchAtSignInEnabled()) StartupIntegration.SetLaunchAtSignIn(true);
    }

    /// <summary>
    /// The exe is about to be deleted. An entry still pointing at it would have Windows try to start
    /// a missing file at every sign-in. An entry pointing at some other copy is left alone.
    /// </summary>
    public static void OnUninstalling()
    {
        if (StartupIntegration.SamePath(StartupIntegration.LaunchAtSignInTarget(), Environment.ProcessPath))
            StartupIntegration.SetLaunchAtSignIn(false);
    }
}

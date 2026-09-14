using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using Cadence.App.Interop;

namespace Cadence.App.Launch;

/// <summary>
/// The one-time welcome shown on first launch.
/// </summary>
/// <remarks>
/// Exists because a tray app with no window gives no sign that it started. Windows 11 files new tray
/// icons under the taskbar overflow arrow, so without this a first double-click shows nothing, the
/// user assumes it failed, and clicks again. A centred window is the one thing they cannot miss.
/// <para>
/// The options start ticked so the smooth path is a single click, but nothing is written until
/// Get started is pressed. Skip, Escape or closing the window changes nothing on the machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class WelcomeWindow : Window
{
    private readonly bool _offerStartMenu;

    /// <param name="launchAtSignIn">Whether start at sign-in starts ticked.</param>
    /// <param name="offerStartMenu">
    /// False for an installed copy: Setup already created its Start menu entry, and a second one
    /// pointing at the same exe would only be clutter.
    /// </param>
    /// <param name="dark">Whether the app theme is dark.</param>
    public WelcomeWindow(bool launchAtSignIn, bool offerStartMenu, bool dark)
    {
        InitializeComponent();

        _offerStartMenu = offerStartMenu;
        LaunchAtSignInCheck.IsChecked = launchAtSignIn;
        StartMenuCheck.IsChecked = offerStartMenu;
        StartMenuCheck.Visibility = offerStartMenu ? Visibility.Visible : Visibility.Collapsed;

        // Rounded corners and no system backdrop; the page brush is the background.
        SourceInitialized += (_, _) => Win32.ApplyBackdrop(this, Win32.BackdropType.None, dark);
        MouseLeftButtonDown += OnDragArea;
        Loaded += (_, _) => GetStartedButton.Focus();
    }

    /// <summary>True only when the user chose Get started. Closing any other way applies nothing.</summary>
    public bool ApplyChoices { get; private set; }

    public bool LaunchAtSignIn => LaunchAtSignInCheck.IsChecked is true;

    public bool AddToStartMenu => _offerStartMenu && StartMenuCheck.IsChecked is true;

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        ApplyChoices = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    /// <summary>The window has no title bar, so its body is the drag handle.</summary>
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState is not MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag began.
        }
    }
}

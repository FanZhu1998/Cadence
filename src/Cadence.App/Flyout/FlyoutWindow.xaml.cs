using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cadence.App.Interop;
using Cadence.App.ViewModels;

namespace Cadence.App.Flyout;

/// <summary>
/// The flyout anchored to the tray icon: the real UI.
/// </summary>
/// <remarks>
/// Closes on deactivation, Escape, or a second tray click — never on mouse-leave. Closing on
/// mouse-leave is a macOS habit that makes a panel with buttons in it impossible to use.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class FlyoutWindow : Window
{
    private readonly Storyboard _spin;
    private bool _closingSuppressed;

    /// <summary>Raised when the user asks to quit, so the host can shut down cleanly.</summary>
    public event Action? QuitRequested;

    public event Action? SettingsRequested;

    public event Action? HistoryRequested;

    public FlyoutWindow()
    {
        InitializeComponent();

        _spin = BuildSpinStoryboard();

        // A tool window never appears in Alt-Tab. It does take focus, which is what lets
        // deactivation close it.
        SourceInitialized += (_, _) => Win32.MakeToolWindow(this);
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public FlyoutViewModel? ViewModel => DataContext as FlyoutViewModel;

    /// <summary>True while the panel is visible, which the refresh cadence uses to speed up.</summary>
    public bool IsOpen => IsVisible;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Shows the flyout anchored to a tray icon rectangle.
    /// </summary>
    /// <param name="iconRect">
    /// The icon's screen rectangle in physical pixels, or null when the shell will not report it
    /// (the icon is in the overflow flyout), in which case the panel goes to the corner nearest
    /// the notification area.
    /// </param>
    public void ShowAnchored(Win32.Rect? iconRect)
    {
        // Measure before positioning: SizeToContent means ActualHeight is meaningless until the
        // content has been laid out at least once.
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea; // device-independent units

        double left, top;

        if (iconRect is { } rect)
        {
            var iconLeft = rect.Left / dpi.DpiScaleX;
            var iconTop = rect.Top / dpi.DpiScaleY;
            var iconBottom = rect.Bottom / dpi.DpiScaleY;
            var iconCentre = iconLeft + (rect.Width / dpi.DpiScaleX / 2);

            left = iconCentre - (ActualWidth / 2);

            // Above the icon for a bottom taskbar, below it for a top one.
            var taskbarAtTop = iconTop < work.Top + (work.Height / 2);
            top = taskbarAtTop ? iconBottom + 8 : iconTop - ActualHeight - 8;
        }
        else
        {
            left = work.Right - ActualWidth - 12;
            top = work.Bottom - ActualHeight - 12;
        }

        // Clamp into the work area of whichever monitor we landed on, so the panel is never
        // half off-screen or behind the taskbar.
        Left = Math.Clamp(left, work.Left + 8, Math.Max(work.Left + 8, work.Right - ActualWidth - 8));
        Top = Math.Clamp(top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - ActualHeight - 8));

        Activate();
        Focus();
    }

    /// <summary>Hides rather than closes: recreating the window on every open is visibly slow.</summary>
    public void HideFlyout()
    {
        if (_closingSuppressed) return;
        Hide();
    }

    /// <summary>Toggles visibility, which is what a tray click does.</summary>
    public void Toggle(Win32.Rect? iconRect)
    {
        if (IsVisible) HideFlyout();
        else ShowAnchored(iconRect);
    }

    /// <summary>Applies theme colours and the Mica backdrop.</summary>
    public void ApplyTheme(bool dark)
    {
        Win32.ApplyBackdrop(this, Win32.BackdropType.Mica, dark);
    }

    /// <summary>Starts or stops the refresh glyph spinning, without moving anything around it.</summary>
    public void SetRefreshing(bool refreshing)
    {
        if (refreshing) _spin.Begin(this, isControllable: true);
        else _spin.Stop(this);
    }

    private Storyboard BuildSpinStoryboard()
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, RefreshGlyph);
        Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.Angle"));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    /// <summary>
    /// Runs an action that opens another window without the flyout closing underneath it.
    /// </summary>
    private void WithoutAutoClose(Action action)
    {
        _closingSuppressed = true;
        try
        {
            action();
        }
        finally
        {
            _closingSuppressed = false;
            Hide();
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => WithoutAutoClose(() => SettingsRequested?.Invoke());

    private void OnHistoryClick(object sender, RoutedEventArgs e) => WithoutAutoClose(() => HistoryRequested?.Invoke());

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        _closingSuppressed = true;
        QuitRequested?.Invoke();
    }
}

using System.Runtime.Versioning;
using System.Windows;
using Cadence.App.Interop;
using Cadence.Core.Model;
using Microsoft.Win32;
using ThemeMode = Cadence.Core.Model.ThemeMode;

namespace Cadence.App.Theme;

/// <summary>
/// Swaps the colour dictionary and tells everyone when the shell theme changes.
/// </summary>
/// <remarks>
/// WPF has no equivalent of a macOS template image, so both light and dark are rendered explicitly
/// and the tray icon cache is invalidated on every switch. The taskbar and app themes are separate
/// Windows settings, so the icon follows the shell and the flyout follows apps.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ThemeManager : IDisposable
{
    private static readonly Uri DarkUri = new("Theme/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("Theme/Light.xaml", UriKind.Relative);

    private ResourceDictionary? _current;
    private ThemeMode _mode = ThemeMode.System;
    private bool _disposed;

    /// <summary>Raised after the applied theme changes, so icons can be re-rendered.</summary>
    public event Action? Changed;

    public ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the flyout and windows should render dark.</summary>
    public bool IsAppDark { get; private set; }

    /// <summary>True when the taskbar is dark, which is what the tray icon must contrast against.</summary>
    public bool IsShellDark { get; private set; } = true;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;

        IsAppDark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => Win32.IsAppDarkTheme(),
        };

        // The tray icon always follows the shell: an explicit app-theme choice does not repaint
        // the taskbar behind it, and a dark icon on a dark taskbar is invisible.
        IsShellDark = Win32.IsShellDarkTheme();

        var uri = IsAppDark ? DarkUri : LightUri;
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        var replacement = new ResourceDictionary { Source = uri };

        if (_current is not null) dictionaries.Remove(_current);

        // Inserted first so Tokens and Controls, which reference these brushes dynamically,
        // continue to resolve.
        dictionaries.Insert(0, replacement);
        _current = replacement;

        Changed?.Invoke();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color))
        {
            return;
        }

        // The registry value can lag the broadcast slightly; re-reading on the dispatcher after
        // the current message settles avoids applying the theme that is on its way out.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) Apply(_mode);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}

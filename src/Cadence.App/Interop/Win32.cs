using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Cadence.App.Interop;

/// <summary>
/// The Win32 surface the flyout and tray need: icon geometry, DWM backdrop and corners, work-area
/// clamping, and the shell's theme preference.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Win32
{
    // ---- window styles ---------------------------------------------------------------------------

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>
    /// Marks a window as a tool window so it never appears in Alt-Tab or the taskbar.
    /// </summary>
    /// <remarks>
    /// A tray flyout showing up in Alt-Tab is the clearest possible signal that an app was built by
    /// someone who did not use it.
    /// </remarks>
    public static void MakeToolWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        var style = GetWindowLongPtrW(handle, GwlExStyle);
        SetWindowLongPtrW(handle, GwlExStyle, style | WsExToolWindow);
    }

    /// <summary>Makes a window click-through, for the HUD when it is not hovered.</summary>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        const int WsExTransparent = 0x00000020;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        var style = GetWindowLongPtrW(handle, GwlExStyle);
        var updated = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;

        SetWindowLongPtrW(handle, GwlExStyle, updated);
    }

    /// <summary>Prevents a window taking focus when shown, for the HUD.</summary>
    public static void SetNoActivate(Window window, bool noActivate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        var style = GetWindowLongPtrW(handle, GwlExStyle);
        SetWindowLongPtrW(handle, GwlExStyle, noActivate ? style | WsExNoActivate : style & ~WsExNoActivate);
    }

    // ---- DWM -------------------------------------------------------------------------------------

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;

    public enum CornerPreference { Default = 0, DoNotRound = 1, Round = 2, RoundSmall = 3 }

    public enum BackdropType { Auto = 0, None = 1, Mica = 2, Acrylic = 3, MicaAlt = 4 }

    /// <summary>
    /// Applies the Mica backdrop and rounded corners.
    /// </summary>
    /// <remarks>
    /// Every call is best-effort: these attributes are ignored on builds that do not support them,
    /// and the window still renders correctly with its own background. Failing here must never stop
    /// the flyout appearing.
    /// </remarks>
    public static void ApplyBackdrop(Window window, BackdropType type, bool dark, CornerPreference corner = CornerPreference.Round)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;

        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var cornerValue = (int)corner;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref cornerValue, sizeof(int));

        var backdrop = (int)type;
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }

    // ---- tray icon geometry ------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    /// <summary>
    /// The screen rectangle of a tray icon, in physical pixels, or null when the shell will not say.
    /// </summary>
    /// <remarks>
    /// Returns null while the icon sits in the overflow flyout, which is the common case on a busy
    /// taskbar. Callers must have a fallback rather than assuming a position.
    /// </remarks>
    public static Rect? GetTrayIconRect(nint windowHandle, uint iconId)
    {
        var identifier = new NotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            hWnd = windowHandle,
            uID = iconId,
        };

        return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 ? rect : null;
    }

    /// <summary>
    /// The screen rectangle of a tray icon registered with a GUID identity.
    /// </summary>
    /// <remarks>
    /// The identifier is a union: an icon registered by GUID has to be looked up by GUID, with the
    /// window and id fields left zero.
    /// </remarks>
    public static Rect? GetTrayIconRect(Guid iconGuid)
    {
        var identifier = new NotifyIconIdentifier
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            guidItem = iconGuid,
        };

        return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 ? rect : null;
    }

    // ---- shell theme --------------------------------------------------------------------------------

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// True when the shell is using the dark theme.
    /// </summary>
    /// <remarks>
    /// Reads <c>SystemUsesLightTheme</c> rather than <c>AppsUseLightTheme</c>: the tray icon lives
    /// in the taskbar, which follows the system setting, and those two can differ.
    /// </remarks>
    public static bool IsShellDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true; // dark is the safer default: a dark icon on a dark taskbar disappears
        }
    }

    /// <summary>True when apps should render dark, which is what the flyout follows.</summary>
    public static bool IsAppDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    // ---- metrics ------------------------------------------------------------------------------------

    private const int SmCxSmIcon = 49;

    /// <summary>The shell's small-icon width in physical pixels, which is the tray icon size.</summary>
    public static int SmallIconSize()
    {
        var size = GetSystemMetrics(SmCxSmIcon);
        return size > 0 ? size : 16;
    }

    /// <summary>
    /// Rounds a raw metric to a size the renderer draws cleanly at.
    /// </summary>
    /// <remarks>
    /// Snapping to the standard steps keeps glyph positioning on whole pixels. An arbitrary size
    /// like 22 produces visibly soft text at the sizes a tray icon uses.
    /// </remarks>
    public static int SnapIconSize(int raw) => raw switch
    {
        <= 16 => 16,
        <= 20 => 20,
        <= 24 => 24,
        <= 32 => 32,
        _ => 48,
    };

    // ---- monitors and cursor -----------------------------------------------------------------------

    /// <summary>One display, in physical pixels.</summary>
    public readonly record struct MonitorInfo(Rect Bounds, Rect WorkArea, bool IsPrimary);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// Every attached display with its work area.
    /// </summary>
    /// <remarks>
    /// WPF exposes only the primary screen's work area through <c>SystemParameters</c>, which is
    /// no use for a window the user drags onto a second monitor. Enumerating directly avoids a
    /// WinForms reference for the sake of one struct.
    /// </remarks>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        return monitors;

        bool Callback(nint monitor, nint _, nint __, nint ___)
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };

            if (GetMonitorInfoW(monitor, ref info))
                monitors.Add(new MonitorInfo(info.rcMonitor, info.rcWork, (info.dwFlags & 1) != 0));

            return true;
        }
    }

    /// <summary>The cursor position in physical pixels, or null if the call fails.</summary>
    public static System.Windows.Point? GetCursorPosition()
        => GetCursorPos(out var point) ? new System.Windows.Point(point.X, point.Y) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out PointL point);

    // ---- power ---------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    /// <summary>
    /// True when Windows has energy saver engaged.
    /// </summary>
    /// <remarks>
    /// Reads <c>SystemStatusFlag</c>, which is the actual battery-saver bit, rather than inferring
    /// it from charge level. A user on 15% with the charger in is not saving power, and a user who
    /// turned saver on manually at 90% is.
    /// </remarks>
    public static bool IsBatterySaverOn()
    {
        return GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
    }

    /// <summary>True when running on battery rather than mains.</summary>
    public static bool IsOnBattery()
        => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
}

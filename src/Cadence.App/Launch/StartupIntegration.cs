using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Cadence.App.Launch;

/// <summary>
/// Where Cadence plugs into Windows: starting at sign-in, and an entry in the Start menu.
/// </summary>
/// <remarks>
/// Both are per-user, the HKCU Run key and the per-user Programs folder, so neither ever needs
/// elevation and neither touches anyone else signed in to the machine.
/// <para>
/// An installed copy gets its Start menu entry from Setup and keeps one path across updates, so only
/// a portable exe creates the shortcut itself, and only when the user says so on the welcome screen.
/// Both entries record the exe's path when they were written: a portable exe moved afterwards leaves
/// them pointing at the old location. Switching start at sign-in off and on in Settings repairs it,
/// and installing moves it to the installed copy automatically.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Cadence";

    /// <summary>The per-user Start menu entry, under the user's own Programs folder.</summary>
    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Cadence.lnk");

    public static bool IsLaunchAtSignInEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The executable the sign-in entry starts, or null when there is none.</summary>
    public static string? LaunchAtSignInTarget()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return ParseRunCommand(key?.GetValue(RunValueName) as string);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The executable in a Run-key command line, which may be quoted and carry arguments.</summary>
    public static string? ParseRunCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var text = command.Trim();
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? text : text[..(exe + 4)];
    }

    /// <summary>Whether two paths name the same file, compared the way Windows compares them.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the Run-key entry for the running executable.</summary>
    public static void SetLaunchAtSignIn(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return;
            }

            if (Environment.ProcessPath is { Length: > 0 } exe) key.SetValue(RunValueName, $"\"{exe}\"");
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down machine may forbid this; the setting simply will not stick.
        }
    }

    public static bool HasStartMenuShortcut() => File.Exists(StartMenuShortcutPath);

    /// <summary>
    /// Creates or refreshes the Start menu entry for the running executable.
    /// </summary>
    /// <returns>
    /// False when the shell refused. Reported rather than thrown: a missing shortcut is not worth
    /// failing a first launch over.
    /// </returns>
    public static bool EnsureStartMenuShortcut()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return false;

        try
        {
            CreateShortcut(StartMenuShortcutPath, exe, "AI quota monitor and forecaster");
            return true;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes a shell link at <paramref name="shortcutPath"/> pointing at <paramref name="targetPath"/>.</summary>
    /// <remarks>
    /// Goes through the shell's own IShellLink rather than hand-writing the .lnk format, and rather
    /// than shelling out to a script host — a tray app spawning scripts to write into the Start menu
    /// is exactly the behaviour antivirus software flags.
    /// </remarks>
    public static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shortcutPath))!);

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)link).Save(shortcutPath, fRemember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>
    /// IShellLinkW. The whole vtable is declared because COM dispatches by slot order, but only the
    /// setters are ever called, so the getters take raw pointers rather than marshalled buffers.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);

        void GetIDList(out nint ppidl);

        void SetIDList(nint pidl);

        void GetDescription(nint pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(nint pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(nint pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(nint pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(nint hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

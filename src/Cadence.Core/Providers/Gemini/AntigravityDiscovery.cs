using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Cadence.Core.Diagnostics;

namespace Cadence.Core.Providers.Gemini;

/// <summary>A running Antigravity language server and how to talk to it.</summary>
public sealed record AntigravityEndpoint(int ProcessId, int Port, string? CsrfToken)
{
    public Uri BaseUri => new($"https://127.0.0.1:{Port}");
}

/// <summary>
/// Finds the Antigravity language server on this machine.
/// </summary>
/// <remarks>
/// The macOS reference implementation shells out to <c>ps</c> and <c>lsof</c>. On Windows the
/// equivalents are WMI for the command line (which carries the CSRF token and sometimes the port)
/// and the IP Helper TCP table for the listening port, filtered to the owning PID.
/// </remarks>
public static partial class AntigravityDiscovery
{
    private static readonly string[] ProcessNameHints = ["language_server", "language server"];

    /// <summary>Marks the server as Antigravity's rather than another Codeium-derived product's.</summary>
    private const string AppDataMarker = "antigravity";

    /// <summary>Returns every Antigravity endpoint found, best candidate first.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<AntigravityEndpoint> Discover()
    {
        var endpoints = new List<AntigravityEndpoint>();

        foreach (var (pid, commandLine) in EnumerateCandidateProcesses())
        {
            if (!commandLine.Contains(AppDataMarker, StringComparison.OrdinalIgnoreCase)) continue;

            var csrf = CsrfTokenArg().Match(commandLine) is { Success: true } m ? m.Groups[1].Value : null;

            // The port is sometimes on the command line and sometimes only discoverable from the
            // TCP table, so try the cheap source first.
            var port = ExtensionPortArg().Match(commandLine) is { Success: true } p
                ? int.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;

            if (port <= 0) port = FindListeningPort(pid);
            if (port <= 0) continue;

            endpoints.Add(new AntigravityEndpoint(pid, port, csrf));
        }

        // An endpoint with a CSRF token is the app or IDE server; the agy CLI server needs none.
        // Prefer whichever we know most about.
        return [.. endpoints.OrderByDescending(e => e.CsrfToken is not null)];
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(int Pid, string CommandLine)> EnumerateCandidateProcesses()
    {
        // ManagementObjectSearcher is the only supported way to read another process's command
        // line without native PEB reads.
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server%'");

        ManagementObjectCollection results;
        try
        {
            results = searcher.Get();
        }
        catch (ManagementException)
        {
            // WMI can be disabled or throttled; treat as "nothing found" rather than failing.
            yield break;
        }

        foreach (var item in results)
        {
            using var process = (ManagementObject)item;

            var name = process["Name"] as string ?? string.Empty;
            if (!ProcessNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

            if (process["CommandLine"] is not string commandLine || commandLine.Length == 0) continue;
            if (process["ProcessId"] is not uint pid) continue;

            yield return ((int)pid, commandLine);
        }
    }

    /// <summary>
    /// The loopback TCP port this PID is listening on, or 0.
    /// </summary>
    /// <remarks>
    /// <see cref="IPGlobalProperties"/> does not expose owning PIDs, so this goes to
    /// <c>GetExtendedTcpTable</c> directly.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static int FindListeningPort(int pid)
    {
        foreach (var row in TcpTable.ListenersForProcess(pid))
        {
            if (IPAddress.IsLoopback(row.Address)) return row.Port;
        }

        return 0;
    }

    [GeneratedRegex(@"--csrf[_-]?token[=\s]+(\S+)", RegexOptions.IgnoreCase, 500)]
    private static partial Regex CsrfTokenArg();

    [GeneratedRegex(@"--extension[_-]?server[_-]?port[=\s]+(\d+)", RegexOptions.IgnoreCase, 500)]
    private static partial Regex ExtensionPortArg();
}

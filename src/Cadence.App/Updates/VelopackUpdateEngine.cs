using System.IO;
using Velopack;
using Velopack.Sources;

namespace Cadence.App.Updates;

/// <summary>
/// <see cref="IUpdateEngine"/> over Velopack, reading releases from the project's GitHub page.
/// </summary>
/// <remarks>
/// <see cref="FeedOverrideVariable"/> points a copy at another feed, a folder or a URL, which is how
/// an update is tested before anything is published. It is an environment variable rather than a
/// setting so a test cannot leave an installed copy pointed at a folder that later disappears.
/// </remarks>
public sealed class VelopackUpdateEngine : IUpdateEngine
{
    public const string ReleasesRepository = "https://github.com/FanZhu1998/Cadence";
    public const string FeedOverrideVariable = "CADENCE_UPDATE_FEED";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _found;

    public VelopackUpdateEngine()
    {
        try
        {
            var feed = Environment.GetEnvironmentVariable(FeedOverrideVariable);
            _manager = string.IsNullOrWhiteSpace(feed)
                ? new UpdateManager(new GithubSource(ReleasesRepository, null, false, null))
                : new UpdateManager(feed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Treated as not installed. Failing to find the updater must never stop Cadence starting.
            _manager = null;
        }
    }

    public bool IsInstalled => _manager?.IsInstalled is true;

    public string? CurrentVersion => IsInstalled ? _manager!.CurrentVersion?.ToString() : null;

    public string? PendingVersion => IsInstalled ? _manager!.UpdatePendingRestart?.Version?.ToString() : null;

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (!IsInstalled) return null;

        _found = await _manager!.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        return _found?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(CancellationToken ct)
    {
        if (!IsInstalled || _found is null) return;

        await _manager!.DownloadUpdatesAsync(_found, _ => { }, ct).ConfigureAwait(false);
    }

    public void InstallOnExit()
    {
        if (!IsInstalled || _manager!.UpdatePendingRestart is not { } pending) return;

        // Silent: Cadence is a tray app, and the whole update takes a couple of seconds.
        _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, []);
    }
}

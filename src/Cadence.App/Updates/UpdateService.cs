using Microsoft.Extensions.Logging;

namespace Cadence.App.Updates;

public enum UpdateState
{
    /// <summary>Not an installed copy, so it cannot update itself.</summary>
    Unavailable,
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded. Installs when Cadence next starts, or straight away on request.</summary>
    ReadyToInstall,
    Failed,
}

public sealed record UpdateStatus(UpdateState State, string? Version = null);

/// <summary>
/// Keeps an installed Cadence current: checks the release feed in the background, downloads what it
/// finds, and lets it install the next time Cadence starts.
/// </summary>
/// <remarks>
/// Installing on the next start rather than at once is deliberate. Cadence starts at sign-in, so an
/// update lands within a day without interrupting anything, and Settings offers an immediate restart
/// for anyone who wants it sooner. The first check waits a minute so it never competes with sign-in;
/// later ones run every six hours, well inside GitHub's limit for anonymous requests.
/// </remarks>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateEngine _engine;
    private readonly Func<bool> _checkAutomatically;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public UpdateService(IUpdateEngine engine, Func<bool> checkAutomatically, ILogger<UpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(checkAutomatically);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _checkAutomatically = checkAutomatically;
        _logger = logger;

        Status = !engine.IsInstalled ? new UpdateStatus(UpdateState.Unavailable)
            : engine.PendingVersion is { } pending ? new UpdateStatus(UpdateState.ReadyToInstall, pending)
            : new UpdateStatus(UpdateState.Idle);
    }

    public bool IsInstalled => _engine.IsInstalled;

    public string? CurrentVersion => _engine.CurrentVersion;

    public UpdateStatus Status { get; private set; }

    /// <summary>Raised whenever <see cref="Status"/> changes, usually on a background thread.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>Starts background checks. Does nothing for a copy that cannot update.</summary>
    public void Start() => Start(FirstCheckDelay, CheckInterval);

    public void Start(TimeSpan firstDelay, TimeSpan interval)
    {
        if (!_engine.IsInstalled || _loop is not null) return;

        _loop = RunAsync(firstDelay, interval, _stop.Token);
    }

    private async Task RunAsync(TimeSpan firstDelay, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(firstDelay, ct).ConfigureAwait(false);

            while (true)
            {
                // Read each time, so switching automatic checks off in Settings applies at once.
                if (_checkAutomatically()) await CheckNowAsync(ct).ConfigureAwait(false);
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Checks the feed and downloads a newer version if there is one. Safe to call from Settings
    /// while a background check runs: the second caller waits for the first and sees its result.
    /// </summary>
    public async Task<UpdateStatus> CheckNowAsync(CancellationToken ct = default)
    {
        if (!_engine.IsInstalled) return Status;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // One download waiting is enough. Anything newer is found after it installs.
            if (Status.State is UpdateState.ReadyToInstall) return Status;

            Publish(new UpdateStatus(UpdateState.Checking));

            var version = await _engine.CheckAsync(ct).ConfigureAwait(false);
            if (version is null)
            {
                Publish(new UpdateStatus(UpdateState.UpToDate, _engine.CurrentVersion));
                return Status;
            }

            Publish(new UpdateStatus(UpdateState.Downloading, version));
            await _engine.DownloadAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("Update {Version} downloaded; it installs when Cadence next starts", version);
            Publish(new UpdateStatus(UpdateState.ReadyToInstall, version));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Offline, rate limited, or the feed moved: worth a line in the log, never a dialog.
            _logger.LogWarning(e, "Update check failed");
            Publish(new UpdateStatus(UpdateState.Failed));
        }
        finally
        {
            _gate.Release();
        }

        return Status;
    }

    /// <summary>
    /// Installs the downloaded version now: hands it to the updater, then shuts Cadence down
    /// normally so the tray icon is removed rather than left behind as a ghost.
    /// </summary>
    /// <returns>False when nothing is waiting to install.</returns>
    public bool InstallNow(Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        if (Status.State is not UpdateState.ReadyToInstall) return false;

        _logger.LogInformation("Restarting to install update {Version}", Status.Version);
        _engine.InstallOnExit();
        shutdown();
        return true;
    }

    private void Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

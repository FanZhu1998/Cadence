using Cadence.Core.Model;
using Cadence.Core.Refresh;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cadence.App.Refresh;

/// <summary>
/// The timing loop: decides when each provider is polled next and reacts to machine state.
/// </summary>
/// <remarks>
/// One independent loop per provider, each with its own jitter, so three providers never fire the
/// same second. Power, session and network events feed straight in: a laptop that sleeps for the
/// weekend should not wake to a queue of missed polls, and a locked machine is not being used.
/// </remarks>
public sealed class RefreshScheduler : IDisposable
{
    private readonly RefreshCoordinator _coordinator;
    private readonly UsageStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger<RefreshScheduler> _logger;
    private readonly Random _jitter = new();

    private readonly Dictionary<ProviderId, CancellationTokenSource> _loops = [];
    private readonly Lock _gate = new();

    private CancellationTokenSource? _lifetime;
    private RefreshConditions _conditions = RefreshConditions.Default;
    private bool _disposed;

    public RefreshScheduler(
        RefreshCoordinator coordinator,
        UsageStore store,
        Func<AppSettings> settings,
        ILogger<RefreshScheduler> logger)
    {
        _coordinator = coordinator;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Current machine conditions, exposed for the diagnostics pane.</summary>
    public RefreshConditions Conditions => _conditions;

    public void Start()
    {
        _lifetime = new CancellationTokenSource();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        _conditions = _conditions with
        {
            NetworkAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(),
            OnBatterySaver = IsBatterySaverOn(),
        };

        foreach (var provider in _coordinator.EnabledProviders) StartLoop(provider.Id);
    }

    /// <summary>Tells the scheduler the flyout opened or closed, which changes the cadence.</summary>
    public void SetFlyoutOpen(bool open)
    {
        _conditions = _conditions with { FlyoutOpen = open };

        // Opening the panel is an explicit request to look at fresh numbers.
        if (open) _ = RefreshNowAsync();
    }

    /// <summary>Refreshes everything immediately, coalescing with anything already in flight.</summary>
    public Task RefreshNowAsync(CancellationToken ct = default)
        => _coordinator.RefreshAllAsync(ct);

    private void StartLoop(ProviderId provider)
    {
        lock (_gate)
        {
            if (_loops.ContainsKey(provider)) return;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
            _loops[provider] = cts;

            _ = RunLoopAsync(provider, cts.Token);
        }
    }

    private async Task RunLoopAsync(ProviderId provider, CancellationToken ct)
    {
        // A small stagger at startup so three providers do not all fire on the first tick.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_jitter.Next(0, 1500)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _coordinator.RefreshAsync(provider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Refresh loop for {Provider} threw", provider);
            }

            var delay = NextDelay(provider);

            if (delay is null)
            {
                // Suspended, offline, manual, or the circuit is open. Idle until something changes
                // rather than spinning on a short poll.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await Task.Delay(delay.Value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan? NextDelay(ProviderId provider)
    {
        var state = _store.Get(provider);
        var failures = _coordinator.FailureCount(provider);

        if (failures > 0)
            return CadencePolicy.BackoffFor(failures, state.Error, _jitter.NextDouble());

        return CadencePolicy.Next(_settings().Cadence, state, _conditions, DateTimeOffset.UtcNow, _jitter.NextDouble());
    }

    // ---- machine state ---------------------------------------------------------------------------

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _conditions = _conditions with { Suspended = true };
                _logger.LogInformation("Suspending refresh: machine going to sleep");
                break;

            case PowerModes.Resume:
                _conditions = _conditions with { Suspended = false, OnBatterySaver = IsBatterySaverOn() };
                _logger.LogInformation("Resuming refresh after wake");
                // Numbers are certainly stale after a sleep, and the reset may have passed.
                _ = RefreshNowAsync();
                break;

            case PowerModes.StatusChange:
                _conditions = _conditions with { OnBatterySaver = IsBatterySaverOn() };
                break;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
                _conditions = _conditions with { SessionLocked = true };
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
                _conditions = _conditions with { SessionLocked = false };
                _ = RefreshNowAsync();
                break;
        }
    }

    private void OnNetworkChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        _conditions = _conditions with { NetworkAvailable = e.IsAvailable };

        // Coming back online is worth an immediate poll; going offline just stops the burn of
        // retries that would all fail.
        if (e.IsAvailable) _ = RefreshNowAsync();
    }

    private static bool IsBatterySaverOn()
    {
        try
        {
            return Interop.Win32.IsBatterySaverOn();
        }
        catch (Exception e) when (e is PlatformNotSupportedException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;

        _lifetime?.Cancel();

        lock (_gate)
        {
            foreach (var cts in _loops.Values) cts.Dispose();
            _loops.Clear();
        }

        _lifetime?.Dispose();
    }
}

using Cadence.Core.Forecast;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Providers;
using Microsoft.Extensions.Logging;

namespace Cadence.Core.Refresh;

/// <summary>
/// Drives fetches, stores results, and attaches forecasts.
/// </summary>
/// <remarks>
/// The one place that calls providers. Concurrent requests for the same provider are coalesced
/// onto a single in-flight task, so a user hammering Refresh while the adaptive timer also fires
/// produces one HTTP request, not five.
/// </remarks>
public sealed class RefreshCoordinator(
    IReadOnlyList<IUsageProvider> providers,
    UsageStore store,
    HistoryRepository history,
    HttpClient http,
    ILogger<RefreshCoordinator> logger,
    Func<AppSettings> settingsAccessor,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<ProviderId, Task<UsageSnapshot>> _inFlight = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<ProviderId, int> _consecutiveFailures = [];

    /// <summary>How long history to feed the forecaster. Long enough to learn a weekly rhythm.</summary>
    private static readonly TimeSpan ForecastLookback = TimeSpan.FromDays(35);

    public IReadOnlyList<IUsageProvider> Providers => providers;

    /// <summary>Providers the user has enabled.</summary>
    public IEnumerable<IUsageProvider> EnabledProviders
    {
        get
        {
            var settings = settingsAccessor();
            return providers.Where(p => settings.For(p.Id).Enabled);
        }
    }

    /// <summary>Refreshes every enabled provider concurrently.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var tasks = EnabledProviders.Select(p => RefreshAsync(p.Id, ct)).ToArray();
        if (tasks.Length == 0) return;

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one provider. Callers arriving while a fetch is in flight join that fetch.
    /// </summary>
    public Task<UsageSnapshot> RefreshAsync(ProviderId id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(id, out var existing)) return existing;

            var task = RunAsync(id, ct);
            _inFlight[id] = task;
            return task;
        }
    }

    private async Task<UsageSnapshot> RunAsync(ProviderId id, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Id == id)
                       ?? throw new ArgumentException($"No provider registered for {id}.", nameof(id));

        var now = _time.GetUtcNow();
        store.SetRefreshing(id, true);

        try
        {
            var settings = settingsAccessor();
            var context = new FetchContext(http, now, settings.For(id), logger);

            var snapshot = await provider.FetchAsync(context, ct).ConfigureAwait(false);

            if (snapshot.Error is { } error)
            {
                lock (_gate) _consecutiveFailures[id] = _consecutiveFailures.GetValueOrDefault(id) + 1;

                logger.LogWarning("Refresh of {Provider} failed: {Kind} {Message}", id, error.Kind, error.Message);
                store.RecordFailure(id, error, now);
                return snapshot;
            }

            lock (_gate) _consecutiveFailures[id] = 0;

            await history.AppendAsync(snapshot, ct).ConfigureAwait(false);

            var withForecasts = await AttachForecastsAsync(snapshot, settings, now, ct).ConfigureAwait(false);
            store.RecordSuccess(withForecasts);

            return withForecasts;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            store.SetRefreshing(id, false);
            throw;
        }
        catch (Exception e)
        {
            // The loop must survive anything a provider can do.
            logger.LogError(e, "Unhandled error refreshing {Provider}", id);

            var error = new FetchError(FetchErrorKind.Unknown, "Something went wrong refreshing this provider.", e.Message);
            store.RecordFailure(id, error, now);

            return UsageSnapshot.Failed(id, "unknown", error, now);
        }
        finally
        {
            lock (_gate) _inFlight.Remove(id);
        }
    }

    /// <summary>Runs the forecaster over each window using stored history.</summary>
    public async Task<UsageSnapshot> AttachForecastsAsync(
        UsageSnapshot snapshot, AppSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        if (!settings.Forecast.Enabled || snapshot.Windows.Count == 0) return snapshot;

        var engine = new ForecastEngine(new ForecastOptions
        {
            Mode = settings.Forecast.UseBlendedEstimator ? EstimatorMode.Blended : EstimatorMode.EvenPaceOnly,
            UseIntensityProfile = settings.Forecast.UseIntensityProfile,
            WorkHoursPerDayOverride = settings.Forecast.WorkHoursPerDay,
        });

        var since = now - ForecastLookback;
        var updated = new List<QuotaWindow>(snapshot.Windows.Count);

        foreach (var window in snapshot.Windows)
        {
            if (!window.UsageKnown)
            {
                updated.Add(window);
                continue;
            }

            try
            {
                var windowHistory = await history.ReadHistoryAsync(snapshot.Provider, window, since, ct)
                    .ConfigureAwait(false);

                updated.Add(window with { Forecast = engine.Project(windowHistory, now) });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A forecast is an enhancement; losing one must not lose the usage number.
                logger.LogWarning(e, "Forecast failed for {Window}", window.Id);
                updated.Add(window);
            }
        }

        return snapshot with { Windows = updated };
    }

    /// <summary>Consecutive failures for a provider, used to size backoff.</summary>
    public int FailureCount(ProviderId id)
    {
        lock (_gate) return _consecutiveFailures.GetValueOrDefault(id);
    }
}

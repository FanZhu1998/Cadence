using Cadence.Core.Model;

namespace Cadence.Core.Refresh;

/// <summary>One provider's current state, as the UI sees it.</summary>
public sealed record ProviderState
{
    public required ProviderId Provider { get; init; }

    /// <summary>The most recent snapshot carrying real numbers, even if it is now stale.</summary>
    public UsageSnapshot? Snapshot { get; init; }

    /// <summary>Set when the latest attempt failed. <see cref="Snapshot"/> may still hold good data.</summary>
    public FetchError? Error { get; init; }

    /// <summary>When the last <em>successful</em> fetch landed.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public bool IsRefreshing { get; init; }

    /// <summary>Consecutive auth failures; three opens the circuit breaker.</summary>
    public int ConsecutiveAuthFailures { get; init; }

    /// <summary>Set while the breaker is open, meaning polling has stopped pending user action.</summary>
    public bool CircuitOpen { get; init; }

    public bool HasData => Snapshot is { Windows.Count: > 0 };

    /// <summary>How old the displayed numbers are.</summary>
    public TimeSpan? Age(DateTimeOffset now) => LastSuccessAt is { } at ? now - at : null;

    /// <summary>True when the numbers are real but no longer fresh, so the UI should dim them.</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan threshold)
        => HasData && Age(now) is { } age && age > threshold;
}

/// <summary>
/// The single observable source of truth the UI binds to.
/// </summary>
/// <remarks>
/// Nothing downstream of this ever calls a provider. The refresh loop pushes in, the tray icon,
/// flyout, HUD and notifier read out, which is what keeps the data flow one-directional and makes
/// "why did the number change" answerable.
/// <para>
/// A failed fetch never clears good data: <see cref="RecordFailure"/> keeps the previous snapshot
/// and records the error alongside it.
/// </para>
/// </remarks>
public sealed class UsageStore
{
    private readonly Dictionary<ProviderId, ProviderState> _states = [];
    private readonly Lock _gate = new();

    /// <summary>Raised after any state change, on the caller's thread.</summary>
    public event Action<ProviderId, ProviderState>? Changed;

    public ProviderState Get(ProviderId provider)
    {
        lock (_gate)
        {
            return _states.TryGetValue(provider, out var state)
                ? state
                : new ProviderState { Provider = provider };
        }
    }

    public IReadOnlyList<ProviderState> All()
    {
        lock (_gate)
        {
            return [.. _states.Values];
        }
    }

    public void SetRefreshing(ProviderId provider, bool refreshing)
        => Update(provider, state => state with { IsRefreshing = refreshing });

    public void RecordSuccess(UsageSnapshot snapshot)
        => Update(snapshot.Provider, _ => new ProviderState
        {
            Provider = snapshot.Provider,
            Snapshot = snapshot,
            Error = null,
            LastSuccessAt = snapshot.FetchedAt,
            LastAttemptAt = snapshot.FetchedAt,
            IsRefreshing = false,
            ConsecutiveAuthFailures = 0,
            CircuitOpen = false,
        });

    /// <summary>
    /// Records a failure while preserving the last good snapshot.
    /// </summary>
    /// <remarks>
    /// Three consecutive auth failures open the circuit. Retrying a 401 in a loop achieves nothing
    /// except making the provider's abuse detection notice us.
    /// </remarks>
    public void RecordFailure(ProviderId provider, FetchError error, DateTimeOffset at)
        => Update(provider, state =>
        {
            var authFailures = error.IsAuthFailure ? state.ConsecutiveAuthFailures + 1 : 0;

            return state with
            {
                Provider = provider,
                Error = error,
                LastAttemptAt = at,
                IsRefreshing = false,
                ConsecutiveAuthFailures = authFailures,
                CircuitOpen = authFailures >= 3,
                Snapshot = state.Snapshot?.AsStale(error),
            };
        });

    /// <summary>Closes the breaker after the user has acted, e.g. pasted a new token.</summary>
    public void ResetCircuit(ProviderId provider)
        => Update(provider, state => state with { CircuitOpen = false, ConsecutiveAuthFailures = 0 });

    /// <summary>Replaces the windows of the current snapshot, used to attach forecasts.</summary>
    public void UpdateWindows(ProviderId provider, IReadOnlyList<QuotaWindow> windows)
        => Update(provider, state => state.Snapshot is { } snapshot
            ? state with { Snapshot = snapshot with { Windows = windows } }
            : state);

    private void Update(ProviderId provider, Func<ProviderState, ProviderState> mutate)
    {
        ProviderState updated;

        lock (_gate)
        {
            var current = _states.TryGetValue(provider, out var existing)
                ? existing
                : new ProviderState { Provider = provider };

            updated = mutate(current);
            _states[provider] = updated;
        }

        Changed?.Invoke(provider, updated);
    }
}

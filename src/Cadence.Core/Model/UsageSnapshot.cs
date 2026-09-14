namespace Cadence.Core.Model;

/// <summary>
/// Everything one provider reported at one moment. Immutable; the store swaps whole snapshots.
/// </summary>
/// <remarks>
/// Invariant: a failed fetch never discards the last good snapshot. The refresh loop keeps the
/// previous payload and marks it stale via <see cref="AsStale"/>, so the UI keeps showing real
/// numbers with their age rather than blanking out.
/// </remarks>
public sealed record UsageSnapshot
{
    public required ProviderId Provider { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>Which strategy in the source chain produced this, e.g. "oauth", "cli", "antigravity".</summary>
    public required string SourceLabel { get; init; }

    public AccountIdentity? Identity { get; init; }

    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];

    public MoneySnapshot? Spend { get; init; }

    public ProviderStatus? Status { get; init; }

    /// <summary>Non-null means this snapshot is partial or stale; <see cref="Windows"/> may be empty.</summary>
    public FetchError? Error { get; init; }

    public bool IsHealthy => Error is null;

    /// <summary>
    /// The window the compact tray metric tracks: whichever is closest to exhaustion.
    /// </summary>
    /// <remarks>
    /// Per-model sub-windows are deliberately included. A model-scoped weekly limit really can be
    /// the binding constraint — real Claude accounts show one sitting well above the overall weekly
    /// figure — and excluding it would have the tray report headroom the user does not have. Double
    /// counting is not a risk here because this takes a maximum, not a sum.
    /// </remarks>
    public QuotaWindow? PrimaryWindow =>
        Windows.Where(w => w.UsageKnown)
               .OrderByDescending(w => w.UsedPercent)
               .FirstOrDefault();

    /// <summary>
    /// The current session: the short rolling window, typically five hours, or null when the
    /// provider has none.
    /// </summary>
    /// <remarks>
    /// Chosen by <see cref="WindowKind"/>, never by a provider's window id, so a new provider or a
    /// renamed window is picked up without touching any caller. Where a provider reports more than
    /// one session window, the one closest to exhaustion wins, for the same reason
    /// <see cref="PrimaryWindow"/> takes a maximum: the tightest one is the one that stops you.
    /// </remarks>
    public QuotaWindow? SessionWindow =>
        Windows.Where(w => w.Kind is WindowKind.Session)
               .OrderByDescending(w => w.UsedPercent ?? -1)
               .FirstOrDefault();

    public static UsageSnapshot Failed(ProviderId provider, string source, FetchError error, DateTimeOffset at) =>
        new() { Provider = provider, FetchedAt = at, SourceLabel = source, Error = error };

    /// <summary>Carry a previous good snapshot forward under a new error, preserving its numbers.</summary>
    public UsageSnapshot AsStale(FetchError error) => this with { Error = error };
}

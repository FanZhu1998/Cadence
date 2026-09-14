namespace Cadence.Core.Model;

/// <summary>
/// One quota window (Claude five_hour, Codex primary_window, an Antigravity bucket).
/// </summary>
/// <remarks>
/// Invariant: <see cref="UsedPercent"/> of <c>null</c> means <em>unknown</em>, never zero. Providers
/// do return windows carrying reset metadata but no utilisation (Claude education and enterprise
/// accounts, Antigravity rows with no remainingFraction). Rendering those as 0% is a lie that reads
/// to the user as "you have your full quota".
/// </remarks>
public sealed record QuotaWindow
{
    /// <summary>Stable id, e.g. "claude.five_hour". History rows and layout overrides key off this.</summary>
    public required string Id { get; init; }

    /// <summary>Human label, e.g. "Session (5h)".</summary>
    public required string Title { get; init; }

    public required WindowKind Kind { get; init; }

    /// <summary>0..100, or null when the provider did not report utilisation.</summary>
    public double? UsedPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Nominal window length (5h, 7d), used to infer the window start for pace maths.</summary>
    public TimeSpan? WindowLength { get; init; }

    /// <summary>
    /// The window's actual start, when the provider states it.
    /// </summary>
    /// <remarks>
    /// Preferred over inferring "reset minus nominal length". Rolling windows do not always start
    /// exactly one nominal length before they reset, and the elapsed fraction feeds directly into
    /// every forecast.
    /// </remarks>
    public DateTimeOffset? WindowStartsAt { get; init; }

    /// <summary>Provider-reported severity, where one is given. Drives the warning accent.</summary>
    public IncidentSeverity Severity { get; init; } = IncidentSeverity.None;

    public long? UsedUnits { get; init; }

    public long? LimitUnits { get; init; }

    /// <summary>Populated by the forecast engine after the snapshot is stored.</summary>
    public Forecast? Forecast { get; init; }

    /// <summary>Collapsed by default in the UI. For noisy per-model sub-windows.</summary>
    public bool SecondaryByDefault { get; init; }

    public bool UsageKnown => UsedPercent is not null;

    /// <summary>
    /// Start of the current window: what the provider reported, else reset minus nominal length.
    /// </summary>
    public DateTimeOffset? WindowStart =>
        WindowStartsAt ?? (ResetsAt is { } r && WindowLength is { } len ? r - len : null);

    /// <summary>
    /// Fraction of the window elapsed at <paramref name="now"/>, in [0,1], or null when the window
    /// bounds are unknown.
    /// </summary>
    public double? ElapsedFraction(DateTimeOffset now)
    {
        if (WindowStart is not { } start || ResetsAt is not { } reset) return null;
        var total = (reset - start).TotalSeconds;
        if (total <= 0) return null;
        return Math.Clamp((now - start).TotalSeconds / total, 0d, 1d);
    }

    public TimeSpan? TimeUntilReset(DateTimeOffset now) =>
        ResetsAt is { } r ? (r > now ? r - now : TimeSpan.Zero) : null;
}

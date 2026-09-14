namespace Cadence.Core.Model;

public enum ProviderId
{
    Claude,
    Codex,
    Gemini,
}

/// <summary>Classifies a quota window so the UI and the forecaster can treat it appropriately.</summary>
public enum WindowKind
{
    /// <summary>Short rolling window, typically five hours.</summary>
    Session,

    /// <summary>Seven-day rolling window.</summary>
    Weekly,

    /// <summary>Calendar-month window, usually a spend cap rather than a rate limit.</summary>
    Monthly,

    /// <summary>Per-model sub-limit that shares a parent window (Opus 7d, Sonnet 7d).</summary>
    Model,

    /// <summary>Anything provider-specific that does not fit above.</summary>
    Extra,
}

/// <summary>Why a fetch failed. Each value drives different UI copy and a different retry policy.</summary>
public enum FetchErrorKind
{
    /// <summary>No credential found on disk at all. Retrying will not help.</summary>
    NotLoggedIn,

    /// <summary>Credential found but expired and refresh failed. Retrying will not help.</summary>
    TokenExpired,

    /// <summary>Token authenticates but lacks a scope the usage endpoint needs (Claude: user:profile).</summary>
    ScopeMissing,

    /// <summary>429. Honour Retry-After exactly.</summary>
    RateLimited,

    /// <summary>200 with a body we could not map. The endpoint changed shape under us.</summary>
    EndpointChanged,

    /// <summary>The account's tier is no longer served by this path (Gemini consumer OAuth).</summary>
    TierDeprecated,

    /// <summary>A local helper process or loopback server is not running (Antigravity).</summary>
    NotRunning,

    /// <summary>Transport failure, DNS, offline. Retry with backoff.</summary>
    Network,

    /// <summary>5xx or anything else transient on the provider side.</summary>
    ProviderUnavailable,

    /// <summary>Unclassified.</summary>
    Unknown,
}

/// <summary>
/// A typed fetch failure. <see cref="IsTerminal"/> distinguishes "stop polling and ask the user
/// to act" from "back off and try again".
/// </summary>
public sealed record FetchError(
    FetchErrorKind Kind,
    string Message,
    string? Detail = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>True when no amount of retrying fixes this; the user has to do something.</summary>
    public bool IsTerminal => Kind is FetchErrorKind.NotLoggedIn
                                   or FetchErrorKind.TokenExpired
                                   or FetchErrorKind.ScopeMissing
                                   or FetchErrorKind.TierDeprecated;

    /// <summary>True when the circuit breaker should count this toward opening.</summary>
    public bool IsAuthFailure => Kind is FetchErrorKind.NotLoggedIn
                                      or FetchErrorKind.TokenExpired
                                      or FetchErrorKind.ScopeMissing;
}

public sealed record AccountIdentity(string? Email = null, string? PlanLabel = null, string? OrgName = null);

/// <summary>Spend figures, where a provider exposes them. All amounts in <see cref="Currency"/>.</summary>
public sealed record MoneySnapshot
{
    public decimal? SpentThisPeriod { get; init; }
    public decimal? PeriodLimit { get; init; }
    public decimal? CreditBalance { get; init; }
    public DateTimeOffset? CreditsExpireAt { get; init; }
    public string Currency { get; init; } = "USD";
}

public enum IncidentSeverity { None, Minor, Major, Critical }

public sealed record ProviderStatus(IncidentSeverity Severity, string? Description = null, Uri? Link = null)
{
    public static readonly ProviderStatus Operational = new(IncidentSeverity.None);
}

using Cadence.Core.Model;
using Microsoft.Extensions.Logging;

namespace Cadence.Core.Providers;

/// <summary>Outcome of one strategy attempt: a snapshot, or a typed reason it could not produce one.</summary>
public readonly record struct StrategyResult
{
    private StrategyResult(UsageSnapshot? snapshot, FetchError? error)
    {
        Snapshot = snapshot;
        Error = error;
    }

    public UsageSnapshot? Snapshot { get; }

    public FetchError? Error { get; }

    public bool Succeeded => Snapshot is not null;

    public static StrategyResult Ok(UsageSnapshot snapshot) => new(snapshot, null);

    public static StrategyResult Fail(FetchError error) => new(null, error);

    public static StrategyResult Fail(FetchErrorKind kind, string message, string? detail = null)
        => new(null, new FetchError(kind, message, detail));
}

/// <summary>Everything a strategy needs to run one fetch.</summary>
public sealed record FetchContext(
    HttpClient Http,
    DateTimeOffset Now,
    ProviderSettings Settings,
    ILogger Logger,
    IDiagnosticsSink? Diagnostics = null);

/// <summary>
/// Receives redacted request/response detail for the Settings "Test connection" pane.
/// </summary>
/// <remarks>
/// When a private endpoint changes shape, this is what turns an hour of guessing into ten seconds
/// of reading. It is only attached during an explicit connection test, never during background
/// polling.
/// </remarks>
public interface IDiagnosticsSink
{
    void Step(string strategy, string message);

    void Response(string strategy, int statusCode, string redactedBodyPrefix);
}

/// <summary>One way of getting usage out of a provider, e.g. OAuth, a CLI probe, a pasted token.</summary>
public interface IUsageStrategy
{
    /// <summary>Stable identifier, surfaced as <see cref="UsageSnapshot.SourceLabel"/>.</summary>
    string Label { get; }

    /// <summary>Human-readable name for the source picker.</summary>
    string DisplayName { get; }

    /// <summary>Cheap check for whether this strategy has anything to work with.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct);
}

/// <summary>A credential source discovered on this machine, for the settings UI.</summary>
public sealed record CredentialSource(
    string StrategyLabel,
    string DisplayName,
    bool IsAvailable,
    string? Location = null,
    string? Detail = null);

/// <summary>Static description of a provider: naming, colours, and window labels.</summary>
public sealed record ProviderDescriptor
{
    public required ProviderId Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Short label for the tray icon and compact rows.</summary>
    public required string ShortName { get; init; }

    /// <summary>Accent colour as #RRGGBB, used only for the provider glyph.</summary>
    public required string AccentHex { get; init; }

    /// <summary>Ordered strategy labels, describing the default fallback chain.</summary>
    public IReadOnlyList<string> SourceChain { get; init; } = [];
}

public interface IUsageProvider
{
    ProviderId Id { get; }

    ProviderDescriptor Descriptor { get; }

    /// <summary>Walks the source chain and returns the first success, or the most useful failure.</summary>
    Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct);

    /// <summary>Enumerates what this machine offers, for the settings source picker.</summary>
    Task<IReadOnlyList<CredentialSource>> DiscoverSourcesAsync(CancellationToken ct);
}

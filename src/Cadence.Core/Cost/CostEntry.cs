using Cadence.Core.Model;

namespace Cadence.Core.Cost;

/// <summary>
/// One billable exchange, normalised across providers.
/// </summary>
/// <remarks>
/// Normalisation matters because the two providers use opposite conventions, verified against real
/// transcripts on 2026-09-13:
/// <list type="bullet">
/// <item>Claude's fields are <em>additive</em>: <c>input_tokens</c> excludes cache reads and cache
/// creation, so total input is the sum of all three.</item>
/// <item>Codex's are <em>inclusive</em>: <c>cached_input_tokens</c> is a subset of
/// <c>input_tokens</c>, and <c>reasoning_output_tokens</c> a subset of <c>output_tokens</c>.</item>
/// </list>
/// Every field on this record is exclusive of every other, so summing them is always correct.
/// Getting Codex's convention backwards overstates a cache-heavy session roughly threefold.
/// </remarks>
public sealed record CostEntry
{
    public required ProviderId Provider { get; init; }

    /// <summary>
    /// The provider's own idempotency key: <c>message.id|requestId</c> for Claude,
    /// <c>response_id</c> for Codex. Streaming writes the same exchange many times.
    /// </summary>
    public required string EntryId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Model { get; init; }

    /// <summary>Fresh input tokens, excluding anything read from or written to cache.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens, including any reasoning tokens (which bill as output).</summary>
    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>Cache writes with the 5-minute TTL, which bill lower than the 1-hour ones.</summary>
    public long CacheWrite5mTokens { get; init; }

    /// <summary>Cache writes with the 1-hour TTL.</summary>
    public long CacheWrite1hTokens { get; init; }

    /// <summary>Reasoning tokens, reported separately for display. Already inside <see cref="OutputTokens"/>.</summary>
    public long ReasoningTokens { get; init; }

    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWrite5mTokens + CacheWrite1hTokens;
}

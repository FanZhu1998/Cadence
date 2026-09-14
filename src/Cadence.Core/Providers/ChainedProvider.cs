using Cadence.Core.Model;
using Microsoft.Extensions.Logging;

namespace Cadence.Core.Providers;

/// <summary>
/// Base provider that walks an ordered chain of strategies.
/// </summary>
/// <remarks>
/// The chain is data, not control flow: adding a fallback is appending to a list, and the settings
/// source picker is generated from the same list. When the user pins one strategy explicitly,
/// fallback is disabled, because silently succeeding through a different route would hide from
/// them that the one they chose is broken.
/// </remarks>
public abstract class ChainedProvider(IReadOnlyList<IUsageStrategy> chain) : IUsageProvider
{
    protected IReadOnlyList<IUsageStrategy> Chain { get; } = chain;

    public abstract ProviderId Id { get; }

    public abstract ProviderDescriptor Descriptor { get; }

    public async Task<UsageSnapshot> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var pinned = ctx.Settings.PinnedStrategy;
        var candidates = pinned is { Length: > 0 }
            ? Chain.Where(s => s.Label == pinned).ToArray()
            : [.. Chain];

        if (candidates.Length == 0)
        {
            return UsageSnapshot.Failed(Id, pinned ?? "auto",
                new FetchError(FetchErrorKind.Unknown, $"No strategy named '{pinned}' is available."), ctx.Now);
        }

        FetchError? best = null;
        var bestLabel = candidates[0].Label;

        foreach (var strategy in candidates)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Diagnostics?.Step(strategy.Label, "attempting");

            StrategyResult result;
            try
            {
                if (!await strategy.IsAvailableAsync(ct).ConfigureAwait(false))
                {
                    ctx.Diagnostics?.Step(strategy.Label, "unavailable, skipping");
                    best ??= new FetchError(FetchErrorKind.NotLoggedIn, $"{strategy.DisplayName} is not set up on this machine.");
                    bestLabel = strategy.Label;
                    continue;
                }

                result = await strategy.FetchAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // A strategy throwing must never take down the refresh loop; the next one still
                // gets its turn and the user still sees their last good numbers.
                ctx.Logger.LogWarning(e, "Strategy {Strategy} for {Provider} threw", strategy.Label, Id);
                result = StrategyResult.Fail(FetchErrorKind.Unknown, $"{strategy.DisplayName} failed unexpectedly.", e.Message);
            }

            if (result.Succeeded)
            {
                ctx.Diagnostics?.Step(strategy.Label, "succeeded");
                return result.Snapshot!;
            }

            ctx.Diagnostics?.Step(strategy.Label, $"failed: {result.Error!.Kind} - {result.Error.Message}");

            if (ShouldReplaceBestError(best, result.Error!))
            {
                best = result.Error;
                bestLabel = strategy.Label;
            }

            // A terminal failure on a pinned strategy stops here: falling through would report a
            // different source's problem and confuse the fix.
            if (pinned is { Length: > 0 }) break;
        }

        return UsageSnapshot.Failed(Id, bestLabel,
            best ?? new FetchError(FetchErrorKind.Unknown, "No source produced usage data."), ctx.Now);
    }

    /// <summary>
    /// Picks the failure most worth showing. An actionable one ("re-authenticate") beats a vague
    /// one ("not set up"), because it tells the user what to actually do.
    /// </summary>
    private static bool ShouldReplaceBestError(FetchError? current, FetchError candidate)
    {
        if (current is null) return true;
        return Rank(candidate) > Rank(current);

        static int Rank(FetchError e) => e.Kind switch
        {
            FetchErrorKind.ScopeMissing => 100,
            FetchErrorKind.TokenExpired => 90,
            FetchErrorKind.TierDeprecated => 85,
            FetchErrorKind.RateLimited => 80,
            FetchErrorKind.EndpointChanged => 70,
            FetchErrorKind.ProviderUnavailable => 60,
            FetchErrorKind.Network => 50,
            FetchErrorKind.NotRunning => 40,
            FetchErrorKind.NotLoggedIn => 30,
            _ => 10,
        };
    }

    public async Task<IReadOnlyList<CredentialSource>> DiscoverSourcesAsync(CancellationToken ct)
    {
        var sources = new List<CredentialSource>(Chain.Count);

        foreach (var strategy in Chain)
        {
            bool available;
            try
            {
                available = await strategy.IsAvailableAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                available = false;
            }

            sources.Add(new CredentialSource(
                strategy.Label, strategy.DisplayName, available, DescribeLocation(strategy)));
        }

        return sources;
    }

    /// <summary>Where a strategy reads from, shown in the settings pane. Null when not file-backed.</summary>
    protected virtual string? DescribeLocation(IUsageStrategy strategy) => null;
}

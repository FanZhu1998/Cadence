using Cadence.Core.Model;

namespace Cadence.Core.Refresh;

/// <summary>Machine conditions that should slow or stop polling.</summary>
public sealed record RefreshConditions
{
    public bool FlyoutOpen { get; init; }

    public bool SessionLocked { get; init; }

    public bool OnBatterySaver { get; init; }

    public bool Suspended { get; init; }

    public bool NetworkAvailable { get; init; } = true;

    public static readonly RefreshConditions Default = new();
}

/// <summary>
/// Decides how long to wait before the next poll.
/// </summary>
/// <remarks>
/// These are undocumented endpoints on infrastructure that belongs to someone else. Polling hard
/// is how a tool gets blocked for all of its users, so the floor of one minute is absolute and
/// every multiplier below can only ever slow things down from a five-minute base.
/// </remarks>
public static class CadencePolicy
{
    public static readonly TimeSpan BaseInterval = TimeSpan.FromMinutes(5);

    /// <summary>Never poll faster than this, whatever the state of the windows.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(30);

    /// <summary>Below this much movement over the look-back, treat the account as idle.</summary>
    private const double IdleMovementPercent = 0.5;

    private static readonly TimeSpan IdleLookback = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The interval before the next poll, or null to stop polling entirely.
    /// </summary>
    /// <param name="jitter">
    /// A value in [0,1) used to spread providers apart. Passing a fixed value makes this
    /// deterministic for tests.
    /// </param>
    public static TimeSpan? Next(
        RefreshCadence cadence,
        ProviderState state,
        RefreshConditions conditions,
        DateTimeOffset now,
        double jitter)
    {
        if (cadence is RefreshCadence.Manual) return null;
        if (conditions.Suspended || !conditions.NetworkAvailable) return null;

        // An open breaker means we are waiting on the user, not on time.
        if (state.CircuitOpen) return null;

        if (cadence is not RefreshCadence.Adaptive)
        {
            var fixedInterval = cadence switch
            {
                RefreshCadence.Fixed1 => TimeSpan.FromMinutes(1),
                RefreshCadence.Fixed2 => TimeSpan.FromMinutes(2),
                RefreshCadence.Fixed5 => TimeSpan.FromMinutes(5),
                RefreshCadence.Fixed15 => TimeSpan.FromMinutes(15),
                RefreshCadence.Fixed30 => TimeSpan.FromMinutes(30),
                _ => BaseInterval,
            };

            return ApplyJitter(Clamp(fixedInterval), jitter);
        }

        var multiplier = 1.0;

        // Speed up when a window is nearly spent and about to reset: this is when the number
        // actually matters to a decision.
        if (state.Snapshot is { } snapshot && IsNearingExhaustionAndReset(snapshot, now))
            multiplier *= 0.4;

        // The flyout being open means someone is looking at it.
        if (conditions.FlyoutOpen) multiplier *= 0.5;

        // Nothing has moved in a while, so nothing is likely to move in the next few minutes.
        if (state.Snapshot is { } current && !HasMovedRecently(current, now))
            multiplier *= 3.0;

        if (conditions.SessionLocked || conditions.OnBatterySaver) multiplier *= 6.0;

        return ApplyJitter(Clamp(BaseInterval * multiplier), jitter);
    }

    /// <summary>Backoff after a failure: exponential with a ceiling, honouring Retry-After exactly.</summary>
    public static TimeSpan BackoffFor(int consecutiveFailures, FetchError? error, double jitter)
    {
        // A provider telling us when to come back is not a suggestion.
        if (error?.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
            return retryAfter;

        var exponent = Math.Min(consecutiveFailures, 5);
        var backoff = TimeSpan.FromMinutes(Math.Pow(2, exponent));

        return ApplyJitter(Clamp(backoff, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(32)), jitter);
    }

    private static bool IsNearingExhaustionAndReset(UsageSnapshot snapshot, DateTimeOffset now)
        => snapshot.Windows.Any(w =>
            w.UsedPercent > 90 &&
            w.TimeUntilReset(now) is { } remaining &&
            remaining < TimeSpan.FromMinutes(60));

    private static bool HasMovedRecently(UsageSnapshot snapshot, DateTimeOffset now)
    {
        // Without history to compare against, assume active rather than backing off on a guess.
        if (snapshot.FetchedAt < now - IdleLookback) return true;
        return snapshot.Windows.Any(w => w.UsedPercent is > IdleMovementPercent);
    }

    private static TimeSpan Clamp(TimeSpan value) => Clamp(value, Floor, Ceiling);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan floor, TimeSpan ceiling)
        => value < floor ? floor : value > ceiling ? ceiling : value;

    /// <summary>
    /// Spreads providers by up to ±10%, so three of them do not stampede the same second forever.
    /// </summary>
    private static TimeSpan ApplyJitter(TimeSpan interval, double jitter)
    {
        var factor = 0.9 + (Math.Clamp(jitter, 0, 0.999) * 0.2);
        var jittered = interval * factor;

        // Jitter must never take us below the hard floor.
        return jittered < Floor ? Floor : jittered;
    }
}

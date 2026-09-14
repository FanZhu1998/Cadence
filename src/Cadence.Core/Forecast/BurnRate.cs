using Cadence.Core.Model;

namespace Cadence.Core.Forecast;

/// <summary>A burn-rate estimate in percent per hour, plus how it was obtained.</summary>
public readonly record struct RateEstimate(double PercentPerHour, string Method, int SampleCount)
{
    public bool IsUsable => double.IsFinite(PercentPerHour) && PercentPerHour >= 0;

    public static readonly RateEstimate None = new(0d, "none", 0);
}

/// <summary>
/// The estimator bank (E1-E4 in the design) and the credibility weighting that combines them.
/// </summary>
public static class BurnRate
{
    /// <summary>
    /// Increments longer than this are not evidence about the <em>active</em> rate: we cannot tell
    /// how much of the gap was working time, so E3 excludes them rather than guessing.
    /// </summary>
    public static readonly TimeSpan WellSampledInterval = TimeSpan.FromMinutes(30);

    /// <summary>Below this delta an interval counts as idle for active-time purposes.</summary>
    public const double ActivityThresholdPercent = 0.01;

    /// <summary>
    /// Credibility constant k in theta = n / (n + k). At n = 8 increments the responsive
    /// estimators carry half the weight; below that the robust even-pace baseline dominates.
    /// </summary>
    public const double CredibilityK = 8.0;

    /// <summary>E1 - even pace. Total usage over total elapsed time. Maximally robust.</summary>
    public static RateEstimate EvenPace(double usedPercent, TimeSpan elapsed)
    {
        var hours = elapsed.TotalHours;
        if (hours <= 1e-6) return RateEstimate.None;
        return new RateEstimate(Math.Max(0, usedPercent) / hours, "even-pace", 1);
    }

    /// <summary>
    /// E2 - time-aware EWMA of interval rates.
    /// </summary>
    /// <remarks>
    /// The decay is computed from elapsed time, not from sample index, so an irregular polling
    /// cadence (which adaptive refresh guarantees) does not distort the weighting.
    /// </remarks>
    public static RateEstimate Ewma(IReadOnlyList<Increment> increments, TimeSpan halfLife)
    {
        if (increments.Count == 0) return RateEstimate.None;

        // alpha = 1 - exp(-dt/tau), with tau the time constant implied by the half-life.
        var tau = halfLife.TotalHours / Math.Log(2);
        if (tau <= 0) return RateEstimate.None;

        double? estimate = null;
        foreach (var inc in increments)
        {
            if (inc.Hours <= 1e-9) continue;
            var alpha = 1 - Math.Exp(-inc.Hours / tau);
            estimate = estimate is { } previous
                ? (alpha * inc.RatePerHour) + ((1 - alpha) * previous)
                : inc.RatePerHour;
        }

        return estimate is { } value
            ? new RateEstimate(value, "ewma", increments.Count)
            : RateEstimate.None;
    }

    /// <summary>
    /// E3 - rate per <em>active</em> hour.
    /// </summary>
    /// <remarks>
    /// A weekly window that sat idle overnight would otherwise report a near-zero rate at 9am and
    /// forecast that the user is fine. This measures how fast quota burns while actually working,
    /// counting only intervals short enough to attribute confidently.
    /// </remarks>
    public static RateEstimate ActiveRate(IReadOnlyList<Increment> increments)
    {
        double usage = 0, activeHours = 0;
        var counted = 0;

        foreach (var inc in increments)
        {
            if (inc.Duration > WellSampledInterval) continue;
            if (inc.DeltaUsed <= ActivityThresholdPercent) continue;

            usage += inc.DeltaUsed;
            activeHours += inc.Hours;
            counted++;
        }

        if (counted == 0 || activeHours <= 1e-6) return RateEstimate.None;
        return new RateEstimate(usage / activeHours, "active-rate", counted);
    }

    /// <summary>
    /// Share of elapsed time the user was actually working, in [0.01, 1].
    /// </summary>
    /// <remarks>
    /// This is what makes <see cref="ActiveRate"/> safe to use. An active rate is percent per
    /// <em>active</em> hour, so projecting it over wall-clock hours assumes the user never stops
    /// and roughly triples a normal workday's forecast. Multiplying the remaining wall-clock hours
    /// by the duty cycle converts the horizon into the same units as the rate. With a constant duty
    /// cycle the result is identical to even-pace, by construction; the two diverge only when the
    /// user's working rhythm changes, which is exactly the signal we want.
    /// </remarks>
    public static double DutyCycle(IReadOnlyList<Increment> increments)
    {
        double activeHours = 0, totalHours = 0;

        foreach (var inc in increments)
        {
            if (inc.Hours <= 1e-9) continue;
            totalHours += inc.Hours;

            if (inc.DeltaUsed <= ActivityThresholdPercent) continue;

            // A long gap that saw usage tells us work happened but not for how long. Credit a
            // bounded slice rather than the whole gap.
            activeHours += Math.Min(inc.Hours, WellSampledInterval.TotalHours);
        }

        if (totalHours <= 1e-9) return 1d;
        return Math.Clamp(activeHours / totalHours, 0.01d, 1d);
    }

    /// <summary>Default EWMA half-life for a window kind.</summary>
    public static TimeSpan HalfLifeFor(WindowKind kind) => kind switch
    {
        WindowKind.Session => TimeSpan.FromMinutes(25),
        WindowKind.Weekly or WindowKind.Model => TimeSpan.FromHours(8),
        WindowKind.Monthly => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(8),
    };

    /// <summary>
    /// Credibility weight theta = n / (n + k): how far to trust the responsive estimators over the
    /// robust baseline, given <paramref name="incrementCount"/> observations.
    /// </summary>
    public static double CredibilityWeight(int incrementCount)
        => incrementCount / (incrementCount + CredibilityK);

    /// <summary>
    /// Credibility-weighted blend of <em>projected usage</em>, not of rates.
    /// </summary>
    /// <remarks>
    /// Each estimator is projected over the horizon matching its own units before blending:
    /// even-pace and EWMA are per wall-clock hour, the active rate is per active hour. Blending
    /// the rates first and then picking one horizon would mix the two frames and misproject
    /// whenever the duty cycle is not 1.
    /// <para>
    /// The two responsive projections are averaged rather than maxed. On a front-loaded epoch the
    /// EWMA says "they stopped" and the active rate says "they burn fast when working"; both are
    /// true, and the honest answer is between them. Taking the max would fire an exhaustion alarm
    /// at a user who has already put the work down.
    /// </para>
    /// </remarks>
    /// <returns>Expected additional usage, in percentage points, over the horizon.</returns>
    public static double BlendProjection(
        double evenPaceProjection,
        double? ewmaProjection,
        double? activeProjection,
        int incrementCount)
    {
        var responsive = (ewmaProjection, activeProjection) switch
        {
            ({ } e, { } a) => 0.5 * (e + a),
            ({ } e, null) => e,
            (null, { } a) => a,
            _ => (double?)null,
        };

        if (responsive is not { } r) return evenPaceProjection;

        var theta = CredibilityWeight(incrementCount);
        return (theta * r) + ((1 - theta) * evenPaceProjection);
    }

    /// <summary>
    /// Variance of usage accrued per hour, for the prediction band.
    /// </summary>
    /// <remarks>
    /// Increments are resampled into equal-length bins before taking a variance. Taking the
    /// variance of raw interval rates instead would measure sampling jitter as much as real
    /// burstiness, because a 30-second interval and a 20-minute one carry very different noise.
    /// Returns null when there are too few bins to say anything.
    /// </remarks>
    public static double? VariancePerHour(IReadOnlyList<Increment> increments, TimeSpan binLength)
    {
        if (increments.Count < 2 || binLength <= TimeSpan.Zero) return null;

        var start = increments[0].From;
        var end = increments[^1].To;
        var totalHours = (end - start).TotalHours;
        var binHours = binLength.TotalHours;
        if (totalHours <= binHours || binHours <= 0) return null;

        var binCount = (int)Math.Ceiling(totalHours / binHours);
        if (binCount < 3) return null;

        var bins = new double[binCount];

        // Spread each increment's usage across the bins it overlaps, proportional to overlap. An
        // increment is a total over its interval, not an instant, so attributing it wholly to one
        // bin would manufacture spikes.
        foreach (var inc in increments)
        {
            if (inc.Hours <= 1e-9) continue;
            var from = (inc.From - start).TotalHours;
            var to = (inc.To - start).TotalHours;

            var firstBin = Math.Max(0, (int)(from / binHours));
            var lastBin = Math.Min(binCount - 1, (int)(to / binHours));

            for (var b = firstBin; b <= lastBin; b++)
            {
                var binStart = b * binHours;
                var binEnd = binStart + binHours;
                var overlap = Math.Min(to, binEnd) - Math.Max(from, binStart);
                if (overlap > 0) bins[b] += inc.DeltaUsed * (overlap / (to - from));
            }
        }

        var mean = bins.Average();
        var sumSquares = bins.Sum(v => (v - mean) * (v - mean));
        var sampleVariance = sumSquares / (bins.Length - 1);

        // Var per bin -> var per hour: bins are independent, so variance scales with count.
        return sampleVariance / binHours;
    }

    /// <summary>Bin length used for the variance estimate, by window kind.</summary>
    public static TimeSpan VarianceBinFor(WindowKind kind) => kind switch
    {
        WindowKind.Session => TimeSpan.FromMinutes(15),
        WindowKind.Weekly or WindowKind.Model => TimeSpan.FromHours(6),
        WindowKind.Monthly => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(6),
    };
}

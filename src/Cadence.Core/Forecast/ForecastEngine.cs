using Cadence.Core.Model;

namespace Cadence.Core.Forecast;

/// <summary>Which estimators the engine is allowed to use.</summary>
public enum EstimatorMode
{
    /// <summary>Credibility-weighted blend of even-pace, EWMA and active-rate.</summary>
    Blended,

    /// <summary>
    /// Even-pace only: the baseline every other estimator has to beat in the backtest. Shipping
    /// this is the correct outcome if the blend does not win on the user's own history.
    /// </summary>
    EvenPaceOnly,
}

/// <summary>Tunables for <see cref="ForecastEngine"/>, surfaced in Settings.</summary>
public sealed record ForecastOptions
{
    public EstimatorMode Mode { get; init; } = EstimatorMode.Blended;

    /// <summary>Suppress output until this fraction of the window has elapsed.</summary>
    public double MinElapsedFraction { get; init; } = 0.03;

    /// <summary>Suppress output below this many usable increments in the current epoch.</summary>
    public int MinIncrements { get; init; } = 3;

    /// <summary>
    /// When sampling gaps exceed this share of elapsed time, refuse rather than draw a band that
    /// pretends to knowledge we do not have.
    /// </summary>
    public double MaxGapShareOfElapsed { get; init; } = 0.6;

    /// <summary>Use the learned hour-of-week shape for weekly and monthly horizons.</summary>
    public bool UseIntensityProfile { get; init; } = true;

    /// <summary>
    /// Manual override for expected working hours per day, replacing the learned profile. Null
    /// means learn it.
    /// </summary>
    public double? WorkHoursPerDayOverride { get; init; }

    /// <summary>
    /// Coefficient of variation assumed when variance cannot be measured at all. 0.8 produces a
    /// visibly wide band, which is the honest signal at that point.
    /// </summary>
    public double FallbackCoefficientOfVariation { get; init; } = 0.8;

    /// <summary>
    /// Floor on the coefficient of variation even when the measured variance is lower.
    /// </summary>
    /// <remarks>
    /// A short, perfectly regular stretch really does measure a variance near zero, which would
    /// collapse the band to a point and present the projection as a certainty. Future usage is
    /// never certain, so keep a minimum spread.
    /// </remarks>
    public double MinCoefficientOfVariation { get; init; } = 0.15;

    /// <summary>Zone the hour-of-week rhythm is expressed in. Defaults to the machine's.</summary>
    public TimeZoneInfo? Zone { get; init; }

    public static readonly ForecastOptions Default = new();
}

/// <summary>Everything the engine needs about one window to produce a projection.</summary>
public sealed record WindowHistory(
    string WindowId,
    WindowKind Kind,
    TimeSpan? WindowLength,
    IReadOnlyList<Epoch> Epochs)
{
    public Epoch? CurrentEpoch => Epochs.Count > 0 ? Epochs[^1] : null;

    /// <summary>Completed epochs, used to learn the intensity shape.</summary>
    public IEnumerable<Epoch> HistoricalEpochs => Epochs.Count > 1 ? Epochs.Take(Epochs.Count - 1) : [];
}

/// <summary>
/// Turns stored history into a calibrated projection to the window's reset.
/// </summary>
/// <remarks>
/// The output is deliberately conservative at the edges: it refuses to speak early in a window,
/// widens bands when sampling was sparse, and leads with an exhaustion time once that becomes the
/// likelier outcome. A confident wrong "you are fine until Sunday" is worse than no forecast.
/// </remarks>
public sealed class ForecastEngine(ForecastOptions? options = null)
{
    private readonly ForecastOptions _options = options ?? ForecastOptions.Default;

    public Model.Forecast Project(WindowHistory history, DateTimeOffset now)
    {
        if (history.CurrentEpoch is not { } epoch)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.TooEarly);

        if (epoch.UsedPercent is not { } used)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.UsageUnknown);

        if (epoch.ResetsAt is not { } resetsAt)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.NoHorizon);

        if (resetsAt <= now)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.WindowClosed);

        var elapsedFraction = epoch.ElapsedFraction(now);
        var increments = epoch.Increments();

        if (elapsedFraction < _options.MinElapsedFraction || increments.Count < _options.MinIncrements)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.TooEarly);

        var elapsed = epoch.Elapsed(now);
        var maxGap = epoch.MaxSampleGap();
        if (elapsed > TimeSpan.Zero && maxGap.TotalSeconds > elapsed.TotalSeconds * _options.MaxGapShareOfElapsed)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.InsufficientCoverage);

        // --- rates, each in its own time frame -------------------------------------------------
        var blended = _options.Mode is EstimatorMode.Blended;

        var evenPace = BurnRate.EvenPace(used, elapsed);
        var ewma = blended ? BurnRate.Ewma(increments, BurnRate.HalfLifeFor(history.Kind)) : RateEstimate.None;
        var active = blended ? BurnRate.ActiveRate(increments) : RateEstimate.None;

        if (!evenPace.IsUsable && !ewma.IsUsable && !active.IsUsable)
            return Model.Forecast.NotAvailable(ForecastUnavailableReason.TooEarly);

        // --- horizons ---------------------------------------------------------------------------
        var wallClockHours = (resetsAt - now).TotalHours;
        var activeHours = ExpectedActiveHours(history, increments, now, resetsAt, wallClockHours);

        // --- projections, blended on the same scale ---------------------------------------------
        var mean = BurnRate.BlendProjection(
            evenPaceProjection: evenPace.PercentPerHour * wallClockHours,
            ewmaProjection: ewma.IsUsable ? ewma.PercentPerHour * wallClockHours : null,
            activeProjection: active.IsUsable ? active.PercentPerHour * activeHours : null,
            incrementCount: increments.Count);

        mean = Math.Max(0d, mean);

        var variance = ProjectedVariance(increments, history.Kind, wallClockHours, mean);
        var distribution = GammaDistribution.FromMoments(mean, variance);

        var headroom = Math.Max(0d, 100d - used);
        var exhaustProbability = Math.Clamp(1d - distribution.Cdf(headroom), 0d, 1d);

        var median = Math.Min(100d, used + distribution.Quantile(0.50));
        var p10 = Math.Min(100d, used + distribution.Quantile(0.10));
        var p90 = Math.Min(100d, used + distribution.Quantile(0.90));

        // --- exhaustion timing -------------------------------------------------------------------
        // Everything below is in wall-clock terms, since that is what a clock time means to the
        // user. The blended projection over the remaining wall-clock hours *is* the effective
        // average rate, so no unit conversion is needed.
        var effectiveRate = wallClockHours > 1e-9 ? mean / wallClockHours : 0d;

        DateTimeOffset? exhaustsAt = null, earliestExhaustAt = null;
        if (headroom > 0)
        {
            exhaustsAt = ExhaustionTime(now, headroom, effectiveRate, wallClockHours);

            var fastRate = wallClockHours > 1e-9 ? distribution.Quantile(0.90) / wallClockHours : 0d;
            earliestExhaustAt = ExhaustionTime(now, headroom, fastRate, wallClockHours);
        }

        return new Model.Forecast
        {
            Median = median,
            P10 = p10,
            P90 = p90,
            ExhaustProbability = exhaustProbability,
            ExhaustsAt = exhaustsAt,
            EarliestExhaustAt = earliestExhaustAt,
            SustainableRatePerHour = wallClockHours > 1e-9 ? headroom / wallClockHours : 0d,
            BurnRatePerHour = effectiveRate,
            PaceDelta = used - (100d * elapsedFraction),
            Confidence = ConfidenceFrom(increments.Count, maxGap, elapsed),
            Method = DescribeMethod(ewma, active, increments.Count),
            SampleCount = increments.Count,
            MaxSampleGap = maxGap,
        };
    }

    /// <summary>
    /// When the window hits 100% at <paramref name="ratePerHour"/>, or null if that is beyond the
    /// reset. Allowing a little past the reset would report a time in the next window, which reads
    /// as nonsense next to a "resets in" countdown.
    /// </summary>
    private static DateTimeOffset? ExhaustionTime(
        DateTimeOffset now, double headroom, double ratePerHour, double wallClockHours)
    {
        if (ratePerHour <= 1e-9) return null;
        var hours = headroom / ratePerHour;
        return hours <= wallClockHours ? now.AddHours(hours) : null;
    }

    /// <summary>
    /// Expected <em>active</em> hours between now and the reset, which is the horizon the
    /// active-rate estimator is measured against.
    /// </summary>
    private double ExpectedActiveHours(
        WindowHistory history,
        IReadOnlyList<Increment> increments,
        DateTimeOffset now,
        DateTimeOffset resetsAt,
        double wallClockHours)
    {
        if (_options.WorkHoursPerDayOverride is { } perDay)
            return wallClockHours * Math.Clamp(perDay / 24d, 0.01, 1d);

        // A learned hour-of-week shape only has something to say over a horizon long enough to
        // contain that shape. Inside a five-hour session it is noise.
        if (_options.UseIntensityProfile && history.Kind is not WindowKind.Session)
        {
            var profile = IntensityProfile.FromEpochs(history.Epochs, _options.Zone);
            if (profile.IsReliable)
                return Math.Max(profile.ExpectedActiveHours(now, resetsAt), 1e-6);
        }

        // Otherwise assume the rhythm observed so far in this epoch continues.
        return wallClockHours * BurnRate.DutyCycle(increments);
    }

    private static string DescribeMethod(RateEstimate ewma, RateEstimate active, int increments)
    {
        var parts = new List<string> { "even" };
        if (ewma.IsUsable) parts.Add("ewma");
        if (active.IsUsable) parts.Add("active");
        return $"blend[{string.Join('+', parts)}] theta={BurnRate.CredibilityWeight(increments):F2}";
    }

    private double ProjectedVariance(IReadOnlyList<Increment> increments, WindowKind kind, double horizonHours, double mean)
    {
        var perHour = BurnRate.VariancePerHour(increments, BurnRate.VarianceBinFor(kind));

        // A measured variance of zero is a real observation ("burn has been perfectly steady"),
        // not a missing one. Only a null means we could not measure. Conflating the two replaces
        // good evidence with the wide fallback band, which drags the median of the fitted Gamma
        // well below the projection and understates a clear exhaustion risk.
        var measured = perHour is { } v ? v * Math.Max(horizonHours, 0) : (double?)null;

        var cv = measured is null ? _options.FallbackCoefficientOfVariation : _options.MinCoefficientOfVariation;
        var floorSigma = cv * mean;
        var floor = floorSigma * floorSigma;

        return Math.Max(measured ?? 0d, floor);
    }

    private static ForecastConfidence ConfidenceFrom(int increments, TimeSpan maxGap, TimeSpan elapsed)
    {
        var gapShare = elapsed.TotalSeconds > 0 ? maxGap.TotalSeconds / elapsed.TotalSeconds : 1d;

        if (increments >= 12 && gapShare < 0.25) return ForecastConfidence.High;
        if (increments >= 6 && gapShare < 0.45) return ForecastConfidence.Medium;
        return ForecastConfidence.Low;
    }
}

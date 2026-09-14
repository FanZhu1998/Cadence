namespace Cadence.Core.Model;

/// <summary>Why the forecaster declined to produce numbers. Drives the placeholder copy.</summary>
public enum ForecastUnavailableReason
{
    /// <summary>Too little of the window has elapsed, or too few samples in this epoch.</summary>
    TooEarly,

    /// <summary>The window never reported a usable percentage.</summary>
    UsageUnknown,

    /// <summary>No reset time, so there is no horizon to project onto.</summary>
    NoHorizon,

    /// <summary>The window has already reset or is past its reset time.</summary>
    WindowClosed,

    /// <summary>Sampling gaps are so large that any band would be dishonest.</summary>
    InsufficientCoverage,
}

/// <summary>How much to trust the numbers. Drives band presentation and whether we lead with a forecast.</summary>
public enum ForecastConfidence { Low, Medium, High }

/// <summary>
/// A projection for a single <see cref="QuotaWindow"/> to its reset. Percent-valued fields are
/// on the same 0..100 scale as <see cref="QuotaWindow.UsedPercent"/>.
/// </summary>
public sealed record Forecast
{
    /// <summary>Set when no projection could be made; every other field is then default.</summary>
    public ForecastUnavailableReason? Unavailable { get; init; }

    public bool IsAvailable => Unavailable is null;

    /// <summary>P50 projected usage at reset, clamped to 100.</summary>
    public double Median { get; init; }

    /// <summary>P10 projected usage at reset, the optimistic edge of the band.</summary>
    public double P10 { get; init; }

    /// <summary>P90 projected usage at reset, the pessimistic edge.</summary>
    public double P90 { get; init; }

    /// <summary>Probability in [0,1] of hitting 100% before the window resets.</summary>
    public double ExhaustProbability { get; init; }

    /// <summary>Median time the window hits 100%, if it is on track to.</summary>
    public DateTimeOffset? ExhaustsAt { get; init; }

    /// <summary>Soonest plausible exhaustion time (from the high burn-rate edge).</summary>
    public DateTimeOffset? EarliestExhaustAt { get; init; }

    /// <summary>Percent per hour the user could spend and still land exactly at 100% at reset.</summary>
    public double SustainableRatePerHour { get; init; }

    /// <summary>Estimated burn rate in percent per effective hour.</summary>
    public double BurnRatePerHour { get; init; }

    /// <summary>used% minus elapsed%. Positive means burning faster than even pace.</summary>
    public double PaceDelta { get; init; }

    public ForecastConfidence Confidence { get; init; } = ForecastConfidence.Low;

    /// <summary>Which estimator produced the rate, for the diagnostics pane.</summary>
    public string Method { get; init; } = "unknown";

    /// <summary>Number of usable increments this estimate rests on.</summary>
    public int SampleCount { get; init; }

    /// <summary>Largest gap between consecutive samples in this epoch; widens the band.</summary>
    public TimeSpan MaxSampleGap { get; init; }

    /// <summary>Approximate tokens remaining, when the token/percent regression fits well enough.</summary>
    public long? RemainingTokensEstimate { get; init; }

    public static Forecast NotAvailable(ForecastUnavailableReason reason) => new() { Unavailable = reason };

    /// <summary>True when we should lead with "runs out at" rather than a projected percentage.</summary>
    public bool LeadWithExhaustion => IsAvailable && ExhaustProbability > 0.5 && ExhaustsAt is not null;

    /// <summary>True when burning faster than even pace.</summary>
    public bool InDeficit => PaceDelta > 0;
}

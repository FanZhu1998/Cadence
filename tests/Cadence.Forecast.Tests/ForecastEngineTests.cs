using Cadence.Core.Forecast;
using Cadence.Core.Model;

namespace Cadence.Forecast.Tests;

public class ForecastEngineTests
{
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
    private static readonly ForecastEngine Engine = new();

    [Fact]
    public void SteadyBurn_ProjectsTheObviousLinearAnswer()
    {
        // 10%/h for 2h of a 5h window: 20% now, 3h left, so 50% at reset.
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var now = samples[^1].Timestamp;
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, now);

        Assert.True(forecast.IsAvailable);
        // The median of a right-skewed Gamma sits a little below the mean projection, so this is a
        // tolerance rather than an equality: the claim under test is "about 50%", not "50.00%".
        Assert.InRange(forecast.Median, 48d, 51d);
        Assert.Equal(10d, forecast.BurnRatePerHour, 1);
    }

    [Fact]
    public void SteadyBurn_PaceDeltaIsZero_WhenTrackingEvenConsumption()
    {
        // 20%/h over a 5h window is exactly even pace: 100% consumed at exactly the reset.
        var samples = SyntheticSeries.Steady(20, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.Equal(0d, forecast.PaceDelta, 1);
    }

    [Fact]
    public void HeavyBurn_IsFlaggedAsExhausting_WithATimeBeforeReset()
    {
        // 30%/h against a 5h window: 60% used at 2h, and the remaining 40% lasts ~80 more minutes.
        var samples = SyntheticSeries.Steady(30, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var now = samples[^1].Timestamp;
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, now);

        Assert.True(forecast.ExhaustProbability > 0.9, $"expected near-certain exhaustion, got {forecast.ExhaustProbability:P0}");
        Assert.True(forecast.LeadWithExhaustion);
        Assert.NotNull(forecast.ExhaustsAt);

        var hoursOut = (forecast.ExhaustsAt!.Value - now).TotalHours;
        Assert.InRange(hoursOut, 1.0, 2.0);
        Assert.True(forecast.InDeficit);
    }

    [Fact]
    public void LightBurn_ReportsNoMeaningfulExhaustionRisk()
    {
        var samples = SyntheticSeries.Steady(2, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.True(forecast.ExhaustProbability < 0.05, $"got {forecast.ExhaustProbability:P0}");
        Assert.Null(forecast.ExhaustsAt);
        Assert.False(forecast.LeadWithExhaustion);
        Assert.False(forecast.InDeficit);
    }

    [Fact]
    public void FrontLoadedThenIdle_LandsBetweenTheStoppedAndEvenPaceReadings()
    {
        // 40% burned in the first hour, then nothing for an hour. Even-pace says 100% at reset;
        // a pure EWMA says ~40%. Neither alone is right, and 100% would fire a false alarm at a
        // user who has already stopped.
        var samples = SyntheticSeries.FrontLoaded(40, burstHours: 1, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.Equal(40d, history.CurrentEpoch!.UsedPercent!.Value, 1);
        Assert.True(forecast.Median < 99d, $"should not project certain exhaustion for an idle user, got {forecast.Median:F1}%");
        Assert.True(forecast.Median > 45d, $"should not assume the user has stopped for good, got {forecast.Median:F1}%");
    }

    [Fact]
    public void DutyCycleCorrection_KeepsBurstyUsageCloseToItsEvenPaceProjection()
    {
        // Work 30 min, idle 30 min, repeat. The active rate is double the average rate, and the
        // duty cycle halves the horizon to compensate. Without that correction this projects ~2x.
        var samples = SyntheticSeries.Bursty(
            activePercentPerHour: 12, onHours: 0.5, offHours: 0.5,
            SevenDays, TimeSpan.FromMinutes(10), TimeSpan.FromHours(24));
        var now = samples[^1].Timestamp;
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Weekly, SevenDays);

        var forecast = Engine.Project(history, now);
        var used = history.CurrentEpoch!.UsedPercent!.Value;

        // Average rate is 6%/h; 144h remain, so even-pace projects far past 100 and it clamps.
        // The check that matters is that the effective rate stays near the average, not near 12.
        Assert.InRange(forecast.BurnRatePerHour, 4.0, 8.0);
        Assert.True(used > 0);
    }

    [Fact]
    public void OvernightIdleWeekly_DoesNotReportANearZeroRateInTheMorning()
    {
        // 8 working hours a day at 1%/h against a 7-day window, sampled through two full days.
        // A naive EWMA measured at 09:00 after 16 idle hours would read ~0 and forecast "fine".
        var samples = SyntheticSeries.DailyWorkPattern(
            activePercentPerHour: 1.0, workHoursPerDay: 8,
            SevenDays, TimeSpan.FromMinutes(15), TimeSpan.FromHours(48));
        var now = samples[^1].Timestamp;
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Weekly, SevenDays);

        var forecast = Engine.Project(history, now);

        Assert.True(forecast.IsAvailable);
        Assert.True(forecast.BurnRatePerHour > 0.1,
            $"idle overnight must not collapse the rate to zero, got {forecast.BurnRatePerHour:F3}%/h");

        // Truth: 8%/day for 5 more days on top of 16% used = ~56%.
        Assert.InRange(forecast.Median, 30d, 85d);
    }

    [Fact]
    public void Bands_AreOrderedAndBracketTheMedian()
    {
        var samples = SyntheticSeries.Bursty(20, 0.3, 0.2, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.True(forecast.P10 <= forecast.Median, $"P10 {forecast.P10} > median {forecast.Median}");
        Assert.True(forecast.Median <= forecast.P90, $"median {forecast.Median} > P90 {forecast.P90}");
        Assert.True(forecast.P10 >= 0, "a band edge must never be negative");
        Assert.True(forecast.P90 <= 100, "projections clamp at 100%");
    }

    [Fact]
    public void ProjectionIsNeverBelowCurrentUsage()
    {
        // Quota does not come back before a reset, so no quantile may imply usage going down.
        var samples = SyntheticSeries.FrontLoaded(50, 0.5, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(3));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);
        var used = history.CurrentEpoch!.UsedPercent!.Value;

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.True(forecast.P10 >= used - 1e-9, $"P10 {forecast.P10} below current usage {used}");
    }

    // ---- guardrails -------------------------------------------------------------------------

    [Fact]
    public void RefusesToSpeak_TooEarlyInTheWindow()
    {
        // Six minutes into a 5h window is 2% elapsed, below the 3% floor.
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(6));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.TooEarly, forecast.Unavailable);
    }

    [Fact]
    public void RefusesToSpeak_WithTooFewSamples()
    {
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.TooEarly, forecast.Unavailable);
    }

    [Fact]
    public void RefusesToSpeak_WhenUsageIsUnknown()
    {
        // Claude education and enterprise accounts return reset metadata with no utilisation.
        var t0 = SyntheticSeries.Origin;
        var samples = Enumerable.Range(0, 10)
            .Select(i => new UsageSample(t0.AddMinutes(i * 10), null, t0 + FiveHours))
            .ToList();
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.UsageUnknown, forecast.Unavailable);
    }

    [Fact]
    public void RefusesToSpeak_WithoutAResetTime()
    {
        var t0 = SyntheticSeries.Origin;
        var samples = Enumerable.Range(0, 10)
            .Select(i => new UsageSample(t0.AddMinutes(i * 10), i * 2.0, null))
            .ToList();
        var history = new WindowHistory("w", WindowKind.Session, null,
            EpochDetector.Split(samples, "w", WindowKind.Session, null));

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.NoHorizon, forecast.Unavailable);
    }

    [Fact]
    public void RefusesToSpeak_AfterTheWindowHasClosed()
    {
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, SyntheticSeries.Origin + FiveHours + TimeSpan.FromMinutes(1));

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.WindowClosed, forecast.Unavailable);
    }

    [Fact]
    public void RefusesToSpeak_WhenSamplingLeftTooBigAHole()
    {
        // Two samples an hour apart, then a two-hour blackout. Any band drawn over that is fiction.
        var t0 = SyntheticSeries.Origin;
        var resetsAt = t0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(t0, 0d, resetsAt),
            new(t0.AddMinutes(10), 3d, resetsAt),
            new(t0.AddMinutes(20), 6d, resetsAt),
            new(t0.AddMinutes(190), 30d, resetsAt),
        };
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, samples[^1].Timestamp);

        Assert.False(forecast.IsAvailable);
        Assert.Equal(ForecastUnavailableReason.InsufficientCoverage, forecast.Unavailable);
    }

    [Fact]
    public void Confidence_RisesWithEvidence()
    {
        var sparse = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(80));
        var dense = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(2), TimeSpan.FromHours(2));

        var sparseForecast = Engine.Project(
            SyntheticSeries.ToHistory(sparse, WindowKind.Session, FiveHours), sparse[^1].Timestamp);
        var denseForecast = Engine.Project(
            SyntheticSeries.ToHistory(dense, WindowKind.Session, FiveHours), dense[^1].Timestamp);

        Assert.True(denseForecast.Confidence >= sparseForecast.Confidence);
        Assert.Equal(ForecastConfidence.High, denseForecast.Confidence);
    }

    [Fact]
    public void SustainableRate_LandsExactlyAtOneHundredPercent()
    {
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));
        var now = samples[^1].Timestamp;
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Session, FiveHours);

        var forecast = Engine.Project(history, now);
        var used = history.CurrentEpoch!.UsedPercent!.Value;
        var hoursLeft = (history.CurrentEpoch.ResetsAt!.Value - now).TotalHours;

        Assert.Equal(100d, used + (forecast.SustainableRatePerHour * hoursLeft), 6);
    }

    [Fact]
    public void WorkHoursOverride_ShortensTheEffectiveHorizon()
    {
        // Asserting the override is actually wired through, not silently ignored.
        var samples = SyntheticSeries.DailyWorkPattern(1.0, 8, SevenDays, TimeSpan.FromMinutes(15), TimeSpan.FromHours(48));
        var history = SyntheticSeries.ToHistory(samples, WindowKind.Weekly, SevenDays);
        var now = samples[^1].Timestamp;

        var full = new ForecastEngine(new ForecastOptions { WorkHoursPerDayOverride = 24 }).Project(history, now);
        var part = new ForecastEngine(new ForecastOptions { WorkHoursPerDayOverride = 4 }).Project(history, now);

        Assert.True(part.Median < full.Median,
            $"a 4h working day must project below a 24h one ({part.Median:F1} vs {full.Median:F1})");
    }
}

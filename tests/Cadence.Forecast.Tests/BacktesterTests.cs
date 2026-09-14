using Cadence.Core.Forecast;
using Cadence.Core.Model;

namespace Cadence.Forecast.Tests;

public class BacktesterTests
{
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);

    /// <summary>
    /// Builds a run of complete epochs, each burning at its own steady rate, so the outcome at
    /// every reset is known exactly.
    /// </summary>
    private static List<UsageSample> MultiEpoch(IReadOnlyList<double> ratesPerHour, TimeSpan cadence)
    {
        var samples = new List<UsageSample>();
        var cursor = SyntheticSeries.Origin;

        foreach (var rate in ratesPerHour)
        {
            var reset = cursor + FiveHours;
            for (var t = TimeSpan.Zero; t < FiveHours; t += cadence)
                samples.Add(new UsageSample(cursor + t, Math.Min(100d, t.TotalHours * rate), reset));

            cursor = reset;
        }

        return samples;
    }

    [Fact]
    public void SteadyEpochs_AreForecastAccurately()
    {
        // Every epoch burns at a constant rate, so a competent forecaster should be close.
        var samples = MultiEpoch([8, 12, 10, 9, 11, 10], TimeSpan.FromMinutes(10));

        var result = Backtester.Run(samples, "w", WindowKind.Session, FiveHours);

        Assert.True(result.PointCount > 50, $"expected a decent number of replay points, got {result.PointCount}");
        Assert.True(result.MeanAbsoluteError < 8,
            $"steady burn should be easy to forecast; MAE was {result.MeanAbsoluteError:F1}pp");
    }

    [Fact]
    public void BandCoverage_IsInTheRightNeighbourhood()
    {
        // Noisy but stationary burn: the P10-P90 band should capture roughly 80% of outcomes.
        var rng = new Random(4242);
        var rates = Enumerable.Range(0, 12).Select(_ => 8 + (rng.NextDouble() * 8)).ToList();
        var samples = MultiEpoch(rates, TimeSpan.FromMinutes(10));

        var result = Backtester.Run(samples, "w", WindowKind.Session, FiveHours);

        // A wide tolerance on purpose: this is a regression gate against the bands collapsing to a
        // point or ballooning to cover everything, not a claim of exact calibration.
        Assert.InRange(result.BandCoverage, 0.45, 1.0);
    }

    [Fact]
    public void ExhaustionProbability_BeatsTheClimatologyBaseline()
    {
        // A mix of epochs that exhaust and epochs that do not. A useful probability must score
        // better than always predicting the base rate.
        var samples = MultiEpoch([25, 5, 30, 4, 22, 6, 28, 5], TimeSpan.FromMinutes(10));

        var result = Backtester.Run(samples, "w", WindowKind.Session, FiveHours);

        Assert.InRange(result.ExhaustionBaseRate, 0.05, 0.95); // the test is only meaningful if mixed
        Assert.True(result.BeatsClimatology,
            $"Brier {result.BrierScore:F4} should beat climatology {result.ClimatologyBrierScore:F4}");
    }

    [Fact]
    public void BlendedEstimator_IsComparedAgainstEvenPaceOnTheSameData()
    {
        // The comparison the design demands before shipping anything cleverer than the baseline.
        var rng = new Random(99);
        var rates = Enumerable.Range(0, 10).Select(_ => 6 + (rng.NextDouble() * 12)).ToList();
        var samples = MultiEpoch(rates, TimeSpan.FromMinutes(10));

        var blended = Backtester.Run(samples, "w", WindowKind.Session, FiveHours,
            new ForecastOptions { Mode = EstimatorMode.Blended });
        var baseline = Backtester.Run(samples, "w", WindowKind.Session, FiveHours,
            new ForecastOptions { Mode = EstimatorMode.EvenPaceOnly });

        Assert.Equal(EstimatorMode.Blended, blended.Mode);
        Assert.Equal(EstimatorMode.EvenPaceOnly, baseline.Mode);
        Assert.Equal(blended.PointCount, baseline.PointCount);

        // On perfectly steady burn the two should agree closely; the blend must not be *worse*.
        Assert.True(blended.MeanAbsoluteError <= baseline.MeanAbsoluteError + 2.0,
            $"blend {blended.MeanAbsoluteError:F2}pp must not lose to even-pace {baseline.MeanAbsoluteError:F2}pp");
    }

    [Fact]
    public void OpenEpochIsExcluded_BecauseItsOutcomeIsNotKnownYet()
    {
        var samples = MultiEpoch([10, 10, 10], TimeSpan.FromMinutes(10));

        var result = Backtester.Run(samples, "w", WindowKind.Session, FiveHours);

        Assert.Equal(2, result.EpochCount);
    }

    [Fact]
    public void ReliabilityBins_PartitionTheProbabilityRange()
    {
        var samples = MultiEpoch([25, 5, 30, 4], TimeSpan.FromMinutes(10));

        var result = Backtester.Run(samples, "w", WindowKind.Session, FiveHours);

        Assert.Equal(10, result.Reliability.Count);
        Assert.Equal(0d, result.Reliability[0].LowerBound);
        Assert.Equal(1d, result.Reliability[^1].UpperBound);
        Assert.Equal(result.PointCount, result.Reliability.Sum(b => b.Count));
    }

    [Fact]
    public void EmptyHistory_ScoresAsEmptyRatherThanThrowing()
    {
        var result = Backtester.Run([], "w", WindowKind.Session, FiveHours);

        Assert.Equal(0, result.PointCount);
        Assert.Empty(result.Reliability);
    }

    [Theory]
    [InlineData(0.5, 10, 10, 0)]        // perfect prediction, no loss
    [InlineData(0.1, 10, 20, 1.0)]      // under-predicting the P10 costs q * error
    [InlineData(0.9, 20, 10, 1.0)]      // over-predicting the P90 costs (1-q) * error
    public void PinballLoss_MatchesItsDefinition(double q, double predicted, double actual, double expected)
        => Assert.Equal(expected, BacktestPoint.PinballLoss(predicted, actual, q), 9);
}

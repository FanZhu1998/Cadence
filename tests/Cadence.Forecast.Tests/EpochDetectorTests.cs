using Cadence.Core.Forecast;
using Cadence.Core.Model;

namespace Cadence.Forecast.Tests;

public class EpochDetectorTests
{
    private static readonly DateTimeOffset T0 = SyntheticSeries.Origin;
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);

    private static IReadOnlyList<Epoch> Split(IEnumerable<UsageSample> samples)
        => EpochDetector.Split(samples, "w", WindowKind.Session, FiveHours);

    [Fact]
    public void MonotoneSeries_IsASingleEpoch()
    {
        var samples = SyntheticSeries.Steady(10, FiveHours, TimeSpan.FromMinutes(10), TimeSpan.FromHours(4));

        var epochs = Split(samples);

        Assert.Single(epochs);
        Assert.Equal(samples.Count, epochs[0].Samples.Count);
    }

    [Fact]
    public void MaterialDrop_StartsANewEpoch()
    {
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0, 10d, reset),
            new(T0.AddMinutes(30), 40d, reset),
            new(T0.AddMinutes(60), 80d, reset),
            new(T0.AddMinutes(90), 3d, reset + FiveHours), // reset happened
            new(T0.AddMinutes(120), 12d, reset + FiveHours),
        };

        var epochs = Split(samples);

        Assert.Equal(2, epochs.Count);
        Assert.Equal(3, epochs[0].Samples.Count);
        Assert.Equal(2, epochs[1].Samples.Count);
    }

    [Fact]
    public void SmallDrop_IsTreatedAsProviderNoise_NotAReset()
    {
        // Providers round, and occasionally revise a figure down a fraction of a point. Splitting
        // on that would discard the epoch's history every few minutes.
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0, 40d, reset),
            new(T0.AddMinutes(10), 41d, reset),
            new(T0.AddMinutes(20), 40.4d, reset),
            new(T0.AddMinutes(30), 42d, reset),
        };

        Assert.Single(Split(samples));
    }

    [Fact]
    public void ResetRollingForward_StartsANewEpoch_EvenWithoutAUsageDrop()
    {
        // A busy user can cross a reset and immediately burn back to a similar percentage.
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0, 50d, reset),
            new(T0.AddMinutes(30), 52d, reset),
            new(T0.AddMinutes(60), 53d, reset + FiveHours),
        };

        var epochs = Split(samples);

        Assert.Equal(2, epochs.Count);
    }

    [Fact]
    public void CrossingAKnownResetTime_StartsANewEpoch()
    {
        // No usage drop and no new reset metadata, but the clock passed the reset we were told about.
        var reset = T0.AddMinutes(45);
        var samples = new List<UsageSample>
        {
            new(T0, 50d, reset),
            new(T0.AddMinutes(30), 55d, reset),
            new(T0.AddMinutes(90), 56d, reset),
        };

        Assert.Equal(2, Split(samples).Count);
    }

    [Fact]
    public void UnknownUsage_DoesNotFabricateAReset()
    {
        // null means "the provider did not say", not "usage fell to zero".
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0, 40d, reset),
            new(T0.AddMinutes(10), null, reset),
            new(T0.AddMinutes(20), 45d, reset),
        };

        Assert.Single(Split(samples));
    }

    [Fact]
    public void SamplesAreSortedBeforeSplitting()
    {
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0.AddMinutes(20), 30d, reset),
            new(T0, 10d, reset),
            new(T0.AddMinutes(10), 20d, reset),
        };

        var epochs = Split(samples);

        Assert.Single(epochs);
        Assert.Equal([10d, 20d, 30d], epochs[0].Samples.Select(s => s.UsedPercent));
    }

    [Fact]
    public void EmptyInput_YieldsNoEpochs() => Assert.Empty(Split([]));

    /// <summary>
    /// The property the blueprint asks for: over a series built as N known monotone runs separated
    /// by resets, the detector must recover exactly N epochs, never joining two or splitting one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(20)]
    public void Property_RecoversExactlyTheEpochsThatWereGenerated(int epochCount)
    {
        var rng = new Random(20260913 + epochCount);
        var samples = new List<UsageSample>();
        var cursor = T0;
        var expectedPerEpoch = new List<int>();

        for (var e = 0; e < epochCount; e++)
        {
            var windowStart = cursor;
            var reset = windowStart + FiveHours;
            var used = 0d;
            var count = 0;

            // A monotone run of samples inside one window, stopping short of the reset.
            for (var t = TimeSpan.Zero; t < FiveHours - TimeSpan.FromMinutes(20); t += TimeSpan.FromMinutes(rng.Next(5, 20)))
            {
                used = Math.Min(99d, used + (rng.NextDouble() * 6));
                samples.Add(new UsageSample(windowStart + t, used, reset));
                count++;
            }

            expectedPerEpoch.Add(count);
            cursor = reset; // the next window begins exactly at this reset
        }

        var epochs = Split(samples);

        Assert.Equal(epochCount, epochs.Count);
        Assert.Equal(expectedPerEpoch, epochs.Select(x => x.Samples.Count));

        // And no epoch may contain a backwards step, which would mean a boundary was missed.
        foreach (var epoch in epochs)
        {
            var percentages = epoch.Samples.Select(s => s.UsedPercent!.Value).ToArray();
            for (var i = 1; i < percentages.Length; i++)
            {
                Assert.True(percentages[i] >= percentages[i - 1] - EpochDetector.MaterialDropPercent,
                    $"epoch {epoch.Id} contains a reset that was not detected");
            }
        }
    }

    [Fact]
    public void Increments_ClampNegativeStepsRatherThanDroppingThem()
    {
        // Dropping the pair would shorten observed elapsed time and inflate every derived rate.
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample>
        {
            new(T0, 10d, reset),
            new(T0.AddMinutes(30), 9d, reset),
            new(T0.AddMinutes(60), 20d, reset),
        };

        var increments = Split(samples)[0].Increments();

        Assert.Equal(2, increments.Count);
        Assert.Equal(0d, increments[0].DeltaUsed);
        Assert.Equal(11d, increments[1].DeltaUsed);
        Assert.Equal(TimeSpan.FromHours(1), increments[0].Duration + increments[1].Duration);
    }

    [Fact]
    public void Increments_IgnoreASampleWhoseClockWentBackwards()
    {
        var reset = T0 + FiveHours;
        var samples = new List<UsageSample> { new(T0, 10d, reset), new(T0, 12d, reset) };

        Assert.Empty(Split(samples)[0].Increments());
    }

    [Fact]
    public void WindowStart_IsInferredFromResetMinusLength()
    {
        var reset = T0 + FiveHours;
        var epoch = Split([new UsageSample(T0.AddHours(1), 20d, reset)])[0];

        Assert.Equal(T0, epoch.StartedAt);
    }
}

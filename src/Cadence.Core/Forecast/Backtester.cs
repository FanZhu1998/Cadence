using Cadence.Core.Model;

namespace Cadence.Core.Forecast;

/// <summary>One replayed forecast paired with what actually happened.</summary>
public sealed record BacktestPoint(
    DateTimeOffset At,
    double ElapsedFraction,
    double UsedAtForecastTime,
    Model.Forecast Forecast,
    double ActualAtReset)
{
    public bool ActuallyExhausted => ActualAtReset >= 99.5;

    public double AbsoluteError => Math.Abs(Forecast.Median - ActualAtReset);

    public bool InsideBand => ActualAtReset >= Forecast.P10 && ActualAtReset <= Forecast.P90;

    /// <summary>Pinball (quantile) loss, the proper scoring rule for a quantile forecast.</summary>
    public static double PinballLoss(double predicted, double actual, double q)
    {
        var delta = actual - predicted;
        return Math.Max(q * delta, (q - 1) * delta);
    }
}

/// <summary>One bin of a reliability diagram: predicted probability versus observed frequency.</summary>
public sealed record ReliabilityBin(double LowerBound, double UpperBound, int Count, double MeanPredicted, double ObservedFrequency);

/// <summary>Scored output of a walk-forward replay.</summary>
public sealed record BacktestResult
{
    public required string WindowId { get; init; }

    public required EstimatorMode Mode { get; init; }

    public int PointCount { get; init; }

    public int EpochCount { get; init; }

    /// <summary>Mean absolute error of the median projection, in percentage points.</summary>
    public double MeanAbsoluteError { get; init; }

    public double MedianAbsoluteError { get; init; }

    public double PinballP10 { get; init; }

    public double PinballP50 { get; init; }

    public double PinballP90 { get; init; }

    /// <summary>Share of outcomes inside the P10-P90 band. Should land near 0.80 if calibrated.</summary>
    public double BandCoverage { get; init; }

    /// <summary>Brier score of the exhaustion probability. Lower is better.</summary>
    public double BrierScore { get; init; }

    /// <summary>
    /// Brier score of always predicting the base rate. A forecast that cannot beat this is adding
    /// nothing over knowing how often exhaustion happens at all.
    /// </summary>
    public double ClimatologyBrierScore { get; init; }

    public double ExhaustionBaseRate { get; init; }

    public IReadOnlyList<ReliabilityBin> Reliability { get; init; } = [];

    public bool BeatsClimatology => BrierScore < ClimatologyBrierScore;

    /// <summary>How far band coverage is from its 80% target, in percentage points.</summary>
    public double CalibrationError => Math.Abs(BandCoverage - 0.80) * 100;
}

/// <summary>
/// Walk-forward replay: at each historical timestamp, forecast using only data available then,
/// and score it against what actually happened by the reset.
/// </summary>
/// <remarks>
/// This exists so the forecast can be disproven. The blend is only worth shipping over plain
/// even-pace if it wins here on the user's own history; if it does not, the honest move is to ship
/// the baseline. Nothing about a projection is trustworthy just because the maths is elaborate.
/// </remarks>
public static class Backtester
{
    /// <summary>
    /// Replays every completed epoch in <paramref name="samples"/>.
    /// </summary>
    /// <remarks>
    /// Only completed epochs are scored: the outcome at reset has to be known to score against,
    /// and the epoch in progress does not have one yet.
    /// </remarks>
    public static BacktestResult Run(
        IReadOnlyList<UsageSample> samples,
        string windowId,
        WindowKind kind,
        TimeSpan? windowLength,
        ForecastOptions? options = null,
        DateTimeOffset? from = null)
    {
        var opts = options ?? ForecastOptions.Default;
        var engine = new ForecastEngine(opts);

        var filtered = from is { } start ? samples.Where(s => s.Timestamp >= start).ToList() : samples.ToList();
        var epochs = EpochDetector.Split(filtered, windowId, kind, windowLength);

        // The final epoch is still open, so its outcome is unknown.
        var completed = epochs.Count > 1 ? epochs.Take(epochs.Count - 1).ToList() : [];

        var points = new List<BacktestPoint>();

        for (var e = 0; e < completed.Count; e++)
        {
            var epoch = completed[e];
            var actual = epoch.Samples.LastOrDefault(s => s.UsedPercent is not null)?.UsedPercent;
            if (actual is not { } outcome) continue;

            var known = epoch.Samples.Where(s => s.UsedPercent is not null).ToList();

            // Replay each prefix. History passed to the engine includes the *earlier* epochs, so
            // the intensity profile sees only what was learnable at that moment.
            for (var i = 2; i < known.Count; i++)
            {
                var prefix = known.Take(i + 1).ToList();
                var at = prefix[^1].Timestamp;

                var priorEpochs = completed.Take(e).ToList();
                var replayEpoch = new Epoch(e, windowId, kind, windowLength, prefix);
                priorEpochs.Add(replayEpoch);

                var history = new WindowHistory(windowId, kind, windowLength, priorEpochs);
                var forecast = engine.Project(history, at);
                if (!forecast.IsAvailable) continue;

                points.Add(new BacktestPoint(
                    at,
                    replayEpoch.ElapsedFraction(at),
                    prefix[^1].UsedPercent!.Value,
                    forecast,
                    outcome));
            }
        }

        return Score(points, windowId, opts.Mode, completed.Count);
    }

    /// <summary>Scores an already-replayed set of points.</summary>
    public static BacktestResult Score(
        IReadOnlyList<BacktestPoint> points, string windowId, EstimatorMode mode, int epochCount)
    {
        if (points.Count == 0)
        {
            return new BacktestResult { WindowId = windowId, Mode = mode, EpochCount = epochCount };
        }

        var errors = points.Select(p => p.AbsoluteError).OrderBy(x => x).ToArray();
        var baseRate = points.Count(p => p.ActuallyExhausted) / (double)points.Count;

        return new BacktestResult
        {
            WindowId = windowId,
            Mode = mode,
            PointCount = points.Count,
            EpochCount = epochCount,
            MeanAbsoluteError = errors.Average(),
            MedianAbsoluteError = errors[errors.Length / 2],
            PinballP10 = points.Average(p => BacktestPoint.PinballLoss(p.Forecast.P10, p.ActualAtReset, 0.10)),
            PinballP50 = points.Average(p => BacktestPoint.PinballLoss(p.Forecast.Median, p.ActualAtReset, 0.50)),
            PinballP90 = points.Average(p => BacktestPoint.PinballLoss(p.Forecast.P90, p.ActualAtReset, 0.90)),
            BandCoverage = points.Count(p => p.InsideBand) / (double)points.Count,
            BrierScore = points.Average(p => Squared(p.Forecast.ExhaustProbability - (p.ActuallyExhausted ? 1d : 0d))),
            ClimatologyBrierScore = points.Average(p => Squared(baseRate - (p.ActuallyExhausted ? 1d : 0d))),
            ExhaustionBaseRate = baseRate,
            Reliability = BuildReliability(points),
        };
    }

    private static double Squared(double x) => x * x;

    private static IReadOnlyList<ReliabilityBin> BuildReliability(IReadOnlyList<BacktestPoint> points, int bins = 10)
    {
        var result = new List<ReliabilityBin>(bins);

        for (var b = 0; b < bins; b++)
        {
            var lower = b / (double)bins;
            var upper = (b + 1) / (double)bins;

            // Closed at the top only in the last bin, so p == 1.0 is not dropped.
            var inBin = points.Where(p =>
                p.Forecast.ExhaustProbability >= lower &&
                (b == bins - 1 ? p.Forecast.ExhaustProbability <= upper : p.Forecast.ExhaustProbability < upper))
                .ToArray();

            if (inBin.Length == 0)
            {
                result.Add(new ReliabilityBin(lower, upper, 0, double.NaN, double.NaN));
                continue;
            }

            result.Add(new ReliabilityBin(
                lower, upper, inBin.Length,
                inBin.Average(p => p.Forecast.ExhaustProbability),
                inBin.Count(p => p.ActuallyExhausted) / (double)inBin.Length));
        }

        return result;
    }
}

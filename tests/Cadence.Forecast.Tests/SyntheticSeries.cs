using Cadence.Core.Forecast;
using Cadence.Core.Model;

namespace Cadence.Forecast.Tests;

/// <summary>
/// Builds sample series with known burn shapes, so a forecast can be checked against a truth we
/// chose rather than against whatever the code happens to produce.
/// </summary>
internal static class SyntheticSeries
{
    public static readonly DateTimeOffset Origin = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero); // a Monday

    /// <summary>
    /// Samples a caller-supplied cumulative-usage curve at a fixed cadence.
    /// </summary>
    /// <param name="usedAt">Cumulative percent used, as a function of hours since window start.</param>
    public static List<UsageSample> Sample(
        Func<double, double> usedAt,
        TimeSpan windowLength,
        TimeSpan cadence,
        TimeSpan through,
        DateTimeOffset? start = null)
    {
        var t0 = start ?? Origin;
        var resetsAt = t0 + windowLength;
        var samples = new List<UsageSample>();

        for (var elapsed = TimeSpan.Zero; elapsed <= through; elapsed += cadence)
        {
            var used = Math.Clamp(usedAt(elapsed.TotalHours), 0d, 100d);
            samples.Add(new UsageSample(t0 + elapsed, used, resetsAt));
        }

        return samples;
    }

    /// <summary>Constant burn of <paramref name="percentPerHour"/>.</summary>
    public static List<UsageSample> Steady(
        double percentPerHour, TimeSpan windowLength, TimeSpan cadence, TimeSpan through, DateTimeOffset? start = null)
        => Sample(h => h * percentPerHour, windowLength, cadence, through, start);

    /// <summary>Burns hard for <paramref name="burstHours"/>, then stops dead.</summary>
    public static List<UsageSample> FrontLoaded(
        double burstPercentPerHour, double burstHours, TimeSpan windowLength, TimeSpan cadence, TimeSpan through,
        DateTimeOffset? start = null)
        => Sample(h => Math.Min(h, burstHours) * burstPercentPerHour, windowLength, cadence, through, start);

    /// <summary>Alternates <paramref name="onHours"/> of work with <paramref name="offHours"/> idle.</summary>
    public static List<UsageSample> Bursty(
        double activePercentPerHour, double onHours, double offHours,
        TimeSpan windowLength, TimeSpan cadence, TimeSpan through, DateTimeOffset? start = null)
    {
        var period = onHours + offHours;
        return Sample(
            h =>
            {
                var complete = Math.Floor(h / period);
                var into = h - (complete * period);
                var activeHours = (complete * onHours) + Math.Min(into, onHours);
                return activeHours * activePercentPerHour;
            },
            windowLength, cadence, through, start);
    }

    /// <summary>
    /// A weekly rhythm: <paramref name="workHoursPerDay"/> of work each day starting at 09:00,
    /// nothing overnight.
    /// </summary>
    public static List<UsageSample> DailyWorkPattern(
        double activePercentPerHour, double workHoursPerDay,
        TimeSpan windowLength, TimeSpan cadence, TimeSpan through, DateTimeOffset? start = null)
    {
        var t0 = start ?? Origin;

        // Origin is 09:00, so "hours into the working day" is just h mod 24.
        return Sample(
            h =>
            {
                var days = Math.Floor(h / 24);
                var into = h - (days * 24);
                var active = (days * workHoursPerDay) + Math.Min(into, workHoursPerDay);
                return active * activePercentPerHour;
            },
            windowLength, cadence, through, t0);
    }

    public static WindowHistory ToHistory(
        IEnumerable<UsageSample> samples, WindowKind kind, TimeSpan windowLength, string windowId = "test.window")
        => new(windowId, kind, windowLength, EpochDetector.Split(samples, windowId, kind, windowLength));
}

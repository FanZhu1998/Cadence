namespace Cadence.Core.Forecast;

/// <summary>
/// E4 - a learned hour-of-week activity shape.
/// </summary>
/// <remarks>
/// Projecting a weekly window over raw wall-clock hours assumes the user works at 3am on Sunday.
/// This learns when they actually work, so the horizon is measured in <em>expected active hours</em>.
/// It replaces the "work days: 4 / 5 / 7" setting that comparable tools ask the user to fill in.
/// </remarks>
public sealed class IntensityProfile(TimeZoneInfo? zone = null)
{
    /// <summary>168 buckets: hour-of-week, Monday 00:00 = 0.</summary>
    public const int BucketCount = 7 * 24;

    /// <summary>Below this many observed hours the shape is noise; callers should ignore it.</summary>
    public const int MinObservedHours = 48;

    private readonly double[] _activeHours = new double[BucketCount];
    private readonly double[] _totalHours = new double[BucketCount];

    /// <summary>
    /// The frame the weekly rhythm is expressed in. Working hours are a local-time phenomenon, so
    /// bucketing has to happen in the user's zone, not in UTC.
    /// </summary>
    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Local;

    /// <summary>Total observed hours across all buckets.</summary>
    public double ObservedHours => _totalHours.Sum();

    /// <summary>True when there is enough coverage for the shape to mean anything.</summary>
    public bool IsReliable => ObservedHours >= MinObservedHours && _activeHours.Sum() > 0;

    /// <summary>Bucket index for a timestamp, in this profile's zone.</summary>
    public int BucketOf(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, Zone);
        // Monday-first so the working week is contiguous.
        var day = ((int)local.DayOfWeek + 6) % 7;
        return (day * 24) + local.Hour;
    }

    /// <summary>
    /// The next whole hour strictly after <paramref name="t"/>, by tick arithmetic.
    /// </summary>
    /// <remarks>
    /// Deliberately not rebuilt from a <c>DateTime</c> plus an offset: that throws whenever the
    /// two disagree, which is every machine outside UTC. Arithmetic on the instant keeps the
    /// offset intact and stays correct across a DST transition.
    /// </remarks>
    private static DateTimeOffset NextHourBoundary(DateTimeOffset t)
    {
        var remainder = t.Ticks % TimeSpan.TicksPerHour;
        return t.AddTicks(TimeSpan.TicksPerHour - remainder);
    }

    /// <summary>
    /// Folds one epoch's increments into the profile. Each interval contributes its duration to the
    /// buckets it spans, and to the active tally in proportion when usage moved.
    /// </summary>
    public void Observe(IEnumerable<Increment> increments)
    {
        foreach (var inc in increments)
        {
            if (inc.Hours <= 1e-9 || inc.Duration > TimeSpan.FromHours(6)) continue;

            var active = inc.DeltaUsed > BurnRate.ActivityThresholdPercent;

            // Walk the interval hour by hour so a span crossing a bucket edge is attributed to both.
            var cursor = inc.From;
            while (cursor < inc.To)
            {
                var nextHour = NextHourBoundary(cursor);
                var segmentEnd = nextHour < inc.To ? nextHour : inc.To;
                var hours = (segmentEnd - cursor).TotalHours;
                if (hours <= 0) break;

                var bucket = BucketOf(cursor);
                _totalHours[bucket] += hours;
                if (active) _activeHours[bucket] += hours;

                cursor = segmentEnd;
            }
        }
    }

    /// <summary>
    /// Probability the user is active in <paramref name="bucket"/>, shrunk toward the global
    /// activity rate by how much evidence that bucket has.
    /// </summary>
    /// <remarks>
    /// Without shrinkage a bucket observed for ten minutes once would read as 0% or 100% active and
    /// dominate the horizon. The shrinkage constant is in hours of observation.
    /// </remarks>
    public double ActivityProbability(int bucket)
    {
        const double ShrinkageHours = 3.0;

        var globalRate = ObservedHours > 0 ? _activeHours.Sum() / ObservedHours : 0.5;
        var observed = _totalHours[bucket];
        if (observed <= 0) return globalRate;

        var bucketRate = _activeHours[bucket] / observed;
        var weight = observed / (observed + ShrinkageHours);
        return (weight * bucketRate) + ((1 - weight) * globalRate);
    }

    /// <summary>
    /// Expected active hours between <paramref name="from"/> and <paramref name="to"/>: the horizon
    /// the forecast actually projects over.
    /// </summary>
    public double ExpectedActiveHours(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0d;

        var total = 0d;
        var cursor = from;

        // Cap the walk so a month-long horizon cannot spin: beyond this we extrapolate by the
        // average weekly shape instead.
        const int MaxSteps = 24 * 40;
        var steps = 0;

        while (cursor < to && steps++ < MaxSteps)
        {
            var nextHour = NextHourBoundary(cursor);
            var segmentEnd = nextHour < to ? nextHour : to;
            var hours = (segmentEnd - cursor).TotalHours;
            if (hours <= 0) break;

            total += hours * ActivityProbability(BucketOf(cursor));
            cursor = segmentEnd;
        }

        if (cursor < to)
        {
            var globalRate = ObservedHours > 0 ? _activeHours.Sum() / ObservedHours : 0.5;
            total += (to - cursor).TotalHours * globalRate;
        }

        return total;
    }

    /// <summary>Builds a profile from a set of epochs, most recent last.</summary>
    public static IntensityProfile FromEpochs(IEnumerable<Epoch> epochs, TimeZoneInfo? zone = null)
    {
        var profile = new IntensityProfile(zone);
        foreach (var epoch in epochs) profile.Observe(epoch.Increments());
        return profile;
    }
}

using Cadence.Core.Model;

namespace Cadence.Core.Forecast;

/// <summary>One stored observation of one quota window.</summary>
public sealed record UsageSample(
    DateTimeOffset Timestamp,
    double? UsedPercent,
    DateTimeOffset? ResetsAt = null,
    long? UsedUnits = null);

/// <summary>The change between two consecutive samples inside a single epoch.</summary>
/// <remarks>
/// Increments never span an epoch boundary. A boundary is a reset, where usage falls back to zero;
/// differencing across one produces a large negative delta that is not a burn rate.
/// </remarks>
public readonly record struct Increment(DateTimeOffset From, DateTimeOffset To, double DeltaUsed)
{
    public TimeSpan Duration => To - From;

    public double Hours => Duration.TotalHours;

    /// <summary>Percent per hour over this interval, or 0 for a zero-length interval.</summary>
    public double RatePerHour => Hours > 1e-9 ? DeltaUsed / Hours : 0d;
}

/// <summary>
/// A contiguous run of samples between two resets of the same quota window.
/// </summary>
public sealed class Epoch
{
    private readonly List<UsageSample> _samples;
    private Increment[]? _increments;

    public Epoch(int id, string windowId, WindowKind kind, TimeSpan? windowLength, IEnumerable<UsageSample> samples)
    {
        Id = id;
        WindowId = windowId;
        Kind = kind;
        WindowLength = windowLength;
        _samples = [.. samples.OrderBy(s => s.Timestamp)];
    }

    public int Id { get; }

    public string WindowId { get; }

    public WindowKind Kind { get; }

    public TimeSpan? WindowLength { get; }

    public IReadOnlyList<UsageSample> Samples => _samples;

    /// <summary>The most recent sample that carried a known percentage.</summary>
    public UsageSample? Latest => _samples.LastOrDefault(s => s.UsedPercent is not null);

    /// <summary>Latest known used percentage in this epoch.</summary>
    public double? UsedPercent => Latest?.UsedPercent;

    /// <summary>The reset time last reported for this epoch.</summary>
    public DateTimeOffset? ResetsAt =>
        _samples.LastOrDefault(s => s.ResetsAt is not null)?.ResetsAt;

    /// <summary>
    /// Inferred window start: reset minus nominal length where known, otherwise the first
    /// observation we have. The fallback biases elapsed time short, which biases the burn rate
    /// high, which is the safe direction.
    /// </summary>
    public DateTimeOffset? StartedAt =>
        ResetsAt is { } r && WindowLength is { } len ? r - len
        : _samples.Count > 0 ? _samples[0].Timestamp
        : null;

    public TimeSpan Elapsed(DateTimeOffset now) =>
        StartedAt is { } s && now > s ? now - s : TimeSpan.Zero;

    /// <summary>Fraction of the window elapsed at <paramref name="now"/>, in [0,1].</summary>
    public double ElapsedFraction(DateTimeOffset now)
    {
        if (StartedAt is not { } start || ResetsAt is not { } reset) return 0d;
        var total = (reset - start).TotalSeconds;
        if (total <= 0) return 1d;
        return Math.Clamp((now - start).TotalSeconds / total, 0d, 1d);
    }

    /// <summary>
    /// Consecutive deltas between samples with known percentages.
    /// </summary>
    /// <remarks>
    /// Negative deltas are clamped to zero rather than dropped. A small negative step inside an
    /// epoch is provider rounding or a partial rollback, not a refund of quota; dropping the pair
    /// would silently shorten the observed elapsed time and inflate every rate computed from it.
    /// </remarks>
    public IReadOnlyList<Increment> Increments()
    {
        if (_increments is not null) return _increments;

        var known = _samples.Where(s => s.UsedPercent is not null).ToArray();
        var result = new List<Increment>(Math.Max(0, known.Length - 1));

        for (var i = 1; i < known.Length; i++)
        {
            var from = known[i - 1];
            var to = known[i];
            if (to.Timestamp <= from.Timestamp) continue; // guard a clock that moved backwards

            var delta = Math.Max(0d, to.UsedPercent!.Value - from.UsedPercent!.Value);
            result.Add(new Increment(from.Timestamp, to.Timestamp, delta));
        }

        return _increments = [.. result];
    }

    /// <summary>Largest interval between consecutive samples; a proxy for how much we missed.</summary>
    public TimeSpan MaxSampleGap()
    {
        var incs = Increments();
        return incs.Count == 0 ? TimeSpan.Zero : incs.Max(i => i.Duration);
    }
}

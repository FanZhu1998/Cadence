using Cadence.Core.Model;

namespace Cadence.Core.Forecast;

/// <summary>
/// Splits a flat sample series into epochs at quota resets.
/// </summary>
/// <remarks>
/// Every downstream number depends on this being right. A missed boundary joins the tail of one
/// window to the head of the next and yields a burn rate computed across a reset; a spurious
/// boundary throws away the history that makes a forecast possible.
/// </remarks>
public static class EpochDetector
{
    /// <summary>
    /// A drop of at least this many percentage points is treated as a reset rather than provider
    /// noise. Providers do round and occasionally revise a figure down by a fraction of a point.
    /// </summary>
    public const double MaterialDropPercent = 2.0;

    /// <summary>Reset timestamps jitter by a few seconds between responses; ignore that.</summary>
    public static readonly TimeSpan ResetRollForwardTolerance = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Groups <paramref name="samples"/> into epochs, oldest first. Input need not be sorted.
    /// </summary>
    public static IReadOnlyList<Epoch> Split(
        IEnumerable<UsageSample> samples,
        string windowId,
        WindowKind kind,
        TimeSpan? windowLength)
    {
        var ordered = samples.OrderBy(s => s.Timestamp).ToArray();
        if (ordered.Length == 0) return [];

        var epochs = new List<Epoch>();
        var current = new List<UsageSample> { ordered[0] };
        var epochId = 0;

        for (var i = 1; i < ordered.Length; i++)
        {
            if (IsBoundary(ordered[i - 1], ordered[i]))
            {
                epochs.Add(new Epoch(epochId++, windowId, kind, windowLength, current));
                current = [];
            }

            current.Add(ordered[i]);
        }

        if (current.Count > 0)
            epochs.Add(new Epoch(epochId, windowId, kind, windowLength, current));

        return epochs;
    }

    /// <summary>The most recent epoch, or null when there are no samples.</summary>
    public static Epoch? Current(
        IEnumerable<UsageSample> samples,
        string windowId,
        WindowKind kind,
        TimeSpan? windowLength)
        => Split(samples, windowId, kind, windowLength).LastOrDefault();

    /// <summary>True when a reset occurred between <paramref name="previous"/> and <paramref name="next"/>.</summary>
    public static bool IsBoundary(UsageSample previous, UsageSample next)
    {
        // 1. Usage fell materially. Only decisive when both percentages are known: a null is
        //    "unknown", and treating unknown as a drop to zero would fabricate a reset.
        if (previous.UsedPercent is { } before && next.UsedPercent is { } after
            && after < before - MaterialDropPercent)
        {
            return true;
        }

        if (previous.ResetsAt is { } previousReset)
        {
            // 2. The window rolled forward: the provider is now naming a later reset.
            if (next.ResetsAt is { } nextReset && nextReset > previousReset + ResetRollForwardTolerance)
                return true;

            // 3. We crossed the reset the provider previously named, so this sample belongs to the
            //    window after it, even if usage happens to look similar on both sides.
            if (next.Timestamp > previousReset + ResetRollForwardTolerance)
                return true;
        }

        return false;
    }
}

using System.Globalization;
using Cadence.Core.Model;

namespace Cadence.App.ViewModels;

/// <summary>
/// Turns a <see cref="Forecast"/> into one sentence a person can act on.
/// </summary>
/// <remarks>
/// The flyout shows one line of plain language under each bar, never a statistics readout. Nobody
/// makes a decision from "P90 = 94.2, sigma = 12.1"; they make it from "you will run out about two
/// hours before this resets". The numbers stay available in the tooltip and the history window for
/// anyone who wants them.
/// </remarks>
public static class ForecastNarrator
{
    /// <summary>The sentence shown beneath the bar. Empty when there is nothing worth saying.</summary>
    public static string Describe(Forecast? forecast, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (forecast is null) return string.Empty;

        if (forecast.Unavailable is { } reason) return DescribeUnavailable(reason);

        // Once running out is the likelier outcome, lead with when, not with a percentage.
        if (forecast.LeadWithExhaustion && forecast.ExhaustsAt is { } exhaustsAt)
        {
            var when = FormatClock(exhaustsAt, now);
            var margin = resetsAt is { } reset && reset > exhaustsAt
                ? $", about {Approximate(reset - exhaustsAt)} before it resets"
                : string.Empty;

            return $"At this pace you will hit the limit around {when}{margin}.";
        }

        if (forecast.ExhaustProbability >= 0.15)
        {
            var chance = (int)Math.Round(forecast.ExhaustProbability * 100);
            var by = resetsAt is { } reset ? $" before {FormatClock(reset, now)}" : string.Empty;

            return $"Roughly a {chance}% chance of running out{by}.";
        }

        // Comfortable: say so, and say what it rests on.
        var projected = (int)Math.Round(forecast.Median);

        if (projected <= 70)
        {
            return forecast.PaceDelta < -5
                ? $"Well inside your limit — on track for about {projected}% by the reset."
                : $"On pace to finish around {projected}%, with room to spare.";
        }

        return $"On track for about {projected}% by the reset — close, but inside the limit.";
    }

    private static string DescribeUnavailable(ForecastUnavailableReason reason) => reason switch
    {
        ForecastUnavailableReason.TooEarly => "Too early in this window to forecast.",
        ForecastUnavailableReason.UsageUnknown => "This plan does not report usage for this window.",
        ForecastUnavailableReason.NoHorizon => "No reset time, so no forecast.",
        ForecastUnavailableReason.WindowClosed => "This window has reset.",
        ForecastUnavailableReason.InsufficientCoverage => "Cadence missed too many checks to forecast honestly.",
        _ => string.Empty,
    };

    /// <summary>The compact pace token, e.g. "11pp in deficit".</summary>
    public static string PaceToken(Forecast? forecast)
    {
        if (forecast is not { IsAvailable: true } f) return string.Empty;

        var delta = Math.Abs(f.PaceDelta);
        if (delta < 1.5) return "on even pace";

        return f.InDeficit
            ? $"{delta:F0}pp ahead of even pace"
            : $"{delta:F0}pp in reserve";
    }

    /// <summary>The band, e.g. "58–94%". Empty when the forecast is unavailable.</summary>
    public static string Band(Forecast? forecast)
        => forecast is { IsAvailable: true } f
            ? string.Create(CultureInfo.InvariantCulture, $"{f.P10:F0}–{f.P90:F0}%")
            : string.Empty;

    /// <summary>Countdown text, e.g. "resets in 3h 12m".</summary>
    public static string ResetText(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset) return string.Empty;
        if (reset <= now) return "resetting…";

        var remaining = reset - now;

        // Beyond a day, a weekday and time is far easier to act on than "in 2d 16h".
        return remaining > TimeSpan.FromHours(36)
            ? $"resets {reset.ToLocalTime():ddd HH:mm}"
            : $"resets in {Approximate(remaining)}";
    }

    /// <summary>How old the displayed numbers are, when that matters.</summary>
    public static string AgeText(TimeSpan? age) => age switch
    {
        null => string.Empty,
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)age.Value.TotalMinutes}m ago",
        { TotalHours: < 24 } => $"{(int)age.Value.TotalHours}h ago",
        _ => $"{(int)age.Value.TotalDays}d ago",
    };

    /// <summary>A clock time, or a weekday and time once it is not today.</summary>
    private static string FormatClock(DateTimeOffset instant, DateTimeOffset now)
    {
        var local = instant.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    /// <summary>
    /// A rounded, spoken duration. Precision here would be false: the underlying estimate has a
    /// band measured in hours, so "2 hours" is honest where "1h 53m" is not.
    /// </summary>
    private static string Approximate(TimeSpan span) => span switch
    {
        { TotalDays: >= 2 } => $"{(int)Math.Round(span.TotalDays)} days",
        { TotalHours: >= 20 } => "a day",
        { TotalHours: >= 2 } => $"{(int)Math.Round(span.TotalHours)} hours",
        { TotalMinutes: >= 90 } => "an hour and a half",
        { TotalMinutes: >= 45 } => "an hour",
        { TotalMinutes: >= 20 } => $"{(int)Math.Round(span.TotalMinutes / 10.0) * 10} minutes",
        { TotalMinutes: >= 2 } => $"{(int)span.TotalMinutes} minutes",
        _ => "a moment",
    };
}

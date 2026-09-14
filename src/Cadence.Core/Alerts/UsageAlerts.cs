using System.Text.Json.Serialization;
using Cadence.Core.Forecast;
using Cadence.Core.Model;

namespace Cadence.Core.Alerts;

/// <summary>The usage levels worth interrupting someone for, in percent used.</summary>
public static class UsageThresholds
{
    public static readonly IReadOnlyList<double> All = [50, 75, 90, 95, 98, 100];

    /// <summary>The highest threshold at or below <paramref name="usedPercent"/>, or 0 below the first.</summary>
    public static double HighestReached(double usedPercent)
    {
        var reached = 0d;
        foreach (var threshold in All)
        {
            if (usedPercent >= threshold) reached = threshold;
        }

        return reached;
    }
}

/// <summary>One notification's worth of news about one quota window.</summary>
/// <param name="Threshold">The threshold being announced, or 0 when this is only a forecast warning.</param>
/// <param name="ExhaustsAt">Set when the forecast warning rides along with this alert.</param>
public sealed record UsageAlert(
    ProviderId Provider,
    string WindowId,
    string WindowTitle,
    double Threshold,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    DateTimeOffset? ExhaustsAt)
{
    public bool IsThreshold => Threshold > 0;

    public bool IsExhausted => Threshold >= 100;

    public bool IncludesForecast => ExhaustsAt is not null;
}

/// <summary>What has been said about one window during its current epoch.</summary>
public sealed record WindowAlertState
{
    public DateTimeOffset LastSeen { get; init; }

    public double? LastUsedPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Highest threshold announced since the window last reset, or 0 for none.</summary>
    public double Announced { get; init; }

    public bool ForecastAnnounced { get; init; }

    public DateTimeOffset? LastNotifiedAt { get; init; }
}

/// <summary>
/// Everything the notification rules need to remember, across every window and across restarts.
/// </summary>
/// <remarks>
/// Kept on disk so that restarting Cadence, or signing in again, does not repeat a warning that was
/// already given. Holding this only in memory is how a quota monitor ends up announcing "Codex at
/// 100%" on every launch.
/// </remarks>
public sealed class AlertLedger
{
    public Dictionary<string, WindowAlertState> Windows { get; init; } = [];

    /// <summary>One-off notices, such as expired credentials, keyed, with when each was last shown.</summary>
    public Dictionary<string, DateTimeOffset> Notices { get; init; } = [];

    /// <summary>When any notification was last shown, across every provider.</summary>
    public DateTimeOffset? LastShownAt { get; set; }

    /// <summary>True when something worth persisting changed since the last save.</summary>
    [JsonIgnore]
    public bool HasChanges { get; private set; }

    public static string KeyFor(ProviderId provider, string windowId) => $"{provider}|{windowId}";

    internal void MarkChanged() => HasChanges = true;

    internal void MarkSaved() => HasChanges = false;
}

public sealed record AlertOptions
{
    /// <summary>Announce the usage thresholds in <see cref="UsageThresholds.All"/>.</summary>
    public bool Thresholds { get; init; } = true;

    /// <summary>Warn once when the forecast says a window will run out before it resets.</summary>
    public bool Forecast { get; init; } = true;
}

/// <summary>
/// Decides which usage notifications are due, so that a whole working session produces a handful of
/// them rather than a stream.
/// </summary>
/// <remarks>
/// The rules, in order:
/// <list type="number">
/// <item>Each threshold is announced at most once per window until the window resets.</item>
/// <item>Usage that jumps past several thresholds at once produces one alert, for the highest.</item>
/// <item>Two alerts about the same window are at least <see cref="WindowQuietPeriod"/> apart. A
/// threshold crossed in between is not lost: it is announced when the quiet period ends, if it is
/// still the highest. Running out entirely is exempt, because it changes what the user can do.</item>
/// <item>Any two notifications are at least <see cref="GlobalQuietPeriod"/> apart. A tray balloon
/// replaces the one before it, so two in quick succession would hide the first.</item>
/// <item>The forecast warning comes at most once per window, only while usage is below
/// <see cref="ForecastCeiling"/>, and only from a forecast with more than low confidence. Above the
/// ceiling the thresholds already say it.</item>
/// </list>
/// Windows are recognised across polls by reset detection, not by their exact reset timestamp:
/// providers report that time a few seconds differently from one response to the next, and keying on
/// it made every poll look like a new window and repeated the same warning all session.
/// </remarks>
public static class UsageAlertPolicy
{
    public static readonly TimeSpan WindowQuietPeriod = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan GlobalQuietPeriod = TimeSpan.FromMinutes(2);

    /// <summary>How often a one-off notice, such as expired credentials, may repeat.</summary>
    public static readonly TimeSpan NoticeRepeat = TimeSpan.FromDays(1);

    public const double ForecastCeiling = 90;

    /// <summary>
    /// Folds a fresh snapshot into <paramref name="ledger"/> and returns the alerts due now.
    /// </summary>
    /// <remarks>
    /// Nothing is marked as announced here. Call <see cref="Commit"/> once the notification has
    /// actually been shown, so one the shell refused, or one held back while the user was presenting,
    /// is still due on the next refresh.
    /// </remarks>
    public static IReadOnlyList<UsageAlert> Evaluate(
        AlertLedger ledger, UsageSnapshot snapshot, AlertOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        var due = new List<UsageAlert>();

        foreach (var window in snapshot.Windows)
        {
            if (window.UsedPercent is not { } used) continue;

            var state = Track(ledger, AlertLedger.KeyFor(snapshot.Provider, window.Id), used, window.ResetsAt, now);

            var reached = options.Thresholds ? UsageThresholds.HighestReached(used) : 0;
            var threshold = reached > state.Announced ? reached : 0;

            DateTimeOffset? exhaustsAt = null;
            if (options.Forecast && !state.ForecastAnnounced
                && used < ForecastCeiling && state.Announced < ForecastCeiling
                && window.Forecast is { LeadWithExhaustion: true } forecast
                && forecast.Confidence is not ForecastConfidence.Low)
            {
                exhaustsAt = forecast.ExhaustsAt;
            }

            if (threshold == 0 && exhaustsAt is null) continue;

            var runningOut = threshold >= 100;
            if (!runningOut && state.LastNotifiedAt is { } last && now - last < WindowQuietPeriod) continue;

            due.Add(new UsageAlert(snapshot.Provider, window.Id, window.Title, threshold, used, window.ResetsAt, exhaustsAt));
        }

        if (due.Count > 0 && ledger.LastShownAt is { } shown && now - shown < GlobalQuietPeriod) return [];

        return due;
    }

    /// <summary>Records that <paramref name="shown"/> reached the user.</summary>
    public static void Commit(AlertLedger ledger, IEnumerable<UsageAlert> shown, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(shown);

        foreach (var alert in shown)
        {
            var key = AlertLedger.KeyFor(alert.Provider, alert.WindowId);
            if (!ledger.Windows.TryGetValue(key, out var state)) continue;

            ledger.Windows[key] = state with
            {
                Announced = Math.Max(state.Announced, alert.Threshold),
                ForecastAnnounced = state.ForecastAnnounced || alert.IncludesForecast,
                LastNotifiedAt = now,
            };
        }

        ledger.LastShownAt = now;
        ledger.MarkChanged();
    }

    /// <summary>True when the one-off notice <paramref name="key"/> may be shown now.</summary>
    public static bool NoticeDue(AlertLedger ledger, string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        if (ledger.LastShownAt is { } shown && now - shown < GlobalQuietPeriod) return false;
        return !ledger.Notices.TryGetValue(key, out var last) || now - last >= NoticeRepeat;
    }

    public static void CommitNotice(AlertLedger ledger, string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        ledger.Notices[key] = now;
        ledger.LastShownAt = now;
        ledger.MarkChanged();
    }

    /// <summary>
    /// Updates what is known about a window, starting it afresh when it has reset since last seen.
    /// </summary>
    private static WindowAlertState Track(
        AlertLedger ledger, string key, double used, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        var known = ledger.Windows.TryGetValue(key, out var existing);
        var reset = known && EpochDetector.IsBoundary(
            new UsageSample(existing!.LastSeen, existing.LastUsedPercent, existing.ResetsAt),
            new UsageSample(now, used, resetsAt));

        var basis = known && !reset ? existing! : new WindowAlertState();
        var updated = basis with { LastSeen = now, LastUsedPercent = used, ResetsAt = resetsAt ?? basis.ResetsAt };

        ledger.Windows[key] = updated;

        // Seeing usage move is not worth a disk write; a new window or a reset is, because that is
        // what has to survive a restart.
        if (!known || reset) ledger.MarkChanged();

        return updated;
    }
}

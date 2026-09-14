using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cadence.App.ViewModels;
using Cadence.Core.Alerts;
using Cadence.Core.Model;
using Cadence.Core.Refresh;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace Cadence.App.Notifications;

/// <summary>
/// Shows the notifications <see cref="UsageAlertPolicy"/> decides are due, and remembers them.
/// </summary>
/// <remarks>
/// Balloon notifications through the tray icon rather than modern toasts: <c>AppNotificationManager</c>
/// requires package identity, which an unpackaged single-file exe does not have. Balloons are less
/// pretty and work everywhere.
/// <para>
/// When to speak is the policy's decision, and it is deliberately stingy: a quota monitor that nags
/// is one the user turns off, and then it warns them about nothing at all. This class adds the parts
/// that need Windows: holding everything back while the user is presenting, composing one balloon per
/// provider, and keeping the ledger of what has been said on disk so a restart repeats nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class NotificationManager
{
    private readonly Func<NotificationSettings> _settings;
    private readonly ILogger _logger;
    private readonly AlertLedgerStore _store;
    private readonly AlertLedger _ledger;
    private readonly Lock _gate = new();

    public NotificationManager(
        Func<NotificationSettings> settings, ILogger<NotificationManager> logger, AlertLedgerStore? store = null)
    {
        _settings = settings;
        _logger = logger;
        _store = store ?? new AlertLedgerStore();
        _ledger = _store.Load();
    }

    /// <summary>The tray icon used to raise balloons. Set once the tray exists.</summary>
    public TaskbarIcon? Host { get; set; }

    /// <summary>Evaluates a fresh snapshot and shows at most one balloon for it.</summary>
    public void Evaluate(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var options = _settings();

        lock (_gate)
        {
            var due = UsageAlertPolicy.Evaluate(
                _ledger,
                snapshot,
                new AlertOptions { Thresholds = options.OnUsageThresholds, Forecast = options.OnForecastExhaustion },
                now);

            if (due.Count > 0)
            {
                // Held back rather than dropped while the user is presenting: an alert that was not
                // shown stays due, and goes out on the first refresh afterwards.
                if (!IsUserBusy() && Show(Compose(snapshot.Provider, due, now)))
                    UsageAlertPolicy.Commit(_ledger, due, now);
            }
            else if (options.OnCreditsExpiring && snapshot.Spend?.CreditsExpireAt is { } expires
                     && expires > now && expires - now < TimeSpan.FromDays(7))
            {
                ShowNotice(
                    $"{snapshot.Provider}|credits|{expires.ToString("O", CultureInfo.InvariantCulture)}",
                    new Balloon(
                        "Credits expiring",
                        $"Your {snapshot.Provider} credits expire {expires.ToLocalTime().ToString("ddd d MMM", CultureInfo.CurrentCulture)}.",
                        NotificationIcon.Info),
                    now);
            }

            if (_ledger.HasChanges) _store.Save(_ledger, now);
        }
    }

    /// <summary>Notifies that credentials need attention, at most once a day. Terminal errors only.</summary>
    public void NotifyAuthProblem(ProviderId provider, FetchError error, DateTimeOffset now)
    {
        var options = _settings();
        if (!options.OnCredentialsExpired || !error.IsAuthFailure) return;

        lock (_gate)
        {
            ShowNotice(
                $"{provider}|auth|{error.Kind}",
                new Balloon($"{provider} needs signing in again", error.Message, NotificationIcon.Warning),
                now);

            if (_ledger.HasChanges) _store.Save(_ledger, now);
        }
    }

    /// <summary>Writes what has been announced, so the next start does not repeat it.</summary>
    public void Save()
    {
        lock (_gate) _store.Save(_ledger, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The text for one alert: "92% used, and on track to run out around 15:40. Resets in 2h 10m."
    /// </summary>
    public static string Describe(UsageAlert alert, DateTimeOffset now, bool withReset)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var parts = new List<string>();
        if (alert.IsExhausted) parts.Add("limit reached");
        else if (alert.IsThreshold) parts.Add($"{alert.UsedPercent:0}% used");
        if (alert.ExhaustsAt is { } at) parts.Add($"on track to run out around {Clock(at, now)}");

        var text = Capitalise(string.Join(", and ", parts));
        var reset = withReset ? ForecastNarrator.ResetText(alert.ResetsAt, now) : string.Empty;

        return reset.Length > 0 ? $"{text}. {Capitalise(reset)}." : $"{text}.";
    }

    private readonly record struct Balloon(string Title, string Body, NotificationIcon Icon);

    /// <summary>
    /// One balloon per provider. Several windows due at once become one message with a line each,
    /// because a balloon replaces the one before it and a burst would show only the last.
    /// </summary>
    private static Balloon Compose(ProviderId provider, IReadOnlyList<UsageAlert> alerts, DateTimeOffset now)
    {
        // Halfway and three-quarters are news, not warnings.
        var icon = alerts.Any(a => a.Threshold >= 90 || a.IncludesForecast) ? NotificationIcon.Warning : NotificationIcon.Info;

        if (alerts.Count == 1)
        {
            var alert = alerts[0];
            return new Balloon($"{provider} · {alert.WindowTitle}", Describe(alert, now, withReset: true), icon);
        }

        var lines = alerts.Select(a => $"{a.WindowTitle}: {Describe(a, now, withReset: false)}");
        return new Balloon($"{provider} usage", string.Join(Environment.NewLine, lines), icon);
    }

    private void ShowNotice(string key, Balloon balloon, DateTimeOffset now)
    {
        if (!UsageAlertPolicy.NoticeDue(_ledger, key, now) || IsUserBusy()) return;
        if (Show(balloon)) UsageAlertPolicy.CommitNotice(_ledger, key, now);
    }

    private bool Show(Balloon balloon)
    {
        if (Host is not { } host) return false;

        try
        {
            host.ShowNotification(balloon.Title, balloon.Body, balloon.Icon);
            _logger.LogInformation("Notified: {Title} - {Body}", balloon.Title, balloon.Body);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The shell refuses balloons in some states. Not shown means still due, never lost.
            _logger.LogDebug(e, "Could not raise a notification");
            return false;
        }
    }

    private static string Clock(DateTimeOffset at, DateTimeOffset now)
        => at.ToLocalTime().ToString(at - now < TimeSpan.FromHours(20) ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);

    private static string Capitalise(string text)
        => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    /// <summary>
    /// True when Windows says the user should not be interrupted: presenting, on a full-screen
    /// app, in a call, or with focus assist enabled.
    /// </summary>
    private static bool IsUserBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;

            return state is UserNotificationState.PresentationMode
                or UserNotificationState.RunningFullScreen
                or UserNotificationState.BusyOrRunningDirect3D
                or UserNotificationState.QuietTime;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private enum UserNotificationState
    {
        NotPresent = 1,
        BusyOrRunningDirect3D = 2,
        RunningFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        AppSuppressed = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);
}

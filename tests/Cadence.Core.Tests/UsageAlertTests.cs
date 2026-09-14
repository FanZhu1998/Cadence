using Cadence.Core.Alerts;
using Cadence.Core.Model;

namespace Cadence.Core.Tests;

/// <summary>
/// The rules that keep usage notifications to a handful per session. Each test is one sentence of
/// the policy, because the policy is what the user experiences.
/// </summary>
public class UsageAlertTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Start.AddHours(5);
    private static readonly AlertOptions Defaults = new();

    private static UsageSnapshot Snapshot(ProviderId provider, DateTimeOffset at, params QuotaWindow[] windows)
        => new() { Provider = provider, FetchedAt = at, SourceLabel = "test", Windows = windows };

    private static QuotaWindow Window(double used, DateTimeOffset? resetsAt = null, string id = "session", Model.Forecast? forecast = null)
        => new()
        {
            Id = id,
            Title = id == "session" ? "5-hour" : "Weekly",
            Kind = id == "session" ? WindowKind.Session : WindowKind.Weekly,
            UsedPercent = used,
            ResetsAt = resetsAt ?? Reset,
            Forecast = forecast,
        };

    private static Model.Forecast RunningOut(DateTimeOffset at, ForecastConfidence confidence = ForecastConfidence.Medium)
        => new() { ExhaustProbability = 0.8, ExhaustsAt = at, Confidence = confidence };

    /// <summary>Evaluates, then commits whatever was due, as the notification manager does when a balloon shows.</summary>
    private static IReadOnlyList<UsageAlert> Notify(AlertLedger ledger, UsageSnapshot snapshot, DateTimeOffset at, AlertOptions? options = null)
    {
        var due = UsageAlertPolicy.Evaluate(ledger, snapshot, options ?? Defaults, at);
        if (due.Count > 0) UsageAlertPolicy.Commit(ledger, due, at);
        return due;
    }

    [Theory]
    [InlineData(49.9, 0)]
    [InlineData(50, 50)]
    [InlineData(74, 50)]
    [InlineData(89.5, 75)]
    [InlineData(97, 95)]
    [InlineData(99.9, 98)]
    [InlineData(100, 100)]
    public void OnlyTheListedThresholdsCount(double used, double expected)
        => Assert.Equal(expected, UsageThresholds.HighestReached(used));

    [Fact]
    public void EachThresholdIsAnnouncedOnce_AsUsageClimbs()
    {
        var ledger = new AlertLedger();
        var announced = new List<double>();
        var at = Start;

        foreach (var used in new[] { 20d, 40, 52, 60, 76, 80, 91, 93, 96, 99, 100, 100 })
        {
            at = at.AddMinutes(31);
            announced.AddRange(Notify(ledger, Snapshot(ProviderId.Claude, at, Window(used, Reset.AddHours(8))), at).Select(a => a.Threshold));
        }

        Assert.Equal(new double[] { 50, 75, 90, 95, 98, 100 }, announced);
    }

    [Fact]
    public void AJumpPastSeveralThresholds_AnnouncesOnlyTheHighest()
    {
        var ledger = new AlertLedger();

        var first = Notify(ledger, Snapshot(ProviderId.Codex, Start, Window(92)), Start);
        var later = Notify(ledger, Snapshot(ProviderId.Codex, Start.AddMinutes(40), Window(94)), Start.AddMinutes(40));

        Assert.Equal(90, Assert.Single(first).Threshold);
        Assert.Empty(later);
    }

    [Fact]
    public void AReportedResetTimeThatWobbles_IsStillTheSameWindow()
    {
        // The bug this replaces: keying on the exact reset time made every poll look like a new window.
        var ledger = new AlertLedger();

        Notify(ledger, Snapshot(ProviderId.Codex, Start, Window(100, Reset)), Start);
        var again = Notify(ledger, Snapshot(ProviderId.Codex, Start.AddMinutes(45), Window(100, Reset.AddSeconds(23))), Start.AddMinutes(45));

        Assert.Empty(again);
    }

    [Fact]
    public void AfterTheWindowResets_TheThresholdsStartOver()
    {
        var ledger = new AlertLedger();
        var nextReset = Reset.AddHours(5);

        Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(100)), Start);
        var afterReset = Reset.AddMinutes(10);
        var quiet = Notify(ledger, Snapshot(ProviderId.Claude, afterReset, Window(4, nextReset)), afterReset);
        var halfway = Notify(ledger, Snapshot(ProviderId.Claude, afterReset.AddHours(1), Window(51, nextReset)), afterReset.AddHours(1));

        Assert.Empty(quiet);
        Assert.Equal(50, Assert.Single(halfway).Threshold);
    }

    [Fact]
    public void MessagesAboutOneWindow_AreAtLeastHalfAnHourApart_AndNothingIsLost()
    {
        var ledger = new AlertLedger();

        Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(90)), Start);
        var tooSoon = Notify(ledger, Snapshot(ProviderId.Claude, Start.AddMinutes(10), Window(96)), Start.AddMinutes(10));
        var afterQuiet = Notify(ledger, Snapshot(ProviderId.Claude, Start.AddMinutes(31), Window(97)), Start.AddMinutes(31));

        Assert.Empty(tooSoon);
        Assert.Equal(95, Assert.Single(afterQuiet).Threshold);
    }

    [Fact]
    public void RunningOut_IsNeverHeldBack()
    {
        var ledger = new AlertLedger();

        Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(98)), Start);
        var exhausted = Notify(ledger, Snapshot(ProviderId.Claude, Start.AddMinutes(5), Window(100)), Start.AddMinutes(5));

        Assert.True(Assert.Single(exhausted).IsExhausted);
    }

    [Fact]
    public void TwoNotificationsNeverLandWithinTwoMinutes_EvenFromDifferentProviders()
    {
        var ledger = new AlertLedger();

        Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(90)), Start);
        var sameMoment = Notify(ledger, Snapshot(ProviderId.Codex, Start.AddSeconds(20), Window(76)), Start.AddSeconds(20));
        var nextRefresh = Notify(ledger, Snapshot(ProviderId.Codex, Start.AddMinutes(5), Window(77)), Start.AddMinutes(5));

        Assert.Empty(sameMoment);
        Assert.Equal(75, Assert.Single(nextRefresh).Threshold);
    }

    [Fact]
    public void SeveralWindowsOfOneProvider_ComeBackTogether_ForOneMessage()
    {
        var ledger = new AlertLedger();

        var due = Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(91), Window(76, Reset.AddDays(3), "weekly")), Start);

        Assert.Equal(2, due.Count);
    }

    [Fact]
    public void AnAlertThatWasNotShown_IsStillDueNextTime()
    {
        var ledger = new AlertLedger();

        var first = UsageAlertPolicy.Evaluate(ledger, Snapshot(ProviderId.Claude, Start, Window(91)), Defaults, Start);
        var retry = UsageAlertPolicy.Evaluate(ledger, Snapshot(ProviderId.Claude, Start.AddMinutes(5), Window(92)), Defaults, Start.AddMinutes(5));

        Assert.Equal(90, Assert.Single(first).Threshold);
        Assert.Equal(90, Assert.Single(retry).Threshold);
    }

    [Fact]
    public void TheForecastWarning_ComesOncePerWindow_AndOnlyBelowNinety()
    {
        var ledger = new AlertLedger();
        var runsOutAt = Start.AddHours(2);

        var early = Notify(ledger, Snapshot(ProviderId.Claude, Start, Window(30, forecast: RunningOut(runsOutAt))), Start);
        var repeat = Notify(ledger, Snapshot(ProviderId.Claude, Start.AddHours(1), Window(45, forecast: RunningOut(runsOutAt))), Start.AddHours(1));

        var other = new AlertLedger();
        var high = Notify(other, Snapshot(ProviderId.Claude, Start, Window(91, forecast: RunningOut(runsOutAt))), Start);

        Assert.True(Assert.Single(early).IncludesForecast);
        Assert.Empty(repeat);
        Assert.False(Assert.Single(high).IncludesForecast);
    }

    [Fact]
    public void ALowConfidenceForecast_NeverInterrupts()
    {
        var due = Notify(new AlertLedger(),
            Snapshot(ProviderId.Claude, Start, Window(30, forecast: RunningOut(Start.AddHours(2), ForecastConfidence.Low))), Start);

        Assert.Empty(due);
    }

    [Fact]
    public void SwitchedOff_ThresholdsStaySilent()
    {
        var due = Notify(new AlertLedger(), Snapshot(ProviderId.Claude, Start, Window(99)), Start, new AlertOptions { Thresholds = false });

        Assert.Empty(due);
    }

    [Fact]
    public void ARestart_RepeatsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadence-alerts-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AlertLedgerStore(path);
            var ledger = store.Load();
            Notify(ledger, Snapshot(ProviderId.Codex, Start, Window(100)), Start);
            Assert.True(store.Save(ledger, Start));

            var reloaded = new AlertLedgerStore(path).Load();
            var afterRestart = Notify(reloaded, Snapshot(ProviderId.Codex, Start.AddHours(1), Window(100)), Start.AddHours(1));

            Assert.Empty(afterRestart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableLedger_StartsEmptyRatherThanFailing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadence-alerts-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");

            Assert.Empty(new AlertLedgerStore(path).Load().Windows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OneOffNotices_RepeatAtMostDaily()
    {
        var ledger = new AlertLedger();

        Assert.True(UsageAlertPolicy.NoticeDue(ledger, "Claude|auth", Start));
        UsageAlertPolicy.CommitNotice(ledger, "Claude|auth", Start);

        Assert.False(UsageAlertPolicy.NoticeDue(ledger, "Claude|auth", Start.AddHours(6)));
        Assert.True(UsageAlertPolicy.NoticeDue(ledger, "Claude|auth", Start.AddDays(1)));
    }
}

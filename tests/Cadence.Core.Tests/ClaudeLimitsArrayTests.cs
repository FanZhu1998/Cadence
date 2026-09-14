using Cadence.Core.Model;
using Cadence.Core.Providers.Claude;

namespace Cadence.Core.Tests;

/// <summary>
/// Pins the payload shape captured live on 2026-09-13, which differs materially from the shape the
/// original design document describes. Structure is real; values are synthetic.
/// </summary>
public class ClaudeLimitsArrayTests
{
    private static IReadOnlyList<QuotaWindow> Windows()
        => ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-limits-array.json"));

    [Fact]
    public void TheLimitsArrayIsPreferredOverTheTopLevelKeys()
    {
        var windows = Windows();

        Assert.Contains(windows, w => w.Id == "claude.session");
        Assert.Contains(windows, w => w.Id == "claude.weekly_all");
        Assert.Contains(windows, w => w.Id == "claude.weekly_scoped.fable");
    }

    [Fact]
    public void ModelScopedWeeklyLimitIsSurfaced()
    {
        // The whole reason for preferring the limits array: seven_day_opus and seven_day_sonnet are
        // null at the top level, but a scoped weekly limit is live at 76%. Reading only the legacy
        // keys reports 57% and hides the constraint the user will actually hit first.
        var scoped = Windows().Single(w => w.Id == "claude.weekly_scoped.fable");

        Assert.Equal("Fable (7d)", scoped.Title);
        Assert.Equal(76.0, scoped.UsedPercent);
        Assert.Equal(WindowKind.Model, scoped.Kind);
    }

    [Fact]
    public void TheTightestWindowBecomesThePrimaryMetric_EvenWhenItIsModelScoped()
    {
        var snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            FetchedAt = DateTimeOffset.UtcNow,
            SourceLabel = "oauth",
            Windows = Windows(),
        };

        // The tray must show 76, not 57.
        Assert.Equal(76.0, snapshot.PrimaryWindow!.UsedPercent);
    }

    [Fact]
    public void ProviderSeverityIsCarriedThrough()
    {
        Assert.Equal(IncidentSeverity.Minor, Windows().Single(w => w.Id == "claude.weekly_scoped.fable").Severity);
        Assert.Equal(IncidentSeverity.None, Windows().Single(w => w.Id == "claude.session").Severity);
    }

    [Fact]
    public void AnActiveScopedLimitIsNotHiddenByDefault()
    {
        // is_active marks the limit currently governing the account, so it leads rather than
        // collapsing into the secondary list.
        Assert.False(Windows().Single(w => w.Id == "claude.weekly_scoped.fable").SecondaryByDefault);
    }

    [Fact]
    public void WeeklyWindowStartComesFromTheBreakdownRatherThanBeingInferred()
    {
        // A real start beats "reset minus seven days", and it feeds the forecaster's elapsed
        // fraction directly.
        var weekly = Windows().Single(w => w.Id == "claude.weekly_all");

        Assert.Equal(new DateTimeOffset(2026, 9, 9, 15, 0, 0, 995, TimeSpan.Zero).AddTicks(7960), weekly.WindowStartsAt);
        Assert.Equal(weekly.WindowStartsAt, weekly.WindowStart);
    }

    [Fact]
    public void SessionWindowStillInfersItsStartFromTheResetTime()
    {
        var session = Windows().Single(w => w.Id == "claude.session");

        Assert.Null(session.WindowStartsAt);
        Assert.Equal(session.ResetsAt - TimeSpan.FromHours(5), session.WindowStart);
    }

    [Fact]
    public void SpendIsReadFromMinorUnitsAndExponent()
    {
        // amount_minor 8941 with exponent 2 is $89.41, not $8,941.
        var spend = ClaudeUsageParser.ParseSpend(Fixtures.Root_("claude-usage-limits-array.json"));

        Assert.NotNull(spend);
        Assert.Equal(89.41m, spend!.SpentThisPeriod);
        Assert.Equal(100.00m, spend.PeriodLimit);
        Assert.Equal("USD", spend.Currency);
    }

    [Fact]
    public void SpendFormatsWithARealCurrencySymbolDespiteInvariantGlobalization()
    {
        Assert.Equal("$89.41 of $100.00", MoneyFormat.FormatSpend(89.41m, 100.00m, "USD"));
        Assert.Equal("€10.00", MoneyFormat.Format(10m, "EUR"));
        // An unknown code still reads unambiguously rather than as the generic currency sign.
        Assert.Equal("12.30 CHF", MoneyFormat.Format(12.3m, "CHF"));
    }

    [Fact]
    public void UnreleasedFeatureSlotsAreNotRenderedAsZeroPercentWindows()
    {
        // Anthropic ships unreleased features as codenamed keys returning a zero-filled object with
        // no reset. Listing those fills the flyout with meaningless 0% rows.
        var legacy = ClaudeUsageParser.ParseWindows(
            System.Text.Json.JsonDocument.Parse(
                """
                { "five_hour": { "utilization": 5.0, "resets_at": "2026-09-14T03:20:00Z" },
                  "nimbus_quill": { "utilization": 0.0, "resets_at": null },
                  "tangelo": null }
                """).RootElement);

        Assert.Single(legacy);
        Assert.Equal("claude.five_hour", legacy[0].Id);
    }

    [Fact]
    public void PercentagesAreNeverRescaledAsFractions()
    {
        // Genuine sub-1% usage early in a window must not be multiplied into 50%.
        var windows = ClaudeUsageParser.ParseWindows(
            System.Text.Json.JsonDocument.Parse(
                """{ "limits": [ { "kind": "session", "group": "session", "percent": 0.5, "resets_at": "2026-09-14T03:20:00Z" } ] }""")
                .RootElement);

        Assert.Equal(0.5, Assert.Single(windows).UsedPercent);
    }

    [Fact]
    public void PlanLabelComesFromTheOrganizationTierInTheProfilePayload()
    {
        // The usage payload carries no plan name at all on current accounts. The multiplier only
        // exists at organization.rate_limit_tier, so reading the root or `account` alone leaves
        // the plan blank in the UI.
        Assert.Equal("Max 5x", ClaudeUsageParser.ParsePlanLabel(Fixtures.Root_("claude-profile.json")));
    }

    [Fact]
    public void IdentityComesFromTheProfilePayload()
    {
        var identity = ClaudeUsageParser.ParseIdentity(Fixtures.Root_("claude-profile.json"));

        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("Max 5x", identity.PlanLabel);
        Assert.Equal("user@example.com's Organization", identity.OrgName);
    }

    [Fact]
    public void PlanLabelFallsBackToTheAccountBooleans()
    {
        // Some responses carry only the flags.
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "account": { "has_claude_max": true, "has_claude_pro": false } }""");

        Assert.Equal("Max", ClaudeUsageParser.ParsePlanLabel(document.RootElement));
    }

    [Fact]
    public void AnEmptyLimitsArrayFallsBackToTheLegacyKeys()
    {
        var windows = ClaudeUsageParser.ParseWindows(
            System.Text.Json.JsonDocument.Parse(
                """{ "limits": [], "five_hour": { "utilization": 12.0, "resets_at": "2026-09-14T03:20:00Z" } }""")
                .RootElement);

        Assert.Equal("claude.five_hour", Assert.Single(windows).Id);
    }
}

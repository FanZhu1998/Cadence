using Cadence.Core.Model;
using Cadence.Core.Providers.Claude;

namespace Cadence.Core.Tests;

public class ClaudeParserTests
{
    [Fact]
    public void Max20x_MapsEveryKnownWindow()
    {
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.Equal(
            ["claude.five_hour", "claude.seven_day", "claude.seven_day_sonnet", "claude.seven_day_opus", "claude.seven_day_routines"],
            windows.Select(w => w.Id));

        var session = windows.Single(w => w.Id == "claude.five_hour");
        Assert.Equal("Session (5h)", session.Title);
        Assert.Equal(WindowKind.Session, session.Kind);
        Assert.Equal(42.5, session.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), session.WindowLength);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero), session.ResetsAt);

        var weekly = windows.Single(w => w.Id == "claude.seven_day");
        Assert.Equal(WindowKind.Weekly, weekly.Kind);
        Assert.Equal(67.0, weekly.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), weekly.WindowLength);
    }

    [Fact]
    public void SharedLimitKeys_AreIgnoredSoUsageIsNotCountedThreeTimes()
    {
        // claude_design and omelette report against the same pool as five_hour. Counting them
        // would show the same 42.5% three times and make the tray metric meaningless.
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.DoesNotContain(windows, w => w.Id.Contains("design", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(windows, w => w.Id.Contains("omelette", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PerModelWindows_AreMarkedSecondary()
    {
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.True(windows.Single(w => w.Id == "claude.seven_day_opus").SecondaryByDefault);
        Assert.False(windows.Single(w => w.Id == "claude.five_hour").SecondaryByDefault);
    }

    [Fact]
    public void SessionAndWeekly_SortAheadOfPerModelWindows()
    {
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.Equal(WindowKind.Session, windows[0].Kind);
        Assert.Equal(WindowKind.Weekly, windows[1].Kind);
    }

    [Fact]
    public void MissingUtilization_IsUnknownRatherThanZero()
    {
        // The invariant that matters most: an education or enterprise account returns reset
        // metadata with no numbers, and showing 0% would read as "your quota is untouched".
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-unknown-utilization.json"));

        var session = windows.Single(w => w.Id == "claude.five_hour");
        Assert.Null(session.UsedPercent);
        Assert.False(session.UsageKnown);
        Assert.NotNull(session.ResetsAt);

        var weekly = windows.Single(w => w.Id == "claude.seven_day");
        Assert.Null(weekly.UsedPercent);
    }

    [Fact]
    public void Utilization_IsReadAsAPercentageAndNotRescaled()
    {
        // Confirmed against live payloads: utilization is always 0-100 (4.0, 56.0, 89.41 observed).
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-epoch-timestamps.json"));

        Assert.Equal(42.5, windows.Single(w => w.Id == "claude.five_hour").UsedPercent!.Value, 6);
    }

    [Fact]
    public void UsedAndLimit_DeriveAPercentWhenNoneIsGiven()
    {
        var weekly = ClaudeUsageParser
            .ParseWindows(Fixtures.Root_("claude-usage-epoch-timestamps.json"))
            .Single(w => w.Id == "claude.seven_day");

        Assert.Equal(67.0, weekly.UsedPercent!.Value, 6);
        Assert.Equal(335, weekly.UsedUnits);
        Assert.Equal(500, weekly.LimitUnits);
    }

    [Fact]
    public void EpochTimestamps_AreReadInBothSecondsAndMilliseconds()
    {
        var windows = ClaudeUsageParser.ParseWindows(Fixtures.Root_("claude-usage-epoch-timestamps.json"));

        // 1789012800 seconds and 1789012800000 milliseconds are the same instant.
        Assert.Equal(
            windows.Single(w => w.Id == "claude.five_hour").ResetsAt,
            windows.Single(w => w.Id == "claude.seven_day").ResetsAt);
    }

    [Fact]
    public void Spend_IsReadFromExtraUsage()
    {
        var spend = ClaudeUsageParser.ParseSpend(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.NotNull(spend);
        Assert.Equal(12.40m, spend!.SpentThisPeriod);
        Assert.Equal(50.00m, spend.PeriodLimit);
        Assert.Equal("USD", spend.Currency);
    }

    [Theory]
    [InlineData("default_claude_max_20x", "Max 20x")]
    [InlineData("default_claude_max_5x", "Max 5x")]
    [InlineData("claude_max", "Max")]
    [InlineData("pro", "Pro")]
    [InlineData("team", "Team")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("free", "Free")]
    public void PlanLabels_RenderTheMultiplierWhenThereIsOne(string raw, string expected)
        => Assert.Equal(expected, ClaudeUsageParser.PrettyPlan(raw));

    [Fact]
    public void Identity_ComesFromTheUsagePayloadWhenPresent()
    {
        var identity = ClaudeUsageParser.ParseIdentity(Fixtures.Root_("claude-usage-max20x.json"));

        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("Max 20x", identity.PlanLabel);
    }

    [Fact]
    public void PlanLabel_FallsBackToRateLimitTier()
    {
        Assert.Equal("Enterprise",
            ClaudeUsageParser.ParsePlanLabel(Fixtures.Root_("claude-usage-unknown-utilization.json")));
    }

    [Fact]
    public void AnUnrecognisedWindowIsSurfaced_NotSilentlyDropped()
    {
        // A new limit appearing is exactly what the user needs to see; dropping it would hide a
        // quota they are being held to.
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "thirty_day_experimental": { "utilization": 22.0, "resets_at": "2026-10-01T00:00:00Z" } }""");

        var windows = ClaudeUsageParser.ParseWindows(document.RootElement);

        var window = Assert.Single(windows);
        Assert.Equal("claude.thirty_day_experimental", window.Id);
        Assert.Equal(22.0, window.UsedPercent);
        Assert.True(window.SecondaryByDefault);
    }

    [Fact]
    public void MalformedInput_YieldsNoWindowsRatherThanThrowing()
    {
        using var array = System.Text.Json.JsonDocument.Parse("[1,2,3]");
        Assert.Empty(ClaudeUsageParser.ParseWindows(array.RootElement));

        using var text = System.Text.Json.JsonDocument.Parse("\"nope\"");
        Assert.Empty(ClaudeUsageParser.ParseWindows(text.RootElement));
    }

    // ---- credential reader ---------------------------------------------------------------------

    [Fact]
    public void Credentials_ParseTheNestedOauthBlob()
    {
        var token = ClaudeCredentialReader.Parse(Fixtures.Root_("claude-credentials.json"));

        Assert.NotNull(token);
        Assert.StartsWith("sk-ant-oat01-", token!.AccessToken, StringComparison.Ordinal);
        Assert.Contains("user:profile", token.Scopes);
        Assert.True(token.HasRequiredScope);
    }

    [Fact]
    public void Credentials_WithoutTheProfileScope_AreDetectedBeforeAnyRequestIsMade()
    {
        // A token holding only user:inference authenticates and then 403s. Catching it here turns
        // a confusing error into "re-authenticate Claude Code".
        var token = ClaudeCredentialReader.Parse(Fixtures.Root_("claude-credentials-noscope.json"));

        Assert.NotNull(token);
        Assert.False(token!.HasRequiredScope);
    }

    [Fact]
    public void Credentials_ExpiryIsReadFromMillisecondEpoch()
    {
        var token = ClaudeCredentialReader.Parse(Fixtures.Root_("claude-credentials.json"));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789012800000), token!.ExpiresAt);
        Assert.True(token.IsExpired(token.ExpiresAt!.Value.AddSeconds(1)));
        Assert.False(token.IsExpired(token.ExpiresAt.Value.AddSeconds(-1)));
    }

    [Fact]
    public void Credentials_MissingAccessToken_ParsesAsNull()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "claudeAiOauth": { "scopes": [] } }""");
        Assert.Null(ClaudeCredentialReader.Parse(document.RootElement));
    }

    [Fact]
    public async Task Credentials_HalfWrittenFile_ReadsAsNullRatherThanThrowing()
    {
        // Claude Code rewrites this file on its own schedule, so landing mid-write is expected.
        var path = Path.Combine(Path.GetTempPath(), $"cadence-partial-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "claudeAiOauth": { "accessToken": "sk-ant-oat01-tru""");

        try
        {
            Assert.Null(await ClaudeCredentialReader.ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Credentials_MissingFile_ReadsAsNull()
        => Assert.Null(await ClaudeCredentialReader.ReadAsync(
            Path.Combine(Path.GetTempPath(), $"cadence-absent-{Guid.NewGuid():N}.json"),
            CancellationToken.None));
}

using Cadence.Core.Model;
using Cadence.Core.Providers.Gemini;

namespace Cadence.Core.Tests;

public class GeminiParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Antigravity_MapsBothPoolsAndBothWindowLengths()
    {
        var windows = AntigravityParser.ParseWindows(Fixtures.Root_("antigravity-quota.json"), Now);

        // Two real pools, weekly and 5h each, plus one row with reset metadata but no fraction.
        Assert.Equal(4, windows.Count);

        var geminiWeekly = windows.Single(w => w.Id == "gemini.gemini_models.gemini_weekly");
        Assert.Equal(66.0, geminiWeekly.UsedPercent!.Value, 6); // 1 - 0.34
        Assert.Equal(WindowKind.Weekly, geminiWeekly.Kind);
        Assert.Equal(TimeSpan.FromDays(7), geminiWeekly.WindowLength);

        var gemini5h = windows.Single(w => w.Id == "gemini.gemini_models.gemini_5h");
        Assert.Equal(19.0, gemini5h.UsedPercent!.Value, 6); // 1 - 0.81
        Assert.Equal(WindowKind.Session, gemini5h.Kind);
    }

    [Fact]
    public void Antigravity_RemainingFractionIsInvertedIntoUsedPercent()
    {
        var byok = AntigravityParser
            .ParseWindows(Fixtures.Root_("antigravity-quota.json"), Now)
            .Single(w => w.Id == "gemini.claude_and_gpt_models.byok_weekly");

        Assert.Equal(5.0, byok.UsedPercent!.Value, 6); // 1 - 0.95
    }

    [Fact]
    public void Antigravity_RowWithResetButNoFraction_StaysVisibleAsUnknown()
    {
        // The design calls this out specifically: such rows are reset context, not 0% usage.
        var meta = AntigravityParser
            .ParseWindows(Fixtures.Root_("antigravity-quota.json"), Now)
            .Single(w => w.Id == "gemini.claude_and_gpt_models.byok_meta");

        Assert.Null(meta.UsedPercent);
        Assert.False(meta.UsageKnown);
        Assert.NotNull(meta.ResetsAt);
    }

    [Fact]
    public void Antigravity_TitlesCarryBothPoolAndBucket()
    {
        var window = AntigravityParser
            .ParseWindows(Fixtures.Root_("antigravity-quota.json"), Now)
            .Single(w => w.Id == "gemini.gemini_models.gemini_weekly");

        Assert.Contains("Gemini Models", window.Title, StringComparison.Ordinal);
        Assert.Contains("Weekly", window.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Antigravity_FlatRemainingFractionFormIsAlsoAccepted()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "groups": [ { "displayName": "G", "buckets": [ { "bucketId": "b", "remainingFraction": 0.25, "resetTime": "2026-09-20T00:00:00Z" } ] } ] }""");

        var window = Assert.Single(AntigravityParser.ParseWindows(document.RootElement, Now));
        Assert.Equal(75.0, window.UsedPercent!.Value, 6);
    }

    [Fact]
    public void Antigravity_MalformedInputYieldsNoWindows()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Empty(AntigravityParser.ParseWindows(document.RootElement, Now));
    }

    // ---- Code Assist -----------------------------------------------------------------------------

    [Fact]
    public void CodeAssist_TakesTheLowestRemainingFractionPerModel()
    {
        // Two buckets for gemini-2.5-pro (0.62 and 0.44). The tighter one is the limit the user
        // actually hits, so 0.44 -> 56% used.
        var windows = GeminiCodeAssistParser.ParseWindows(Fixtures.Root_("gemini-codeassist-quota.json"), Now);

        var pro = windows.Single(w => w.Id == "gemini.codeassist.gemini_2_5_pro");
        Assert.Equal(56.0, pro.UsedPercent!.Value, 6);
    }

    [Fact]
    public void CodeAssist_ProIsPrimaryAndFlashIsSecondary()
    {
        var windows = GeminiCodeAssistParser.ParseWindows(Fixtures.Root_("gemini-codeassist-quota.json"), Now);

        Assert.False(windows.Single(w => w.Title.Contains("pro", StringComparison.OrdinalIgnoreCase)).SecondaryByDefault);
        Assert.True(windows.Single(w => w.Title.Contains("flash", StringComparison.OrdinalIgnoreCase)).SecondaryByDefault);
    }

    [Fact]
    public void CodeAssist_MalformedInputYieldsNoWindows()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "other": 1 }""");
        Assert.Empty(GeminiCodeAssistParser.ParseWindows(document.RootElement, Now));
    }

    // ---- credentials -----------------------------------------------------------------------------

    [Fact]
    public void GeminiCredentials_ParseAccessTokenAndExpiry()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "access_token": "fake", "refresh_token": "fake-r", "expiry_date": 1789012800000 }""");

        var token = GeminiCredentialReader.Parse(document.RootElement);

        Assert.NotNull(token);
        Assert.Equal("fake", token!.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789012800000), token.ExpiresAt);
    }

    [Fact]
    public void GeminiCredentials_WithoutAnAccessTokenParseAsNull()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "refresh_token": "x" }""");
        Assert.Null(GeminiCredentialReader.Parse(document.RootElement));
    }
}

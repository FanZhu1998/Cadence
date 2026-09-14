using Cadence.Core.Model;
using Cadence.Core.Providers.Codex;

namespace Cadence.Core.Tests;

public class CodexParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrimaryAndSecondaryWindows_MapToSessionAndWeekly()
    {
        var windows = CodexUsageParser.ParseWindows(Fixtures.Root_("codex-wham-usage.json"), Now);

        var session = windows.Single(w => w.Id == "codex.primary_window");
        Assert.Equal(WindowKind.Session, session.Kind);
        Assert.Equal(38.2, session.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(300), session.WindowLength);

        var weekly = windows.Single(w => w.Id == "codex.secondary_window");
        Assert.Equal(WindowKind.Weekly, weekly.Kind);
        Assert.Equal(71.9, weekly.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(10080), weekly.WindowLength);
    }

    [Fact]
    public void RelativeResets_AreConvertedToInstantsUsingTheFetchTime()
    {
        // Codex sends "resets_in_seconds" far more often than a timestamp, so the parser has to be
        // given the fetch time; getting this wrong silently shifts every countdown.
        var windows = CodexUsageParser.ParseWindows(Fixtures.Root_("codex-wham-usage.json"), Now);

        Assert.Equal(Now.AddSeconds(7200), windows.Single(w => w.Id == "codex.primary_window").ResetsAt);
        Assert.Equal(Now.AddSeconds(259200), windows.Single(w => w.Id == "codex.secondary_window").ResetsAt);
    }

    [Fact]
    public void AdditionalRateLimits_BecomeNamedExtraWindowsWithStableIds()
    {
        var windows = CodexUsageParser.ParseWindows(Fixtures.Root_("codex-wham-usage.json"), Now);

        var spark = windows.Single(w => w.Kind == WindowKind.Extra);
        Assert.Equal("codex.extra.codex_spark_5h", spark.Id);
        Assert.Contains("Codex Spark", spark.Title, StringComparison.Ordinal);
        Assert.Equal(10.0, spark.UsedPercent);
        Assert.True(spark.SecondaryByDefault);
    }

    [Fact]
    public void PlanLabelAndCredits_AreRead()
    {
        var root = Fixtures.Root_("codex-wham-usage.json");

        Assert.Equal("Pro", CodexUsageParser.ParsePlanLabel(root));

        var credits = CodexUsageParser.ParseCredits(root);
        Assert.NotNull(credits);
        Assert.Equal(42.50m, credits!.CreditBalance);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), credits.CreditsExpireAt);
    }

    [Fact]
    public void RemainingPercent_IsInvertedIntoUsedPercent()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "rate_limit": { "primary_window": { "remaining_percent": 30.0, "resets_in_seconds": 60 } } }""");

        var window = Assert.Single(CodexUsageParser.ParseWindows(document.RootElement, Now));
        Assert.Equal(70.0, window.UsedPercent);
    }

    [Fact]
    public void MissingUsedPercent_IsUnknownRatherThanZero()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "rate_limit": { "primary_window": { "resets_in_seconds": 60 } } }""");

        var window = Assert.Single(CodexUsageParser.ParseWindows(document.RootElement, Now));
        Assert.Null(window.UsedPercent);
        Assert.False(window.UsageKnown);
    }

    [Fact]
    public void TopLevelWindows_AreFoundWhenThereIsNoRateLimitWrapper()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{ "primary_window": { "used_percent": 5.0, "resets_in_seconds": 60 } }""");

        Assert.Single(CodexUsageParser.ParseWindows(document.RootElement, Now));
    }

    [Fact]
    public void MalformedInput_YieldsNoWindows()
    {
        using var document = System.Text.Json.JsonDocument.Parse("[]");
        Assert.Empty(CodexUsageParser.ParseWindows(document.RootElement, Now));
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("pro", "Pro")]
    [InlineData("team", "Team")]
    [InlineData("business", "Team")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("free", "Free")]
    public void PlanNames_AreTidied(string raw, string expected)
        => Assert.Equal(expected, CodexUsageParser.PrettyPlan(raw));

    // ---- auth.json -------------------------------------------------------------------------------

    [Fact]
    public void AuthFile_ParsesTokensAndRefreshTime()
    {
        var auth = CodexCredentialReader.Parse(Fixtures.Root_("codex-auth.json"));

        Assert.NotNull(auth);
        Assert.Equal("fake-access-token-for-tests", auth!.AccessToken);
        Assert.Equal("acct_test_123", auth.AccountId);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 9, 5, 6, 13, TimeSpan.Zero).AddTicks(9503787),
            auth.LastRefresh);
    }

    [Fact]
    public void AuthFile_StalenessIsMeasuredFromLastRefresh()
    {
        var auth = CodexCredentialReader.Parse(Fixtures.Root_("codex-auth.json"))!;

        Assert.False(auth.IsStale(auth.LastRefresh!.Value.AddDays(7)));
        Assert.True(auth.IsStale(auth.LastRefresh.Value.AddDays(9)));
    }

    [Fact]
    public void AuthFile_WithoutAnAccessToken_ParsesAsNull()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "tokens": { "account_id": "x" } }""");
        Assert.Null(CodexCredentialReader.Parse(document.RootElement));
    }

    [Fact]
    public void CredentialStoreSetting_DefaultsToFileWhenTheKeyIsAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadence-cfg-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "model = \"gpt-6-astra\"\n[features]\njs_repl = false\n");

        try
        {
            Assert.Equal(CodexCredentialStore.File, CodexCredentialReader.ReadStoreSetting(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CredentialStoreSetting_DetectsKeyring()
    {
        // When Codex uses the keyring, auth.json is absent or stale. Reading it blindly would
        // either report a working setup as broken or quietly use an old token.
        var path = Path.Combine(Path.GetTempPath(), $"cadence-cfg-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "cli_auth_credentials_store = \"keyring\"\n");

        try
        {
            Assert.Equal(CodexCredentialStore.Keyring, CodexCredentialReader.ReadStoreSetting(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CredentialStoreSetting_UnparseableConfigFallsBackToFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cadence-cfg-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, "this is not [valid toml = ==\n");

        try
        {
            Assert.Equal(CodexCredentialStore.File, CodexCredentialReader.ReadStoreSetting(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- JWT claims ------------------------------------------------------------------------------

    [Fact]
    public void JwtClaims_ReadEmailAndPlanFromAnUnsignedPayload()
    {
        // Built here rather than committed: a real id_token in the repo would be a live credential.
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            email = "user@example.com",
            chatgpt_plan_type = "pro",
        });

        var jwt = $"{Base64Url("{\"alg\":\"none\"}")}.{Base64Url(payload)}.signature";

        var (email, plan) = JwtClaims.Read(jwt);

        Assert.Equal("user@example.com", email);
        Assert.Equal("pro", plan);
    }

    [Fact]
    public void JwtClaims_ReadFromANamespacedClaimObject()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = "team" },
        });

        var jwt = $"{Base64Url("{}")}.{Base64Url(payload)}.sig";

        Assert.Equal("team", JwtClaims.Read(jwt).PlanType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!notbase64!!!.c")]
    public void JwtClaims_MalformedInputYieldsNoClaims(string? jwt)
    {
        var (email, plan) = JwtClaims.Read(jwt);
        Assert.Null(email);
        Assert.Null(plan);
    }

    private static string Base64Url(string json)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

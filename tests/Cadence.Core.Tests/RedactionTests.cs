using Cadence.Core.Diagnostics;

namespace Cadence.Core.Tests;

/// <summary>
/// Cadence reads the user's AI credentials, so a token reaching a log file on disk is the worst
/// bug this app could have. Each token shape gets its own test.
/// </summary>
public class RedactionTests
{
    [Theory]
    [InlineData("sk-ant-oat01-AbCdEf0123456789xyz")]
    [InlineData("sk-ant-ort01-AbCdEf0123456789xyz")]
    [InlineData("sk-ant-admin01-AbCdEf0123456789xyz")]
    [InlineData("sk-ant-api03-AbCdEf0123456789xyz")]
    [InlineData("sk-proj-AbCdEf0123456789xyz")]
    [InlineData("sk-AbCdEf0123456789xyzQwErTy")]
    [InlineData("ya29.a0AfB_by-LONG-GOOGLE-TOKEN-VALUE")]
    public void EveryTokenShapeIsScrubbed(string secret)
    {
        var scrubbed = Redaction.Scrub($"about to call the api with {secret} as the credential");

        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redaction.Placeholder, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationHeadersAreScrubbed()
    {
        var scrubbed = Redaction.Scrub("Authorization: Bearer eyJhbGciOi.someopaquevalue.signature");

        Assert.DoesNotContain("someopaquevalue", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Bearer [redacted]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void CookieHeadersAndSessionKeysAreScrubbed()
    {
        Assert.DoesNotContain("abc123",
            Redaction.Scrub("Cookie: sessionKey=abc123; other=value"), StringComparison.Ordinal);

        Assert.DoesNotContain("abc123",
            Redaction.Scrub("the sessionKey=abc123 was used"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("csrf_token")]
    public void JsonCredentialFieldsAreScrubbedByName(string field)
    {
        // Catches secrets that do not match a known prefix, which is most of them.
        var json = $$"""{ "{{field}}": "opaque-secret-value-here", "keep": "visible" }""";

        var scrubbed = Redaction.Scrub(json);

        Assert.DoesNotContain("opaque-secret-value-here", scrubbed, StringComparison.Ordinal);
        Assert.Contains("visible", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void BareJwtsAreScrubbed()
    {
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r";

        Assert.DoesNotContain("eyJzdWIiOiIxMjM0", Redaction.Scrub($"id_token was {Jwt}"), StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLinesWithCsrfTokensAreScrubbed()
    {
        var scrubbed = Redaction.ScrubCommandLine(
            "language_server.exe --app_data_dir antigravity --csrf_token SECRETVALUE --extension_server_port 4321");

        Assert.DoesNotContain("SECRETVALUE", scrubbed, StringComparison.Ordinal);
        // The port is not a secret and is genuinely useful when diagnosing discovery.
        Assert.Contains("4321", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string Text = "Session (5h) is 42% used and resets in 3h 12m; projected 71% at reset.";
        Assert.Equal(Text, Redaction.Scrub(Text));
    }

    [Fact]
    public void NullAndEmptyAreSafe()
    {
        Assert.Equal(string.Empty, Redaction.Scrub(null));
        Assert.Equal(string.Empty, Redaction.Scrub(string.Empty));
    }

    [Fact]
    public void FingerprintKeepsAShortPrefixForIdentificationOnly()
    {
        const string Secret = "sk-ant-oat01-AbCdEf0123456789xyzLONGERTOKEN";

        var fingerprint = Redaction.Fingerprint(Secret);

        // Enough to tell two tokens apart in a log, never enough to be useful to anyone. The
        // prefix is capped at a quarter of the secret's length so short secrets leak less.
        Assert.StartsWith("sk-ant-oat", fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("LONGERTOKEN", fingerprint, StringComparison.Ordinal);

        var revealed = fingerprint[..fingerprint.IndexOf('…', StringComparison.Ordinal)];
        Assert.True(revealed.Length <= Secret.Length / 4, $"revealed {revealed.Length} of {Secret.Length} chars");
    }

    [Fact]
    public void FingerprintOfAShortSecretRevealsAlmostNothing()
    {
        Assert.Equal(Redaction.Placeholder, Redaction.Fingerprint("abc"));
    }

    [Fact]
    public void FingerprintOfNothingIsJustThePlaceholder()
    {
        Assert.Equal(Redaction.Placeholder, Redaction.Fingerprint(null));
        Assert.Equal(Redaction.Placeholder, Redaction.Fingerprint("   "));
    }

    [Fact]
    public void ARealisticCredentialFileIsFullyScrubbed()
    {
        var json = Fixtures.ReadText("claude-credentials.json");

        var scrubbed = Redaction.Scrub(json);

        Assert.DoesNotContain("FAKE-TOKEN-FOR-TESTS", scrubbed, StringComparison.Ordinal);
        // Non-secret structure survives, which is what makes a diagnostics bundle useful.
        Assert.Contains("scopes", scrubbed, StringComparison.Ordinal);
        Assert.Contains("user:profile", scrubbed, StringComparison.Ordinal);
    }
}

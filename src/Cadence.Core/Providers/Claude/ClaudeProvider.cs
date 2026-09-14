using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.Model;

namespace Cadence.Core.Providers.Claude;

/// <summary>Endpoints and headers for Claude's OAuth usage surface.</summary>
internal static class ClaudeApi
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

    /// <summary>Required by the OAuth surface; without it the endpoint 404s.</summary>
    public const string BetaHeader = "oauth-2025-04-20";

    public static HttpRequestMessage Get(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        return request;
    }
}

/// <summary>Shared response handling for whichever Claude strategy supplied the token.</summary>
internal static class ClaudeFetch
{
    public static async Task<StrategyResult> UsingTokenAsync(
        string accessToken,
        string sourceLabel,
        FetchContext ctx,
        AccountIdentity? knownIdentity,
        CancellationToken ct)
    {
        using var request = ClaudeApi.Get(ClaudeApi.UsageUrl, accessToken);
        using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // Diagnostics are opt-in and redacted; a generous cap is what makes an endpoint change
        // readable in one glance rather than needing a second run.
        ctx.Diagnostics?.Response(sourceLabel, (int)response.StatusCode, Truncate(Redaction.Scrub(body), 4000));

        if (!response.IsSuccessStatusCode)
            return StrategyResult.Fail(ClassifyHttp(response.StatusCode, body, response.Headers.RetryAfter));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            return StrategyResult.Fail(FetchErrorKind.EndpointChanged,
                "Claude returned a response Cadence could not read.", e.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var windows = ClaudeUsageParser.ParseWindows(root);

            if (windows.Count == 0)
            {
                // 200 with nothing mappable. Education and enterprise accounts legitimately return
                // a subscription notice and no numbers, which is not the same as a broken endpoint.
                var notice = root.ValueKind is JsonValueKind.Object &&
                             root.TryGetProperty("subscription_notice", out _);

                return StrategyResult.Fail(
                    notice ? FetchErrorKind.TierDeprecated : FetchErrorKind.EndpointChanged,
                    notice
                        ? "This Claude plan does not report per-window usage."
                        : "Claude returned no usage windows Cadence recognises.",
                    Truncate(Redaction.Scrub(body), 300));
            }

            var identity = knownIdentity ?? ClaudeUsageParser.ParseIdentity(root);
            if (identity.PlanLabel is null && ClaudeUsageParser.ParsePlanLabel(root) is { } plan)
                identity = identity with { PlanLabel = plan };

            // The usage payload carries no plan name on current accounts, so the profile endpoint
            // supplies it. Best-effort and never fatal: a missing plan label costs one line of
            // chrome, where a failed fetch would cost the numbers.
            if (identity.PlanLabel is null)
                identity = await AddProfileAsync(accessToken, sourceLabel, ctx, identity, ct).ConfigureAwait(false);

            return StrategyResult.Ok(new UsageSnapshot
            {
                Provider = ProviderId.Claude,
                FetchedAt = ctx.Now,
                SourceLabel = sourceLabel,
                Identity = identity,
                Windows = windows,
                Spend = ClaudeUsageParser.ParseSpend(root),
            });
        }
    }

    /// <summary>Fills in plan and email from <c>/api/oauth/profile</c>, if it answers.</summary>
    private static async Task<AccountIdentity> AddProfileAsync(
        string accessToken, string sourceLabel, FetchContext ctx, AccountIdentity identity, CancellationToken ct)
    {
        try
        {
            using var request = ClaudeApi.Get(ClaudeApi.ProfileUrl, accessToken);
            using var response = await ctx.Http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return identity;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ctx.Diagnostics?.Response($"{sourceLabel}:profile", (int)response.StatusCode,
                Truncate(Redaction.Scrub(body), 1000));

            using var document = JsonDocument.Parse(body);
            var profile = ClaudeUsageParser.ParseIdentity(document.RootElement);

            return identity with
            {
                Email = identity.Email ?? profile.Email,
                PlanLabel = identity.PlanLabel ?? profile.PlanLabel,
                OrgName = identity.OrgName ?? profile.OrgName,
            };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return identity;
        }
    }

    /// <summary>
    /// Turns an HTTP failure into a typed error. The scope case matters most: a 403 from a token
    /// missing <c>user:profile</c> is fixed by re-authenticating, not by retrying.
    /// </summary>
    internal static FetchError ClassifyHttp(HttpStatusCode status, string body, RetryConditionHeaderValue? retryAfter)
    {
        var detail = Truncate(Redaction.Scrub(body), 300);

        return status switch
        {
            HttpStatusCode.Unauthorized => new FetchError(
                FetchErrorKind.TokenExpired, "Claude rejected the saved token. Sign in to Claude Code again.", detail),

            HttpStatusCode.Forbidden when LooksLikeScopeProblem(body) => new FetchError(
                FetchErrorKind.ScopeMissing,
                "This Claude token cannot read usage. Re-authenticate Claude Code to grant the user:profile scope.",
                detail),

            HttpStatusCode.Forbidden => new FetchError(
                FetchErrorKind.ScopeMissing, "Claude refused the usage request for this account.", detail),

            HttpStatusCode.TooManyRequests => new FetchError(
                FetchErrorKind.RateLimited, "Claude is rate-limiting usage requests.", detail,
                retryAfter?.Delta ?? TimeSpan.FromMinutes(1)),

            HttpStatusCode.NotFound => new FetchError(
                FetchErrorKind.EndpointChanged, "Claude's usage endpoint moved.", detail),

            >= HttpStatusCode.InternalServerError => new FetchError(
                FetchErrorKind.ProviderUnavailable, "Claude's API is having trouble.", detail),

            _ => new FetchError(FetchErrorKind.Unknown, $"Claude returned {(int)status}.", detail),
        };
    }

    private static bool LooksLikeScopeProblem(string body)
        => body.Contains("scope", StringComparison.OrdinalIgnoreCase)
           || body.Contains("user:profile", StringComparison.OrdinalIgnoreCase)
           || body.Contains("insufficient", StringComparison.OrdinalIgnoreCase);

    internal static string Truncate(string text, int max = 500)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Primary source: the OAuth token Claude Code already wrote to disk.</summary>
public sealed class ClaudeOAuthStrategy(string? credentialPath = null, string? metadataPath = null) : IUsageStrategy
{
    private readonly string _credentialPath = credentialPath ?? KnownPaths.ClaudeCredentials;
    private readonly string _metadataPath = metadataPath ?? KnownPaths.ClaudeAccountMetadata;

    public string Label => "oauth";

    public string DisplayName => "Claude Code sign-in";

    public string Location => _credentialPath;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(File.Exists(_credentialPath));

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var token = await ClaudeCredentialReader.ReadAsync(_credentialPath, ct).ConfigureAwait(false);

        if (token is null)
        {
            return StrategyResult.Fail(FetchErrorKind.NotLoggedIn,
                "No Claude Code sign-in found. Run Claude Code and sign in.", _credentialPath);
        }

        ctx.Diagnostics?.Step(Label, $"read token from {_credentialPath}, scopes=[{string.Join(' ', token.Scopes)}]");

        // Catch the scope problem before spending a request on a guaranteed 403.
        if (!token.HasRequiredScope)
        {
            return StrategyResult.Fail(FetchErrorKind.ScopeMissing,
                $"The saved Claude token lacks the {ClaudeOAuthToken.RequiredScope} scope. Re-authenticate Claude Code.",
                $"scopes: {string.Join(' ', token.Scopes)}");
        }

        if (token.IsExpired(ctx.Now))
        {
            // Cadence deliberately does not refresh this token itself: Claude Code owns the file
            // and races over it are a real bug class. The CLI refreshes on its next run.
            return StrategyResult.Fail(FetchErrorKind.TokenExpired,
                "The Claude Code token has expired. Run Claude Code once to refresh it.",
                $"expired {token.ExpiresAt:u}");
        }

        var (email, org) = await ClaudeAccountMetadataReader.ReadAsync(_metadataPath, ct).ConfigureAwait(false);
        var identity = email is null && org is null ? null : new AccountIdentity(email, null, org);

        return await ClaudeFetch.UsingTokenAsync(token.AccessToken, Label, ctx, identity, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Fallback: a token the user pasted into Settings, DPAPI-protected at rest.
/// </summary>
/// <remarks>
/// Also the multi-account mechanism: point a second Cadence profile at a different token.
/// </remarks>
public sealed class ClaudeManualTokenStrategy(ISecretStore secrets) : IUsageStrategy
{
    public string Label => "manual";

    public string DisplayName => "Pasted token";

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var protectedToken = ctx.Settings.ManualTokenProtected;
        if (string.IsNullOrWhiteSpace(protectedToken))
            return StrategyResult.Fail(FetchErrorKind.NotLoggedIn, "No Claude token has been pasted in Settings.");

        var token = secrets.Unprotect(protectedToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return StrategyResult.Fail(FetchErrorKind.TokenExpired,
                "The saved Claude token could not be decrypted. Paste it again.",
                "DPAPI blobs do not travel between Windows accounts or machines.");
        }

        return await ClaudeFetch.UsingTokenAsync(token, Label, ctx, null, ct).ConfigureAwait(false);
    }
}

public sealed class ClaudeProvider : ChainedProvider
{
    public ClaudeProvider(ISecretStore secrets, string? credentialPath = null, string? metadataPath = null)
        : base([new ClaudeOAuthStrategy(credentialPath, metadataPath), new ClaudeManualTokenStrategy(secrets)])
    {
    }

    public override ProviderId Id => ProviderId.Claude;

    public override ProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId.Claude,
        DisplayName = "Claude",
        ShortName = "CLD",
        AccentHex = "#D97757",
        SourceChain = ["oauth", "manual"],
    };

    protected override string? DescribeLocation(IUsageStrategy strategy)
        => strategy is ClaudeOAuthStrategy oauth ? oauth.Location : null;
}

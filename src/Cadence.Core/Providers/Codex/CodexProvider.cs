using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.Model;

namespace Cadence.Core.Providers.Codex;

internal static class CodexApi
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    public static HttpRequestMessage Get(string url, string accessToken, string? accountId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(accountId))
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        return request;
    }
}

/// <summary>Primary source: the OAuth tokens the Codex CLI wrote to <c>auth.json</c>.</summary>
public sealed class CodexAuthFileStrategy(string? authPath = null, string? configPath = null) : IUsageStrategy
{
    private readonly string _authPath = authPath ?? KnownPaths.CodexAuth;
    private readonly string _configPath = configPath ?? KnownPaths.CodexConfig;

    public string Label => "auth-file";

    public string DisplayName => "Codex CLI sign-in";

    public string Location => _authPath;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(File.Exists(_authPath));

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var store = CodexCredentialReader.ReadStoreSetting(_configPath);
        ctx.Diagnostics?.Step(Label, $"credential store = {store}");

        var auth = await CodexCredentialReader.ReadAsync(_authPath, ct).ConfigureAwait(false);

        if (auth is null)
        {
            // Distinguish "you use the keyring, which Cadence cannot read from here" from
            // "you are not signed in". Only the first has a one-line fix.
            return store is CodexCredentialStore.Keyring
                ? StrategyResult.Fail(FetchErrorKind.NotLoggedIn,
                    "Codex is storing credentials in Windows Credential Manager, which Cadence cannot read directly. " +
                    "Set cli_auth_credentials_store = \"file\" in config.toml and sign in again, or use the local app-server source.",
                    _configPath)
                : StrategyResult.Fail(FetchErrorKind.NotLoggedIn,
                    "No Codex sign-in found. Run codex and sign in.", _authPath);
        }

        if (auth.IsStale(ctx.Now))
            ctx.Diagnostics?.Step(Label, $"token last refreshed {auth.LastRefresh:u}; may be stale");

        return await FetchWithTokenAsync(auth, Label, ctx, ct).ConfigureAwait(false);
    }

    internal static async Task<StrategyResult> FetchWithTokenAsync(
        CodexAuth auth, string label, FetchContext ctx, CancellationToken ct)
    {
        using var request = CodexApi.Get(CodexApi.UsageUrl, auth.AccessToken, auth.AccountId);
        using var response = await ctx.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ctx.Diagnostics?.Response(label, (int)response.StatusCode, ClaudeTruncate(Redaction.Scrub(body)));

        if (!response.IsSuccessStatusCode)
            return StrategyResult.Fail(Classify(response.StatusCode, body, response.Headers.RetryAfter));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            return StrategyResult.Fail(FetchErrorKind.EndpointChanged,
                "Codex returned a response Cadence could not read.", e.Message);
        }

        using (document)
        {
            var windows = CodexUsageParser.ParseWindows(document.RootElement, ctx.Now);

            if (windows.Count == 0)
            {
                return StrategyResult.Fail(FetchErrorKind.EndpointChanged,
                    "Codex returned no rate-limit windows Cadence recognises.",
                    ClaudeTruncate(Redaction.Scrub(body), 300));
            }

            var plan = CodexUsageParser.ParsePlanLabel(document.RootElement)
                       ?? (auth.PlanType is { } p ? CodexUsageParser.PrettyPlan(p) : null);

            return StrategyResult.Ok(new UsageSnapshot
            {
                Provider = ProviderId.Codex,
                FetchedAt = ctx.Now,
                SourceLabel = label,
                Identity = new AccountIdentity(auth.Email, plan),
                Windows = windows,
                Spend = CodexUsageParser.ParseCredits(document.RootElement),
            });
        }
    }

    private static string ClaudeTruncate(string text, int max = 500)
        => text.Length <= max ? text : text[..max] + "…";

    private static FetchError Classify(HttpStatusCode status, string body, RetryConditionHeaderValue? retryAfter)
    {
        var detail = ClaudeTruncate(Redaction.Scrub(body), 300);

        return status switch
        {
            HttpStatusCode.Unauthorized => new FetchError(
                FetchErrorKind.TokenExpired, "Codex rejected the saved token. Run codex to sign in again.", detail),

            HttpStatusCode.Forbidden => new FetchError(
                FetchErrorKind.ScopeMissing, "Codex refused the usage request for this account.", detail),

            HttpStatusCode.TooManyRequests => new FetchError(
                FetchErrorKind.RateLimited, "Codex is rate-limiting usage requests.", detail,
                retryAfter?.Delta ?? TimeSpan.FromMinutes(1)),

            HttpStatusCode.NotFound => new FetchError(
                FetchErrorKind.EndpointChanged, "Codex's usage endpoint moved.", detail),

            >= HttpStatusCode.InternalServerError => new FetchError(
                FetchErrorKind.ProviderUnavailable, "ChatGPT's backend is having trouble.", detail),

            _ => new FetchError(FetchErrorKind.Unknown, $"Codex returned {(int)status}.", detail),
        };
    }
}

/// <summary>
/// Fallback: ask the Codex CLI itself over its JSON-RPC app-server.
/// </summary>
/// <remarks>
/// Strictly better than screen-scraping a PTY, and it works when credentials live in the keyring.
/// Every call is bounded and the child is killed on timeout, because a hung subprocess that is
/// never reaped wedges the refresh loop permanently.
/// </remarks>
public sealed class CodexAppServerStrategy(string? binaryPath = null) : IUsageStrategy
{
    private readonly string? _explicitBinary = binaryPath;

    /// <summary>Suppress relaunch attempts for this long after a launch failure (AV, SmartScreen).</summary>
    private static readonly TimeSpan LaunchFailureCooldown = TimeSpan.FromMinutes(30);

    private DateTimeOffset _launchBlockedUntil = DateTimeOffset.MinValue;

    public string Label => "app-server";

    public string DisplayName => "Local Codex app-server";

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(ResolveBinary() is not null);

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        if (ctx.Now < _launchBlockedUntil)
        {
            return StrategyResult.Fail(FetchErrorKind.NotRunning,
                "Skipping the Codex app-server after a recent launch failure.",
                $"retrying after {_launchBlockedUntil:t}");
        }

        var binary = ResolveBinary();
        if (binary is null)
            return StrategyResult.Fail(FetchErrorKind.NotRunning, "The codex executable was not found on this machine.");

        ctx.Diagnostics?.Step(Label, $"launching {binary}");

        CodexAppServerClient.Result rpc;
        try
        {
            rpc = await CodexAppServerClient.QueryAsync(binary, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _launchBlockedUntil = ctx.Now + LaunchFailureCooldown;
            return StrategyResult.Fail(FetchErrorKind.NotRunning,
                "Could not start the Codex app-server.", e.Message);
        }

        if (rpc.Error is { } error)
            return StrategyResult.Fail(FetchErrorKind.NotRunning, "The Codex app-server did not answer.", error);

        using var document = JsonDocument.Parse(rpc.RateLimitsJson!);
        var windows = CodexUsageParser.ParseWindows(document.RootElement, ctx.Now);

        if (windows.Count == 0)
        {
            return StrategyResult.Fail(FetchErrorKind.EndpointChanged,
                "The Codex app-server returned no rate-limit windows Cadence recognises.");
        }

        return StrategyResult.Ok(new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            FetchedAt = ctx.Now,
            SourceLabel = Label,
            Identity = new AccountIdentity(rpc.Email, rpc.PlanType is { } p ? CodexUsageParser.PrettyPlan(p) : null),
            Windows = windows,
        });
    }

    private string? ResolveBinary()
    {
        if (_explicitBinary is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;

        foreach (var candidate in KnownPaths.CodexBinaryCandidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath("codex.exe") ?? FindOnPath("codex.cmd");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }
}

public sealed class CodexProvider : ChainedProvider
{
    public CodexProvider(string? authPath = null, string? configPath = null, string? binaryPath = null)
        : base([new CodexAuthFileStrategy(authPath, configPath), new CodexAppServerStrategy(binaryPath)])
    {
    }

    public override ProviderId Id => ProviderId.Codex;

    public override ProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId.Codex,
        DisplayName = "Codex",
        ShortName = "DEX",
        AccentHex = "#10A37F",
        SourceChain = ["auth-file", "app-server"],
    };

    protected override string? DescribeLocation(IUsageStrategy strategy)
        => strategy is CodexAuthFileStrategy file ? file.Location : null;
}

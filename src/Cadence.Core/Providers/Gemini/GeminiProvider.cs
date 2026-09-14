using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.Model;

namespace Cadence.Core.Providers.Gemini;

/// <summary>The three ways Cadence can read Gemini usage. See the README for why there are three.</summary>
public static class GeminiModes
{
    /// <summary>Query the local Antigravity language server. The consumer path since June 2026.</summary>
    public const string Antigravity = "antigravity";

    /// <summary>Gemini Code Assist Standard/Enterprise, which still serves the OAuth quota API.</summary>
    public const string CodeAssist = "code-assist";

    /// <summary>API key plus local accounting. Sanctioned, unglamorous, durable.</summary>
    public const string ApiKey = "api-key";
}

/// <summary>
/// An <see cref="HttpClient"/> that will accept a self-signed certificate, but only from loopback.
/// </summary>
/// <remarks>
/// The Antigravity language server presents a self-signed certificate. Accepting that is fine for
/// 127.0.0.1, where the TLS identity check adds nothing over the OS socket boundary; installing a
/// global permissive handler would disable certificate validation for every provider call in the
/// process. The predicate below therefore refuses anything that is not loopback, even if a
/// misconfiguration routes it here.
/// </remarks>
public static class LoopbackHttp
{
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (sender, _, _, _) =>
                {
                    var host = sender switch
                    {
                        HttpRequestMessage request => request.RequestUri?.Host,
                        string name => name,
                        _ => null,
                    };

                    return host is not null
                           && IPAddress.TryParse(host, out var address)
                           && IPAddress.IsLoopback(address);
                },
            },
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}

/// <summary>Mode A: query the local Antigravity language server. No Google credentials involved.</summary>
public sealed class AntigravityStrategy(Func<HttpClient>? httpFactory = null) : IUsageStrategy
{
    private readonly Func<HttpClient> _httpFactory = httpFactory ?? LoopbackHttp.Create;

    /// <summary>Endpoint override for tests, which point this at a local fake.</summary>
    public AntigravityEndpoint? EndpointOverride { get; init; }

    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";

    private static readonly string[] Methods =
    [
        "RetrieveUserQuotaSummary", "GetUserStatus", "GetCommandModelConfigs",
    ];

    public string Label => GeminiModes.Antigravity;

    public string DisplayName => "Antigravity (local)";

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(EndpointOverride is not null || (OperatingSystem.IsWindows() && AntigravityDiscovery.Discover().Count > 0));

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        var endpoint = EndpointOverride;

        if (endpoint is null)
        {
            if (!OperatingSystem.IsWindows())
                return StrategyResult.Fail(FetchErrorKind.NotRunning, "Antigravity discovery requires Windows.");

            endpoint = AntigravityDiscovery.Discover().FirstOrDefault();
        }

        if (endpoint is null)
        {
            // Deliberately not launching it: spawning a PTY-dependent CLI in the background is a
            // reliable way to earn an antivirus flag. The UI offers a launch button instead.
            return StrategyResult.Fail(FetchErrorKind.NotRunning,
                "Antigravity is not running. Start Antigravity, then refresh.");
        }

        using var http = _httpFactory();

        FetchError? lastError = null;

        foreach (var method in Methods)
        {
            ct.ThrowIfCancellationRequested();

            var url = new Uri(endpoint.BaseUri, ServicePath + method);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            if (endpoint.CsrfToken is { Length: > 0 } csrf)
                request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);

            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                ctx.Diagnostics?.Response($"{Label}:{method}", (int)response.StatusCode,
                    Redaction.Scrub(body.Length > 500 ? body[..500] + "…" : body));

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new FetchError(FetchErrorKind.EndpointChanged,
                        $"Antigravity returned {(int)response.StatusCode} for {method}.");
                    continue;
                }

                using var document = JsonDocument.Parse(body);
                var windows = AntigravityParser.ParseWindows(document.RootElement, ctx.Now);
                if (windows.Count == 0)
                {
                    lastError = new FetchError(FetchErrorKind.EndpointChanged,
                        $"Antigravity's {method} returned no quota buckets Cadence recognises.");
                    continue;
                }

                return StrategyResult.Ok(new UsageSnapshot
                {
                    Provider = ProviderId.Gemini,
                    FetchedAt = ctx.Now,
                    SourceLabel = Label,
                    Identity = AntigravityParser.ParseIdentity(document.RootElement),
                    Windows = windows,
                });
            }
            catch (HttpRequestException e)
            {
                lastError = new FetchError(FetchErrorKind.NotRunning,
                    "Could not reach the Antigravity language server.", e.Message);
            }
            catch (JsonException e)
            {
                lastError = new FetchError(FetchErrorKind.EndpointChanged,
                    $"Antigravity's {method} response could not be read.", e.Message);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new FetchError(FetchErrorKind.Network, "The Antigravity language server timed out.");
            }
        }

        return StrategyResult.Fail(lastError ?? new FetchError(FetchErrorKind.NotRunning, "Antigravity did not answer."));
    }
}

/// <summary>
/// Mode B: Gemini Code Assist Standard/Enterprise.
/// </summary>
/// <remarks>
/// Gated behind an explicit setting. Google closed this path to individuals, Google AI Pro and
/// Ultra on 18 June 2026, and has said that third-party use of Gemini CLI OAuth credentials may
/// trigger account restrictions. Offering it by default would quietly put consumer users at risk,
/// so the user has to state that they hold a Standard or Enterprise subscription.
/// </remarks>
public sealed class GeminiCodeAssistStrategy(string? credentialsPath = null) : IUsageStrategy
{
    private readonly string _credentialsPath = credentialsPath ?? KnownPaths.GeminiOAuthCredentials;

    public const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    public const string RetrieveUserQuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";

    public string Label => GeminiModes.CodeAssist;

    public string DisplayName => "Gemini Code Assist (Standard/Enterprise)";

    public string Location => _credentialsPath;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(File.Exists(_credentialsPath));

    public async Task<StrategyResult> FetchAsync(FetchContext ctx, CancellationToken ct)
    {
        if (!string.Equals(ctx.Settings.Mode, GeminiModes.CodeAssist, StringComparison.OrdinalIgnoreCase))
        {
            return StrategyResult.Fail(FetchErrorKind.NotLoggedIn,
                "Code Assist mode is off. Enable it in Settings if you have a Standard or Enterprise subscription.");
        }

        var token = await GeminiCredentialReader.ReadAsync(_credentialsPath, ct).ConfigureAwait(false);
        if (token is null)
            return StrategyResult.Fail(FetchErrorKind.NotLoggedIn, "No Gemini sign-in found.", _credentialsPath);

        if (token.IsExpired(ctx.Now))
        {
            return StrategyResult.Fail(FetchErrorKind.TokenExpired,
                "The Gemini credential has expired. Run the Gemini CLI to refresh it.");
        }

        var tier = await PostAsync(ctx, token.AccessToken, LoadCodeAssistUrl, "{}", ct).ConfigureAwait(false);
        if (tier.Error is { } tierError) return StrategyResult.Fail(tierError);

        string? projectId;
        using (var document = JsonDocument.Parse(tier.Body!))
        {
            if (LooksIneligible(tier.Body!))
            {
                return StrategyResult.Fail(FetchErrorKind.TierDeprecated,
                    "Google no longer serves Gemini Code Assist to this account type. Use Antigravity mode instead.",
                    "Consumer, Google AI Pro and Ultra accounts lost this path on 2026-06-18.");
            }

            projectId = document.RootElement.TryGetProperty("cloudaicompanionProject", out var project)
                        && project.ValueKind is JsonValueKind.String
                ? project.GetString()
                : null;
        }

        var payload = projectId is null
            ? "{}"
            : JsonSerializer.Serialize(new { project = projectId });

        var quota = await PostAsync(ctx, token.AccessToken, RetrieveUserQuotaUrl, payload, ct).ConfigureAwait(false);
        if (quota.Error is { } quotaError) return StrategyResult.Fail(quotaError);

        using (var document = JsonDocument.Parse(quota.Body!))
        {
            var windows = GeminiCodeAssistParser.ParseWindows(document.RootElement, ctx.Now);
            if (windows.Count == 0)
            {
                return StrategyResult.Fail(FetchErrorKind.EndpointChanged,
                    "Code Assist returned no quota buckets Cadence recognises.");
            }

            return StrategyResult.Ok(new UsageSnapshot
            {
                Provider = ProviderId.Gemini,
                FetchedAt = ctx.Now,
                SourceLabel = Label,
                Identity = new AccountIdentity(token.Email, "Code Assist", projectId),
                Windows = windows,
            });
        }
    }

    private static bool LooksIneligible(string body)
        => body.Contains("UNSUPPORTED_CLIENT", StringComparison.OrdinalIgnoreCase)
           || body.Contains("IneligibleTier", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string? Body, FetchError? Error)> PostAsync(
        FetchContext ctx, string accessToken, string url, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await ctx.Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        ctx.Diagnostics?.Response(GeminiModes.CodeAssist, (int)response.StatusCode,
            Redaction.Scrub(body.Length > 500 ? body[..500] + "…" : body));

        if (response.IsSuccessStatusCode) return (body, null);

        if (LooksIneligible(body))
        {
            return (null, new FetchError(FetchErrorKind.TierDeprecated,
                "Google no longer serves Gemini Code Assist to this account type. Use Antigravity mode instead."));
        }

        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new FetchError(FetchErrorKind.TokenExpired, "Google rejected the credential."),
            HttpStatusCode.Forbidden => new FetchError(FetchErrorKind.ScopeMissing, "Google refused the quota request."),
            HttpStatusCode.TooManyRequests => new FetchError(FetchErrorKind.RateLimited, "Google is rate-limiting quota requests.",
                null, response.Headers.RetryAfter?.Delta),
            >= HttpStatusCode.InternalServerError => new FetchError(FetchErrorKind.ProviderUnavailable, "Google's API is having trouble."),
            _ => new FetchError(FetchErrorKind.Unknown, $"Google returned {(int)response.StatusCode}."),
        };

        return (null, error);
    }
}

public sealed class GeminiProvider : ChainedProvider
{
    public GeminiProvider(AntigravityEndpoint? antigravityOverride = null, string? codeAssistCredentials = null)
        : base([
            new AntigravityStrategy { EndpointOverride = antigravityOverride },
            new GeminiCodeAssistStrategy(codeAssistCredentials),
        ])
    {
    }

    public override ProviderId Id => ProviderId.Gemini;

    public override ProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId.Gemini,
        DisplayName = "Gemini",
        ShortName = "GEM",
        AccentHex = "#4285F4",
        SourceChain = [GeminiModes.Antigravity, GeminiModes.CodeAssist],
    };

    protected override string? DescribeLocation(IUsageStrategy strategy)
        => strategy is GeminiCodeAssistStrategy assist ? assist.Location : null;
}

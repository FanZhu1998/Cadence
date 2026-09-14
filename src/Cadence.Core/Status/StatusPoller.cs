using System.Text.Json;
using Cadence.Core.Model;
using Microsoft.Extensions.Logging;

namespace Cadence.Core.Status;

/// <summary>
/// Polls the providers' public status feeds.
/// </summary>
/// <remarks>
/// Cheap and high value: when usage stops updating, the first question is whether the provider is
/// down. These are documented public endpoints, unlike everything else Cadence reads, so they can
/// be polled on a slow fixed schedule without any of the care the usage endpoints need.
/// </remarks>
public sealed class StatusPoller(HttpClient http, ILogger<StatusPoller> logger)
{
    /// <summary>Incidents do not appear and clear by the minute.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<ProviderId, string> Feeds = new()
    {
        [ProviderId.Claude] = "https://status.anthropic.com/api/v2/summary.json",
        [ProviderId.Codex] = "https://status.openai.com/api/v2/summary.json",
        [ProviderId.Gemini] = "https://status.cloud.google.com/incidents.json",
    };

    public async Task<IReadOnlyDictionary<ProviderId, ProviderStatus>> PollAsync(CancellationToken ct = default)
    {
        var results = new Dictionary<ProviderId, ProviderStatus>();

        foreach (var (provider, url) in Feeds)
        {
            try
            {
                results[provider] = await PollOneAsync(provider, url, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A status feed being unreachable is not itself an incident worth showing.
                logger.LogDebug(e, "Status feed for {Provider} unavailable", provider);
            }
        }

        return results;
    }

    private async Task<ProviderStatus> PollOneAsync(ProviderId provider, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return ProviderStatus.Operational;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);

        return provider is ProviderId.Gemini
            ? ParseGoogleIncidents(document.RootElement)
            : ParseStatuspageSummary(document.RootElement);
    }

    /// <summary>Statuspage's summary.json, used by both Anthropic and OpenAI.</summary>
    internal static ProviderStatus ParseStatuspageSummary(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return ProviderStatus.Operational;

        if (!root.TryGetProperty("status", out var status) || status.ValueKind is not JsonValueKind.Object)
            return ProviderStatus.Operational;

        var indicator = status.TryGetProperty("indicator", out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : "none";

        var description = status.TryGetProperty("description", out var d) && d.ValueKind is JsonValueKind.String
            ? d.GetString()
            : null;

        var severity = indicator switch
        {
            "none" => IncidentSeverity.None,
            "minor" => IncidentSeverity.Minor,
            "major" => IncidentSeverity.Major,
            "critical" => IncidentSeverity.Critical,
            _ => IncidentSeverity.None,
        };

        return severity is IncidentSeverity.None
            ? ProviderStatus.Operational
            : new ProviderStatus(severity, description);
    }

    /// <summary>
    /// Google Cloud's incidents.json, which is a flat array of incidents rather than a summary.
    /// </summary>
    /// <remarks>
    /// The feed covers all of Google Cloud, so it is filtered to entries that look AI-related and
    /// are still open. Without the filter every unrelated regional networking incident would light
    /// up the Gemini badge.
    /// </remarks>
    internal static ProviderStatus ParseGoogleIncidents(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Array) return ProviderStatus.Operational;

        var worst = IncidentSeverity.None;
        string? description = null;

        foreach (var incident in root.EnumerateArray())
        {
            if (incident.ValueKind is not JsonValueKind.Object) continue;

            // "end" present means resolved.
            if (incident.TryGetProperty("end", out var end) && end.ValueKind is JsonValueKind.String) continue;

            if (!MentionsAi(incident)) continue;

            var severityText = incident.TryGetProperty("severity", out var s) && s.ValueKind is JsonValueKind.String
                ? s.GetString()
                : null;

            var severity = severityText switch
            {
                "high" => IncidentSeverity.Critical,
                "medium" => IncidentSeverity.Major,
                "low" => IncidentSeverity.Minor,
                _ => IncidentSeverity.Minor,
            };

            if (severity > worst)
            {
                worst = severity;
                description = incident.TryGetProperty("external_desc", out var desc) &&
                              desc.ValueKind is JsonValueKind.String
                    ? desc.GetString()
                    : null;
            }
        }

        return worst is IncidentSeverity.None ? ProviderStatus.Operational : new ProviderStatus(worst, description);
    }

    private static bool MentionsAi(JsonElement incident)
    {
        if (incident.TryGetProperty("affected_products", out var products) &&
            products.ValueKind is JsonValueKind.Array)
        {
            foreach (var product in products.EnumerateArray())
            {
                if (product.ValueKind is not JsonValueKind.Object) continue;
                if (product.TryGetProperty("title", out var title) && title.ValueKind is JsonValueKind.String &&
                    LooksAiRelated(title.GetString()))
                {
                    return true;
                }
            }
        }

        return incident.TryGetProperty("external_desc", out var desc) &&
               desc.ValueKind is JsonValueKind.String && LooksAiRelated(desc.GetString());
    }

    private static bool LooksAiRelated(string? text)
        => text is not null &&
           (text.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Vertex", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Generative", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Code Assist", StringComparison.OrdinalIgnoreCase));
}

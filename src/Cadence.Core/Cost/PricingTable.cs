using System.Text.Json;
using System.Text.Json.Serialization;
using Cadence.Core.Credentials;

namespace Cadence.Core.Cost;

/// <summary>Per-million-token prices for one model.</summary>
public sealed record ModelPricing
{
    [JsonPropertyName("input")] public decimal InputPerMillion { get; init; }

    [JsonPropertyName("output")] public decimal OutputPerMillion { get; init; }

    [JsonPropertyName("cacheRead")] public decimal CacheReadPerMillion { get; init; }

    /// <summary>Cache write with the short TTL.</summary>
    [JsonPropertyName("cacheWrite5m")] public decimal CacheWrite5mPerMillion { get; init; }

    /// <summary>
    /// Cache write with the 1-hour TTL, which is priced above the 5-minute one.
    /// </summary>
    /// <remarks>
    /// A single "cache write" price is wrong: real Claude transcripts split cache creation into
    /// <c>ephemeral_5m_input_tokens</c> and <c>ephemeral_1h_input_tokens</c>, and the two bill
    /// differently. Falls back to the 5m price when a table does not distinguish them.
    /// </remarks>
    [JsonPropertyName("cacheWrite1h")] public decimal? CacheWrite1hPerMillion { get; init; }

    public decimal EffectiveCacheWrite1h => CacheWrite1hPerMillion ?? CacheWrite5mPerMillion;
}

/// <summary>The pricing document, as shipped and as overridden by the user.</summary>
public sealed record PricingDocument
{
    [JsonPropertyName("lastUpdated")] public string? LastUpdated { get; init; }

    [JsonPropertyName("note")] public string? Note { get; init; }

    [JsonPropertyName("models")]
    public Dictionary<string, ModelPricing> Models { get; init; } = [];
}

/// <summary>
/// Model prices, loaded from JSON rather than compiled in.
/// </summary>
/// <remarks>
/// Prices change without warning and a user should never have to wait for a Cadence release to
/// correct one. The shipped table is a starting point; anything in the user's override file wins,
/// per model, so they can fix one price without restating the rest.
/// </remarks>
public sealed class PricingTable
{
    private readonly Dictionary<string, ModelPricing> _models;

    private PricingTable(Dictionary<string, ModelPricing> models, string? lastUpdated)
    {
        _models = models;
        LastUpdated = lastUpdated;
    }

    /// <summary>Date the shipped table was last checked, shown next to cost figures.</summary>
    public string? LastUpdated { get; }

    public int ModelCount => _models.Count;

    /// <summary>Where a user override is read from.</summary>
    public static string OverridePath => Path.Combine(KnownPaths.ConfigDirectory, "pricing.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PricingTable Load(string? builtInJson = null, string? overridePath = null)
    {
        var models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        string? lastUpdated = null;

        var builtIn = Parse(builtInJson ?? DefaultPricingJson.Content);
        if (builtIn is not null)
        {
            lastUpdated = builtIn.LastUpdated;
            foreach (var (model, pricing) in builtIn.Models) models[model] = pricing;
        }

        var path = overridePath ?? OverridePath;
        if (File.Exists(path))
        {
            try
            {
                var userTable = Parse(File.ReadAllText(path));
                if (userTable is not null)
                {
                    // Per-model override, so fixing one price does not require restating the table.
                    foreach (var (model, pricing) in userTable.Models) models[model] = pricing;
                    lastUpdated = userTable.LastUpdated ?? lastUpdated;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken override must not take costs offline; the shipped table still works.
            }
        }

        return new PricingTable(models, lastUpdated);
    }

    private static PricingDocument? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PricingDocument>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds pricing for a model id, falling back to the longest matching prefix.
    /// </summary>
    /// <remarks>
    /// Model ids carry dated suffixes (<c>claude-opus-5-20260814</c>) that the table does not list
    /// individually, so an exact-match-only lookup would silently price most real traffic at zero.
    /// </remarks>
    public ModelPricing? For(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (_models.TryGetValue(model, out var exact)) return exact;

        ModelPricing? best = null;
        var bestLength = 0;

        foreach (var (key, pricing) in _models)
        {
            if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase) && key.Length > bestLength)
            {
                best = pricing;
                bestLength = key.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Costs one entry. Returns 0 for an unknown model rather than guessing.
    /// </summary>
    public decimal CostOf(CostEntry entry)
    {
        if (For(entry.Model) is not { } pricing) return 0m;

        const decimal Million = 1_000_000m;

        return ((entry.InputTokens * pricing.InputPerMillion)
                + (entry.OutputTokens * pricing.OutputPerMillion)
                + (entry.CacheReadTokens * pricing.CacheReadPerMillion)
                + (entry.CacheWrite5mTokens * pricing.CacheWrite5mPerMillion)
                + (entry.CacheWrite1hTokens * pricing.EffectiveCacheWrite1h))
               / Million;
    }

    /// <summary>True when the model has no price, so the UI can mark the figure as partial.</summary>
    public bool IsUnpriced(string model) => For(model) is null;
}

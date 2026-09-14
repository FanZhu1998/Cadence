using System.Reflection;

namespace Cadence.Core.Cost;

/// <summary>The pricing table shipped inside the assembly.</summary>
/// <remarks>
/// Embedded rather than written to disk at install time so a corrupted or deleted user file always
/// has something correct to fall back to. The user's override lives beside config.json.
/// </remarks>
internal static class DefaultPricingJson
{
    private const string ResourceName = "Cadence.Core.Cost.pricing.json";

    private static readonly Lazy<string> Lazy = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return "{\"models\":{}}";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Content => Lazy.Value;
}

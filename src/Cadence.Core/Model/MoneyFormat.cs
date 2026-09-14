using System.Globalization;

namespace Cadence.Core.Model;

/// <summary>
/// Formats money without depending on the culture data.
/// </summary>
/// <remarks>
/// Cadence builds with <c>InvariantGlobalization</c>, which keeps the single-file binary small but
/// means the standard <c>:C</c> format renders the generic currency sign <c>¤</c> rather than a
/// real symbol. Providers report an explicit currency code, so the symbol is chosen from that.
/// </remarks>
public static class MoneyFormat
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",
        ["EUR"] = "€",
        ["GBP"] = "£",
        ["JPY"] = "¥",
        ["CAD"] = "CA$",
        ["AUD"] = "A$",
        ["INR"] = "₹",
    };

    /// <summary>Renders an amount, e.g. <c>$89.41</c>, or <c>12.30 CHF</c> for unknown codes.</summary>
    public static string Format(decimal amount, string currency = "USD")
    {
        var digits = currency.Equals("JPY", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
        var value = amount.ToString($"N{digits}", CultureInfo.InvariantCulture);

        return Symbols.TryGetValue(currency, out var symbol)
            ? $"{symbol}{value}"
            : $"{value} {currency.ToUpperInvariant()}";
    }

    /// <summary>Renders "spent of limit", omitting the limit when there is none.</summary>
    public static string FormatSpend(decimal? spent, decimal? limit, string currency = "USD") => (spent, limit) switch
    {
        ({ } s, { } l) => $"{Format(s, currency)} of {Format(l, currency)}",
        ({ } s, null) => Format(s, currency),
        (null, { } l) => $"limit {Format(l, currency)}",
        _ => "—",
    };
}

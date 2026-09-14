using System.Globalization;
using System.Text.Json;
using Cadence.Core.Model;

namespace Cadence.Core.Providers.Codex;

/// <summary>
/// Maps <c>backend-api/wham/usage</c> onto quota windows.
/// </summary>
/// <remarks>
/// Codex expresses resets as a <em>relative</em> "seconds from now" far more often than as a
/// timestamp, so the parser needs the fetch time to turn them into instants. Written defensively
/// for the same reason as the Claude parser: this endpoint is private and unversioned.
/// </remarks>
public static class CodexUsageParser
{
    private static readonly string[] PercentFields =
    [
        "used_percent", "usedPercent", "utilization", "percent_used", "usage_percent",
    ];

    private static readonly string[] WindowMinuteFields = ["window_minutes", "windowMinutes", "window_size_minutes"];

    private static readonly string[] ResetSecondFields =
    [
        "resets_in_seconds", "resetsInSeconds", "reset_after_seconds", "seconds_until_reset",
    ];

    private static readonly string[] ResetTimestampFields = ["resets_at", "resetsAt", "reset_at", "reset_time"];

    /// <summary>Parses the usage payload into ordered windows.</summary>
    public static IReadOnlyList<QuotaWindow> ParseWindows(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind is not JsonValueKind.Object) return [];

        var limits = FindRateLimits(root);
        if (limits.ValueKind is not JsonValueKind.Object) return [];

        var windows = new List<QuotaWindow>();

        AddNamed(limits, "primary_window", "primary", "Session (5h)", WindowKind.Session,
            TimeSpan.FromHours(5), false, now, windows);

        AddNamed(limits, "secondary_window", "secondary", "Weekly (7d)", WindowKind.Weekly,
            TimeSpan.FromDays(7), false, now, windows);

        // Named extras, e.g. Codex Spark 5-hour / weekly. Stable ids so the user can hide them.
        foreach (var container in new[] { "additional_rate_limits", "additionalRateLimits", "additional" })
        {
            if (!limits.TryGetProperty(container, out var extras)) continue;

            if (extras.ValueKind is JsonValueKind.Array)
            {
                var index = 0;
                foreach (var extra in extras.EnumerateArray())
                    AddExtra(extra, $"extra_{index++}", now, windows);
            }
            else if (extras.ValueKind is JsonValueKind.Object)
            {
                foreach (var property in extras.EnumerateObject())
                    AddExtra(property.Value, property.Name, now, windows);
            }
        }

        return windows;
    }

    /// <summary>The rate-limit block, wherever this build of the endpoint puts it.</summary>
    private static JsonElement FindRateLimits(JsonElement root)
    {
        foreach (var name in new[] { "rate_limit", "rate_limits", "rateLimit", "rateLimits" })
        {
            if (root.TryGetProperty(name, out var found) && found.ValueKind is JsonValueKind.Object)
                return found;
        }

        // Some responses put the windows at the top level.
        foreach (var name in new[] { "primary_window", "primary" })
        {
            if (root.TryGetProperty(name, out _)) return root;
        }

        return default;
    }

    private static void AddNamed(
        JsonElement limits, string primaryName, string altName, string title, WindowKind kind,
        TimeSpan defaultLength, bool secondary, DateTimeOffset now, List<QuotaWindow> into)
    {
        if (!limits.TryGetProperty(primaryName, out var window) || window.ValueKind is not JsonValueKind.Object)
        {
            if (!limits.TryGetProperty(altName, out window) || window.ValueKind is not JsonValueKind.Object)
                return;
        }

        var length = ReadWindowLength(window) ?? defaultLength;

        into.Add(new QuotaWindow
        {
            Id = $"codex.{primaryName}",
            Title = TitleFor(title, length),
            Kind = kind,
            UsedPercent = ReadPercent(window),
            ResetsAt = ReadReset(window, now),
            WindowLength = length,
            SecondaryByDefault = secondary,
        });
    }

    private static void AddExtra(JsonElement window, string key, DateTimeOffset now, List<QuotaWindow> into)
    {
        if (window.ValueKind is not JsonValueKind.Object) return;

        var length = ReadWindowLength(window) ?? TimeSpan.FromHours(5);
        var name = ReadString(window, ["name", "display_name", "displayName", "label", "id"]) ?? Humanise(key);
        var slug = ReadString(window, ["id", "slug"]) ?? key;

        into.Add(new QuotaWindow
        {
            Id = $"codex.extra.{Slug(slug)}",
            Title = TitleFor(name, length),
            Kind = WindowKind.Extra,
            UsedPercent = ReadPercent(window),
            ResetsAt = ReadReset(window, now),
            WindowLength = length,
            SecondaryByDefault = true,
        });
    }

    /// <summary>Appends a duration hint to a title when the name does not already carry one.</summary>
    private static string TitleFor(string name, TimeSpan length)
    {
        if (name.Contains('(') || name.Contains('h') && name.Any(char.IsDigit)) return name;

        var suffix = length.TotalDays >= 1
            ? $"{length.TotalDays:0.#}d"
            : $"{length.TotalHours:0.#}h";

        return $"{name} ({suffix})";
    }

    public static string? ParsePlanLabel(JsonElement root)
    {
        var plan = ReadString(root, ["plan_type", "planType", "plan"])
                   ?? ReadNested(root, "account", ["plan_type", "planType", "plan"])
                   ?? ReadNested(root, "user", ["plan_type", "planType", "plan"]);

        return plan is null ? null : PrettyPlan(plan);
    }

    internal static string PrettyPlan(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();

        return value switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            "team" or "business" => "Team",
            "enterprise" or "enterprise_managed" => "Enterprise",
            "edu" => "Edu",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ')),
        };
    }

    /// <summary>Reads the credit balance block, when the account has one.</summary>
    public static MoneySnapshot? ParseCredits(JsonElement root)
    {
        JsonElement credits = default;
        foreach (var name in new[] { "credits", "credit_balance", "balance" })
        {
            if (root.TryGetProperty(name, out var found))
            {
                credits = found;
                break;
            }
        }

        if (credits.ValueKind is JsonValueKind.Number && credits.TryGetDecimal(out var flat))
            return new MoneySnapshot { CreditBalance = flat };

        if (credits.ValueKind is not JsonValueKind.Object) return null;

        var balance = ReadDecimal(credits, ["balance", "remaining", "available", "amount", "total_granted"]);
        DateTimeOffset? expires = null;
        foreach (var field in new[] { "expires_at", "expiresAt", "expiry" })
        {
            if (credits.TryGetProperty(field, out var value) &&
                Claude.ClaudeUsageParser.TryReadTimestamp(value, out var parsed))
            {
                expires = parsed;
                break;
            }
        }

        if (balance is null && expires is null) return null;

        return new MoneySnapshot { CreditBalance = balance, CreditsExpireAt = expires };
    }

    // ---- field readers -------------------------------------------------------------------------

    private static double? ReadPercent(JsonElement obj)
    {
        foreach (var field in PercentFields)
        {
            if (!obj.TryGetProperty(field, out var value)) continue;

            if (value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var d))
                return Math.Clamp(d <= 1.0 && d > 0 ? d * 100 : d, 0, 100);

            if (value.ValueKind is JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return Math.Clamp(parsed, 0, 100);

            if (value.ValueKind is JsonValueKind.Null) return null;
        }

        // Some builds report remaining rather than used.
        foreach (var field in new[] { "remaining_percent", "percent_remaining" })
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var remaining))
            {
                return Math.Clamp(100 - remaining, 0, 100);
            }
        }

        return null;
    }

    private static TimeSpan? ReadWindowLength(JsonElement obj)
    {
        foreach (var field in WindowMinuteFields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var minutes) && minutes > 0)
            {
                return TimeSpan.FromMinutes(minutes);
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadReset(JsonElement obj, DateTimeOffset now)
    {
        // Relative form first: it is what this endpoint usually sends.
        foreach (var field in ResetSecondFields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds >= 0)
            {
                return now.AddSeconds(seconds);
            }
        }

        foreach (var field in ResetTimestampFields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                Claude.ClaudeUsageParser.TryReadTimestamp(value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string[] fields)
    {
        if (obj.ValueKind is not JsonValueKind.Object) return null;

        foreach (var field in fields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNested(JsonElement root, string container, string[] fields)
        => root.ValueKind is JsonValueKind.Object && root.TryGetProperty(container, out var nested)
            ? ReadString(nested, fields)
            : null;

    private static decimal? ReadDecimal(JsonElement obj, string[] fields)
    {
        foreach (var field in fields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDecimal(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string Slug(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static string Humanise(string key)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
}

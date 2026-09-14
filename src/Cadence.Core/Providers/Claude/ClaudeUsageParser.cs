using System.Globalization;
using System.Text.Json;
using Cadence.Core.Model;

namespace Cadence.Core.Providers.Claude;

/// <summary>
/// Maps the <c>/api/oauth/usage</c> payload onto <see cref="QuotaWindow"/>s.
/// </summary>
/// <remarks>
/// This endpoint is private and undocumented, so the parser is written to bend rather than break:
/// every field is looked up under several plausible names, an unrecognised window is surfaced
/// instead of dropped, and a missing utilisation becomes <c>null</c> (unknown) rather than zero.
/// The whole file is pure so golden fixtures can pin its behaviour; when the shape changes, a new
/// fixture shows exactly what moved.
/// </remarks>
public static class ClaudeUsageParser
{
    private sealed record WindowSpec(string Title, WindowKind Kind, TimeSpan Length, bool Secondary);

    private static readonly Dictionary<string, WindowSpec> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["five_hour"] = new("Session (5h)", WindowKind.Session, TimeSpan.FromHours(5), false),
        ["seven_day"] = new("Weekly (7d)", WindowKind.Weekly, TimeSpan.FromDays(7), false),
        ["seven_day_opus"] = new("Opus (7d)", WindowKind.Model, TimeSpan.FromDays(7), true),
        ["seven_day_sonnet"] = new("Sonnet (7d)", WindowKind.Model, TimeSpan.FromDays(7), true),
        ["seven_day_haiku"] = new("Haiku (7d)", WindowKind.Model, TimeSpan.FromDays(7), true),
        ["seven_day_routines"] = new("Daily Routines", WindowKind.Extra, TimeSpan.FromDays(7), true),
        ["seven_day_cowork"] = new("Cowork (7d)", WindowKind.Extra, TimeSpan.FromDays(7), true),
    };

    /// <summary>
    /// Keys that report against the same pool as the main Claude limit. Counting them as their own
    /// windows would show the same consumption two or three times over.
    /// </summary>
    private static readonly string[] SharedLimitKeys =
    [
        "claude_design", "design", "omelette",
    ];

    /// <summary>
    /// Field names seen carrying the utilisation percentage. The <c>limits</c> array uses
    /// <c>percent</c>; the top-level window objects use <c>utilization</c>.
    /// </summary>
    private static readonly string[] UtilizationFields =
    [
        "utilization", "percent", "used_percent", "percent_used", "usage_percent", "percentage_used",
    ];

    private static readonly string[] ResetFields =
    [
        "resets_at", "reset_at", "resets", "reset", "next_reset_at", "resets_at_utc",
    ];

    private static readonly string[] UsedUnitFields = ["used", "used_tokens", "consumed"];

    private static readonly string[] LimitUnitFields = ["limit", "total", "max", "allowed"];

    /// <summary>Parses the usage payload. Returns an empty list rather than throwing on odd input.</summary>
    public static IReadOnlyList<QuotaWindow> ParseWindows(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return [];

        // The modern payload carries a `limits` array that is strictly richer than the top-level
        // keys: it names model-scoped windows the legacy keys report as null, and those can be the
        // binding constraint. Captured live on 2026-09-13, an account showed seven_day at 56% while
        // a weekly_scoped limit sat at 76% — reading only the top-level keys understates headroom.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind is JsonValueKind.Array)
        {
            var fromLimits = ParseLimitsArray(limits, root);
            if (fromLimits.Count > 0) return fromLimits;
        }

        return ParseLegacyTopLevelKeys(root);
    }

    /// <summary>
    /// Reads the <c>limits</c> array: one entry per active limit, with kind, percent, severity,
    /// reset and an optional model scope.
    /// </summary>
    private static IReadOnlyList<QuotaWindow> ParseLimitsArray(JsonElement limits, JsonElement root)
    {
        var windows = new List<QuotaWindow>();
        var scopedSeen = 0;

        // The weekly window's true start, when the breakdown block reports it.
        DateTimeOffset? weeklyStart = null;
        if (root.TryGetProperty("seven_day_breakdown", out var breakdown) &&
            breakdown.ValueKind is JsonValueKind.Object &&
            breakdown.TryGetProperty("window_started_at", out var started) &&
            TryReadTimestamp(started, out var parsedStart))
        {
            weeklyStart = parsedStart;
        }

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind is not JsonValueKind.Object) continue;

            var kind = ReadString(limit, ["kind"]) ?? "unknown";
            var group = ReadString(limit, ["group"]) ?? kind;
            var percent = ReadPercent(limit);
            var resetsAt = ReadTimestamp(limit);

            if (percent is null && resetsAt is null) continue;

            var isSession = group.Contains("session", StringComparison.OrdinalIgnoreCase);
            var length = isSession ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

            // A scoped limit applies to one model or surface rather than the whole plan.
            string? scopeName = null;
            if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind is JsonValueKind.Object)
            {
                scopeName = ReadNestedString(scope, "model", ["display_name", "name", "id"])
                            ?? ReadNestedString(scope, "surface", ["display_name", "name", "id"]);
            }

            var isScoped = kind.Contains("scoped", StringComparison.OrdinalIgnoreCase) || scopeName is not null;

            // is_active marks the limit currently governing the account; an inactive scoped limit
            // is background detail rather than something to lead with.
            var isActive = limit.TryGetProperty("is_active", out var active) &&
                           active.ValueKind is JsonValueKind.True;

            var id = isScoped
                ? $"claude.{kind.ToLowerInvariant()}.{Slug(scopeName ?? (++scopedSeen).ToString(CultureInfo.InvariantCulture))}"
                : $"claude.{kind.ToLowerInvariant()}";

            windows.Add(new QuotaWindow
            {
                Id = id,
                Title = TitleForLimit(kind, group, scopeName, isSession),
                Kind = isSession ? WindowKind.Session : isScoped ? WindowKind.Model : WindowKind.Weekly,
                UsedPercent = percent,
                ResetsAt = resetsAt,
                WindowLength = length,
                WindowStartsAt = isSession ? null : weeklyStart,
                Severity = ReadSeverity(limit),
                SecondaryByDefault = isScoped && !isActive,
            });
        }

        return Order(windows);
    }

    private static string TitleForLimit(string kind, string group, string? scopeName, bool isSession)
    {
        if (isSession) return "Session (5h)";
        if (scopeName is { Length: > 0 }) return $"{scopeName} (7d)";

        return kind.Contains("all", StringComparison.OrdinalIgnoreCase) || group.Contains("weekly", StringComparison.OrdinalIgnoreCase)
            ? "Weekly (7d)"
            : Humanise(kind);
    }

    private static IncidentSeverity ReadSeverity(JsonElement obj) =>
        ReadString(obj, ["severity"])?.ToLowerInvariant() switch
        {
            "warning" => IncidentSeverity.Minor,
            "critical" or "exceeded" => IncidentSeverity.Major,
            _ => IncidentSeverity.None,
        };

    /// <summary>The pre-<c>limits</c> payload shape: one object per window, keyed by name.</summary>
    private static IReadOnlyList<QuotaWindow> ParseLegacyTopLevelKeys(JsonElement root)
    {

        // Some responses nest the windows under a wrapper.
        var container = root;
        foreach (var wrapper in new[] { "rate_limits", "limits", "usage", "windows" })
        {
            if (root.TryGetProperty(wrapper, out var nested) && nested.ValueKind is JsonValueKind.Object)
            {
                container = nested;
                break;
            }
        }

        var windows = new List<QuotaWindow>();

        foreach (var property in container.EnumerateObject())
        {
            if (property.Value.ValueKind is not JsonValueKind.Object) continue;
            if (property.NameEquals("extra_usage")) continue; // spend, handled separately
            if (SharedLimitKeys.Any(k => property.Name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;

            var spec = Known.GetValueOrDefault(property.Name);
            var used = ReadPercent(property.Value);
            var resetsAt = ReadTimestamp(property.Value);

            // An unrecognised key that still looks like a window is shown rather than dropped: a
            // new limit appearing is exactly the thing the user needs to see.
            if (spec is null && used is null && resetsAt is null) continue;

            // Anthropic ships unreleased features as codenamed keys ("nimbus_quill", "tangelo")
            // that return a zero-filled object with no reset. Those are inactive slots, not
            // quotas, and listing them fills the flyout with meaningless 0% rows. Chasing the
            // codenames by name would be a losing game, so the rule is structural: an unknown
            // window with no reset and no consumption is not a window.
            if (spec is null && resetsAt is null && used is (null or 0)) continue;

            spec ??= new WindowSpec(Humanise(property.Name), WindowKind.Extra, InferLength(property.Name), true);

            windows.Add(new QuotaWindow
            {
                Id = $"claude.{property.Name.ToLowerInvariant()}",
                Title = spec.Title,
                Kind = spec.Kind,
                UsedPercent = used,
                ResetsAt = resetsAt,
                WindowLength = spec.Length,
                UsedUnits = ReadLong(property.Value, UsedUnitFields),
                LimitUnits = ReadLong(property.Value, LimitUnitFields),
                SecondaryByDefault = spec.Secondary,
            });
        }

        return Order(windows);
    }

    /// <summary>
    /// Sorts windows into the order the flyout renders them: session, weekly, then everything else.
    /// </summary>
    private static IReadOnlyList<QuotaWindow> Order(List<QuotaWindow> windows)
    {
        static int Rank(QuotaWindow w) => w.Kind switch
        {
            WindowKind.Session => 0,
            WindowKind.Weekly => 1,
            WindowKind.Monthly => 2,
            WindowKind.Model => 3,
            _ => 4,
        };

        return [.. windows.OrderBy(Rank).ThenByDescending(w => w.UsedPercent ?? -1)];
    }

    /// <summary>
    /// Reads overage spend, preferring the structured <c>spend</c> block over the flat fields.
    /// </summary>
    /// <remarks>
    /// The current payload reports money as minor units plus an exponent
    /// (<c>{"amount_minor": 8941, "currency": "USD", "exponent": 2}</c> is $89.41). Reading
    /// <c>amount_minor</c> as if it were a major-unit amount would render that as $8,941.
    /// </remarks>
    public static MoneySnapshot? ParseSpend(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return null;

        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind is JsonValueKind.Object)
        {
            var used = ReadMoney(spend, "used");
            var limit = ReadMoney(spend, "limit");

            if (used is not null || limit is not null)
            {
                return new MoneySnapshot
                {
                    SpentThisPeriod = used?.Amount,
                    PeriodLimit = limit?.Amount,
                    Currency = used?.Currency ?? limit?.Currency ?? "USD",
                };
            }
        }

        if (!root.TryGetProperty("extra_usage", out var extra) || extra.ValueKind is not JsonValueKind.Object)
            return null;

        var spent = ReadDecimal(extra, ["spent", "used", "amount", "used_credits"]);
        var monthlyLimit = ReadDecimal(extra, ["limit", "cap", "max", "monthly_limit"]);

        // Credit fields are in minor units when the block declares decimal places.
        var places = extra.TryGetProperty("decimal_places", out var dp) &&
                     dp.ValueKind is JsonValueKind.Number && dp.TryGetInt32(out var parsedPlaces)
            ? parsedPlaces
            : 0;

        if (places > 0)
        {
            var divisor = (decimal)Math.Pow(10, places);
            if (spent is { } s) spent = s / divisor;
            if (monthlyLimit is { } l) monthlyLimit = l / divisor;
        }

        if (spent is null && monthlyLimit is null) return null;

        return new MoneySnapshot
        {
            SpentThisPeriod = spent,
            PeriodLimit = monthlyLimit,
            Currency = ReadString(extra, ["currency"]) ?? "USD",
        };
    }

    /// <summary>Reads a <c>{amount_minor, currency, exponent}</c> money object.</summary>
    private static (decimal Amount, string Currency)? ReadMoney(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var money) || money.ValueKind is not JsonValueKind.Object)
            return null;

        if (ReadDecimal(money, ["amount_minor"]) is not { } minor)
            return ReadDecimal(money, ["amount"]) is { } plain
                ? (plain, ReadString(money, ["currency"]) ?? "USD")
                : null;

        var exponent = money.TryGetProperty("exponent", out var e) &&
                       e.ValueKind is JsonValueKind.Number && e.TryGetInt32(out var parsed)
            ? parsed
            : 2;

        return (minor / (decimal)Math.Pow(10, exponent), ReadString(money, ["currency"]) ?? "USD");
    }

    /// <summary>Reads account identity from either the usage or the profile payload.</summary>
    public static AccountIdentity ParseIdentity(JsonElement root)
    {
        var email = ReadString(root, ["email", "email_address"])
                    ?? ReadNestedString(root, "account", ["email", "email_address"])
                    ?? ReadNestedString(root, "profile", ["email", "email_address"]);

        var org = ReadNestedString(root, "organization", ["name", "display_name"])
                  ?? ReadString(root, ["organization_name", "org_name"]);

        return new AccountIdentity(email, ParsePlanLabel(root), org);
    }

    /// <summary>
    /// Builds the plan label, preferring <c>subscriptionType</c> and falling back to the rate-limit
    /// tier. Tiers carrying a multiplier render as "Max 5x" / "Max 20x".
    /// </summary>
    public static string? ParsePlanLabel(JsonElement root)
    {
        var subscription = ReadString(root, ["subscriptionType", "subscription_type", "plan", "plan_type"])
                           ?? ReadNestedString(root, "account", ["subscriptionType", "subscription_type"]);

        // The profile payload puts the tier under `organization`, which is where the multiplier
        // actually lives: organization.rate_limit_tier = "default_claude_max_5x" is the only place
        // "Max 5x" can be derived from.
        var tier = ReadString(root, ["rate_limit_tier", "rateLimitTier", "tier"])
                   ?? ReadNestedString(root, "organization", ["rate_limit_tier", "rateLimitTier"])
                   ?? ReadNestedString(root, "organization", ["organization_type"]);

        var raw = subscription ?? tier;

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Last resort: the boolean plan flags on the account object.
            if (ReadBool(root, "account", "has_claude_max") is true) return "Max";
            if (ReadBool(root, "account", "has_claude_pro") is true) return "Pro";
            return null;
        }

        return PrettyPlan(raw);
    }

    private static bool? ReadBool(JsonElement root, string container, string field)
    {
        if (root.ValueKind is not JsonValueKind.Object) return null;
        if (!root.TryGetProperty(container, out var nested) || nested.ValueKind is not JsonValueKind.Object) return null;

        return nested.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    internal static string PrettyPlan(string raw)
    {
        var value = raw.Trim();

        // e.g. default_claude_max_20x -> Max 20x
        var multiplier = System.Text.RegularExpressions.Regex.Match(
            value, @"max[_\-]?(\d+)x", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (multiplier.Success) return $"Max {multiplier.Groups[1].Value}x";

        if (value.Contains("max", StringComparison.OrdinalIgnoreCase)) return "Max";
        if (value.Contains("team", StringComparison.OrdinalIgnoreCase)) return "Team";
        if (value.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return "Enterprise";
        if (value.Contains("pro", StringComparison.OrdinalIgnoreCase)) return "Pro";
        if (value.Contains("free", StringComparison.OrdinalIgnoreCase)) return "Free";

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    }

    // ---- field readers -----------------------------------------------------------------------

    private static double? ReadPercent(JsonElement obj)
    {
        foreach (var field in UtilizationFields)
        {
            if (!obj.TryGetProperty(field, out var value)) continue;

            switch (value.ValueKind)
            {
                // Always a 0-100 percentage, never a 0-1 fraction. Live payloads carry values like
                // 4.0, 56.0 and 89.41, so a "looks like a fraction, multiply by 100" heuristic
                // would turn genuine sub-1% usage early in a window into an alarming 50%.
                case JsonValueKind.Number when value.TryGetDouble(out var d):
                    return Math.Clamp(d, 0, 100);
                case JsonValueKind.String when double.TryParse(
                    value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return Math.Clamp(parsed, 0, 100);
                case JsonValueKind.Null:
                    return null;
                default:
                    continue;
            }
        }

        // Fall back to used/limit when no percentage was given directly.
        var used = ReadLong(obj, UsedUnitFields);
        var limit = ReadLong(obj, LimitUnitFields);
        if (used is { } u && limit is { } l && l > 0) return Math.Clamp(u * 100d / l, 0, 100);

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement obj)
    {
        foreach (var field in ResetFields)
        {
            if (!obj.TryGetProperty(field, out var value)) continue;
            if (TryReadTimestamp(value, out var parsed)) return parsed;
        }

        return null;
    }

    internal static bool TryReadTimestamp(JsonElement value, out DateTimeOffset result)
    {
        result = default;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) return false;

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out result))
                    return true;

                // Numeric epoch delivered as a string.
                if (long.TryParse(text, out var epoch)) return TryFromEpoch(epoch, out result);
                return false;
            }

            case JsonValueKind.Number when value.TryGetInt64(out var epoch):
                return TryFromEpoch(epoch, out result);

            default:
                return false;
        }
    }

    /// <summary>
    /// Interprets an epoch as seconds or milliseconds by magnitude. Anything past year 5138 in
    /// seconds is really milliseconds.
    /// </summary>
    private static bool TryFromEpoch(long epoch, out DateTimeOffset result)
    {
        result = default;
        if (epoch <= 0) return false;

        try
        {
            result = epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static long? ReadLong(JsonElement obj, string[] fields)
    {
        foreach (var field in fields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

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

    private static string? ReadString(JsonElement obj, string[] fields)
    {
        if (obj.ValueKind is not JsonValueKind.Object) return null;

        foreach (var field in fields)
        {
            if (obj.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.String &&
                value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement root, string container, string[] fields)
        => root.ValueKind is JsonValueKind.Object && root.TryGetProperty(container, out var nested)
            ? ReadString(nested, fields)
            : null;

    private static TimeSpan InferLength(string key)
    {
        if (key.Contains("five_hour", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromHours(5);
        if (key.Contains("hour", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromHours(1);
        if (key.Contains("seven_day", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromDays(7);
        if (key.Contains("month", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromDays(30);
        if (key.Contains("day", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromDays(1);
        return TimeSpan.FromDays(7);
    }

    private static string Slug(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static string Humanise(string key)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
}

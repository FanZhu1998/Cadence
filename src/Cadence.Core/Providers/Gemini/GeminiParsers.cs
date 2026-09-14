using System.Globalization;
using System.Text.Json;
using Cadence.Core.Model;
using Cadence.Core.Providers.Claude;

namespace Cadence.Core.Providers.Gemini;

/// <summary>
/// Maps Antigravity's <c>RetrieveUserQuotaSummary</c> onto quota windows.
/// </summary>
/// <remarks>
/// Antigravity 2.x exposes two pools (Gemini models, and Claude/GPT models), each with a weekly
/// and a five-hour limit. Buckets report <c>remainingFraction</c>, so used = 1 - remaining. Rows
/// that carry reset metadata but no fraction stay visible as reset context with unknown usage;
/// rendering them as 0% would claim a full quota the server never reported.
/// </remarks>
public static class AntigravityParser
{
    public static IReadOnlyList<QuotaWindow> ParseWindows(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind is not JsonValueKind.Object) return [];

        var response = root.TryGetProperty("response", out var nested) && nested.ValueKind is JsonValueKind.Object
            ? nested
            : root;

        if (!TryGetArray(response, ["groups", "quotaGroups"], out var groups)) return [];

        var windows = new List<QuotaWindow>();

        foreach (var group in groups.EnumerateArray())
        {
            var groupName = ReadString(group, ["displayName", "name", "groupId", "id"]) ?? "Gemini";

            if (!TryGetArray(group, ["buckets", "quotaBuckets"], out var buckets)) continue;

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind is not JsonValueKind.Object) continue;

                var bucketId = ReadString(bucket, ["bucketId", "id", "key"]) ?? "bucket";
                var bucketName = ReadString(bucket, ["displayName", "name"]) ?? Humanise(bucketId);

                var used = ReadRemainingFraction(bucket) is { } remaining
                    ? Math.Clamp((1 - remaining) * 100, 0, 100)
                    : (double?)null;

                var resetsAt = ReadReset(bucket, now);

                // Nothing at all to show for this row.
                if (used is null && resetsAt is null) continue;

                var length = InferLength(bucketId, bucketName, ReadString(bucket, ["description"]));

                windows.Add(new QuotaWindow
                {
                    Id = $"gemini.{Slug(groupName)}.{Slug(bucketId)}",
                    Title = $"{groupName} — {bucketName}",
                    Kind = length >= TimeSpan.FromDays(1) ? WindowKind.Weekly : WindowKind.Session,
                    UsedPercent = used,
                    ResetsAt = resetsAt,
                    WindowLength = length,
                });
            }
        }

        return [.. windows.OrderBy(w => w.Kind).ThenByDescending(w => w.UsedPercent ?? -1)];
    }

    public static AccountIdentity? ParseIdentity(JsonElement root)
    {
        var response = root.TryGetProperty("response", out var nested) && nested.ValueKind is JsonValueKind.Object
            ? nested
            : root;

        var email = ReadString(response, ["email", "userEmail"])
                    ?? (response.TryGetProperty("user", out var user) ? ReadString(user, ["email"]) : null);

        var plan = ReadString(response, ["planName", "tier", "subscriptionTier"]);

        return email is null && plan is null ? null : new AccountIdentity(email, plan);
    }

    private static double? ReadRemainingFraction(JsonElement bucket)
    {
        foreach (var field in new[] { "remainingFraction", "remaining_fraction" })
        {
            if (bucket.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var fraction))
            {
                return Math.Clamp(fraction, 0, 1);
            }
        }

        // Nested under a "remaining" object in some builds.
        if (bucket.TryGetProperty("remaining", out var remaining))
        {
            if (remaining.ValueKind is JsonValueKind.Number && remaining.TryGetDouble(out var flat))
                return Math.Clamp(flat, 0, 1);

            if (remaining.ValueKind is JsonValueKind.Object)
            {
                foreach (var field in new[] { "remainingFraction", "fraction", "value" })
                {
                    if (remaining.TryGetProperty(field, out var value) &&
                        value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var nested))
                    {
                        return Math.Clamp(nested, 0, 1);
                    }
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadReset(JsonElement bucket, DateTimeOffset now)
    {
        foreach (var field in new[] { "resetTime", "resetsAt", "reset_time", "nextResetTime" })
        {
            if (bucket.TryGetProperty(field, out var value) && ClaudeUsageParser.TryReadTimestamp(value, out var parsed))
                return parsed;
        }

        foreach (var field in new[] { "resetInSeconds", "secondsUntilReset" })
        {
            if (bucket.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds >= 0)
            {
                return now.AddSeconds(seconds);
            }
        }

        return null;
    }

    private static TimeSpan InferLength(params string?[] hints)
    {
        foreach (var hint in hints)
        {
            if (hint is null) continue;
            if (hint.Contains("week", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("7d", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromDays(7);
            }

            if (hint.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("five", StringComparison.OrdinalIgnoreCase) ||
                hint.Contains("hour", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromHours(5);
            }
        }

        return TimeSpan.FromHours(5);
    }

    private static bool TryGetArray(JsonElement obj, string[] names, out JsonElement array)
    {
        foreach (var name in names)
        {
            if (obj.ValueKind is JsonValueKind.Object &&
                obj.TryGetProperty(name, out array) && array.ValueKind is JsonValueKind.Array)
            {
                return true;
            }
        }

        array = default;
        return false;
    }

    internal static string? ReadString(JsonElement obj, string[] fields)
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

    internal static string Slug(string value)
        => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static string Humanise(string key)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
}

/// <summary>
/// Maps Code Assist's <c>retrieveUserQuota</c> onto windows.
/// </summary>
/// <remarks>
/// Buckets carry <c>remainingFraction</c>, <c>resetTime</c> and <c>modelId</c>. Per model the
/// lowest fraction wins, since that is the limit the user will actually hit first. Pro models are
/// the primary lane, Flash the secondary.
/// </remarks>
public static class GeminiCodeAssistParser
{
    public static IReadOnlyList<QuotaWindow> ParseWindows(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind is not JsonValueKind.Object) return [];

        JsonElement buckets = default;
        foreach (var name in new[] { "quotaBuckets", "buckets", "quotas" })
        {
            if (root.TryGetProperty(name, out buckets) && buckets.ValueKind is JsonValueKind.Array) break;
            buckets = default;
        }

        if (buckets.ValueKind is not JsonValueKind.Array) return [];

        // Group by model, keeping the tightest bucket for each.
        var byModel = new Dictionary<string, (double? Fraction, DateTimeOffset? Reset)>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind is not JsonValueKind.Object) continue;

            var model = AntigravityParser.ReadString(bucket, ["modelId", "model", "id"]) ?? "gemini";

            double? fraction = null;
            if (bucket.TryGetProperty("remainingFraction", out var value) &&
                value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var parsed))
            {
                fraction = Math.Clamp(parsed, 0, 1);
            }

            DateTimeOffset? reset = null;
            if (bucket.TryGetProperty("resetTime", out var resetValue) &&
                ClaudeUsageParser.TryReadTimestamp(resetValue, out var parsedReset))
            {
                reset = parsedReset;
            }

            if (!byModel.TryGetValue(model, out var existing))
            {
                byModel[model] = (fraction, reset);
                continue;
            }

            // Lowest remaining fraction is the binding constraint.
            var tightest = (existing.Fraction, fraction) switch
            {
                ({ } a, { } b) => Math.Min(a, b),
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => (double?)null,
            };

            byModel[model] = (tightest, existing.Reset ?? reset);
        }

        var windows = new List<QuotaWindow>();

        foreach (var (model, data) in byModel)
        {
            var isPro = model.Contains("pro", StringComparison.OrdinalIgnoreCase);

            windows.Add(new QuotaWindow
            {
                Id = $"gemini.codeassist.{AntigravityParser.Slug(model)}",
                Title = model,
                Kind = isPro ? WindowKind.Session : WindowKind.Model,
                UsedPercent = data.Fraction is { } f ? Math.Clamp((1 - f) * 100, 0, 100) : null,
                ResetsAt = data.Reset,
                WindowLength = TimeSpan.FromHours(24),
                SecondaryByDefault = !isPro,
            });
        }

        return [.. windows.OrderBy(w => w.SecondaryByDefault).ThenByDescending(w => w.UsedPercent ?? -1)];
    }
}

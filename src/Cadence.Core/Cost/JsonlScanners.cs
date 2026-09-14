using System.Text.Json;
using Cadence.Core.Model;

namespace Cadence.Core.Cost;

/// <summary>Parses one provider's session transcripts into normalised cost entries.</summary>
public interface IJsonlScanner
{
    ProviderId Provider { get; }

    /// <summary>Directories that may contain this provider's transcripts.</summary>
    IEnumerable<string> Directories();

    /// <summary>
    /// Parses one line. Returns null for lines that carry no usage.
    /// </summary>
    /// <remarks>
    /// Stateful across a file: Codex needs the most recent <c>turn_context</c> to know which model
    /// a later usage record belongs to.
    /// </remarks>
    CostEntry? ParseLine(string line);

    /// <summary>Resets per-file state before a new file is read.</summary>
    void BeginFile();
}

/// <summary>
/// Claude Code transcripts.
/// </summary>
/// <remarks>
/// Verified against a real 80 MB transcript on 2026-09-13: 1156 assistant lines carried usage but
/// only 553 were distinct by <c>(message.id, requestId)</c>, one pair repeating fourteen times.
/// Summing without deduplication inflates that file's totals by more than 2x, which is the single
/// most common bug in tools of this kind. Deduplication happens at the database primary key, so
/// the last write for an id wins, matching the cumulative semantics of streaming chunks.
/// </remarks>
public sealed class ClaudeJsonlScanner : IJsonlScanner
{
    public ProviderId Provider => ProviderId.Claude;

    public IEnumerable<string> Directories() => Credentials.KnownPaths.ClaudeProjectDirectories();

    public void BeginFile() { }

    public CostEntry? ParseLine(string line)
    {
        // Cheap reject before paying for a parse: most lines are not assistant turns.
        if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null; // a torn final line in a file being written right now
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return null;
            if (GetString(root, "type") is not "assistant") return null;

            if (!root.TryGetProperty("message", out var message) || message.ValueKind is not JsonValueKind.Object)
                return null;

            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind is not JsonValueKind.Object)
                return null;

            var messageId = GetString(message, "id");
            var requestId = GetString(root, "requestId");
            if (messageId is null && requestId is null) return null;

            // cache_creation splits by TTL and the two bill differently.
            long write5m = 0, write1h = 0;
            if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind is JsonValueKind.Object)
            {
                write5m = GetLong(creation, "ephemeral_5m_input_tokens");
                write1h = GetLong(creation, "ephemeral_1h_input_tokens");
            }

            var totalCreation = GetLong(usage, "cache_creation_input_tokens");
            if (write5m + write1h == 0 && totalCreation > 0)
            {
                // Older transcripts report only the total. Attribute it to the cheaper bucket
                // rather than inventing a 1-hour write the user may not have paid for.
                write5m = totalCreation;
            }

            long reasoning = 0;
            if (usage.TryGetProperty("output_tokens_details", out var details) &&
                details.ValueKind is JsonValueKind.Object)
            {
                reasoning = GetLong(details, "thinking_tokens");
            }

            return new CostEntry
            {
                Provider = ProviderId.Claude,
                EntryId = $"{messageId}|{requestId}",
                Timestamp = ReadTimestamp(root) ?? DateTimeOffset.UnixEpoch,
                Model = GetString(message, "model") ?? "unknown",

                // Claude's fields are additive: input_tokens already excludes cache reads and
                // cache creation, so each is stored as-is. The `iterations` array is a breakdown
                // of these same numbers and is deliberately ignored.
                InputTokens = GetLong(usage, "input_tokens"),
                OutputTokens = GetLong(usage, "output_tokens"),
                CacheReadTokens = GetLong(usage, "cache_read_input_tokens"),
                CacheWrite5mTokens = write5m,
                CacheWrite1hTokens = write1h,

                // Thinking tokens are a subset of output_tokens and already billed as output.
                ReasoningTokens = reasoning,
            };
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
        => GetString(root, "timestamp") is { Length: > 0 } text &&
           DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
               out var parsed)
            ? parsed
            : null;

    internal static string? GetString(JsonElement obj, string name)
        => obj.ValueKind is JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind is JsonValueKind.String
            ? v.GetString()
            : null;

    internal static long GetLong(JsonElement obj, string name)
        => obj.ValueKind is JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind is JsonValueKind.Number && v.TryGetInt64(out var parsed)
            ? parsed
            : 0L;
}

/// <summary>
/// Codex session transcripts.
/// </summary>
/// <remarks>
/// Two traps, both verified against a real transcript on 2026-09-13:
/// <list type="number">
/// <item>Usage lives in <c>token_usage_record</c>, not in the <c>event_msg</c> entries older
/// documentation describes.</item>
/// <item>The payload carries three usage blocks. <c>turn_token_usage</c> and
/// <c>thread_token_usage</c> are running totals; only <c>usage</c> is the per-response delta.
/// Summing either cumulative block grows quadratically with conversation length.</item>
/// </list>
/// Codex's fields are also inclusive rather than additive, so fresh input is
/// <c>input_tokens - cached_input_tokens</c>.
/// </remarks>
public sealed class CodexJsonlScanner : IJsonlScanner
{
    private string _currentModel = "unknown";

    public ProviderId Provider => ProviderId.Codex;

    public IEnumerable<string> Directories() => Credentials.KnownPaths.CodexSessionDirectories();

    public void BeginFile() => _currentModel = "unknown";

    public CostEntry? ParseLine(string line)
    {
        var isUsage = line.Contains("token_usage_record", StringComparison.Ordinal);
        var isContext = line.Contains("turn_context", StringComparison.Ordinal);
        if (!isUsage && !isContext) return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return null;

            var type = ClaudeJsonlScanner.GetString(root, "type");
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind is not JsonValueKind.Object)
                return null;

            // A turn_context names the model for everything that follows it.
            if (type is "turn_context")
            {
                if (ClaudeJsonlScanner.GetString(payload, "model") is { Length: > 0 } model)
                    _currentModel = model;
                return null;
            }

            if (type is not "token_usage_record") return null;

            // Only the per-response delta. The sibling turn_ and thread_ blocks are cumulative.
            if (!payload.TryGetProperty("usage", out var usage) || usage.ValueKind is not JsonValueKind.Object)
                return null;

            var responseId = ClaudeJsonlScanner.GetString(payload, "response_id")
                             ?? ClaudeJsonlScanner.GetString(payload, "turn_id");
            if (responseId is null) return null;

            var input = ClaudeJsonlScanner.GetLong(usage, "input_tokens");
            var cached = ClaudeJsonlScanner.GetLong(usage, "cached_input_tokens");
            var output = ClaudeJsonlScanner.GetLong(usage, "output_tokens");

            return new CostEntry
            {
                Provider = ProviderId.Codex,
                EntryId = responseId,
                Timestamp = ReadTimestamp(root) ?? DateTimeOffset.UnixEpoch,
                Model = _currentModel,

                // Inclusive to exclusive: cached input is a subset of input_tokens.
                InputTokens = Math.Max(0, input - cached),
                CacheReadTokens = cached,
                CacheWrite5mTokens = ClaudeJsonlScanner.GetLong(usage, "cache_write_input_tokens"),
                OutputTokens = output,

                // Likewise a subset of output_tokens, reported for display only.
                ReasoningTokens = ClaudeJsonlScanner.GetLong(usage, "reasoning_output_tokens"),
            };
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
        => ClaudeJsonlScanner.GetString(root, "timestamp") is { Length: > 0 } text &&
           DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
               out var parsed)
            ? parsed
            : null;
}

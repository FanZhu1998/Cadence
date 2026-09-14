using System.Text.Json;
using Cadence.Core.Credentials;

namespace Cadence.Core.Providers.Claude;

/// <summary>OAuth material Claude Code left on disk.</summary>
public sealed record ClaudeOAuthToken(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes)
{
    /// <summary>
    /// The usage endpoint needs this scope. A token minted with only <c>user:inference</c>
    /// authenticates fine and then returns 403, which reads as a bug unless we name the cause.
    /// </summary>
    public const string RequiredScope = "user:profile";

    public bool HasRequiredScope =>
        Scopes.Count == 0 || Scopes.Contains(RequiredScope, StringComparer.OrdinalIgnoreCase);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;

    /// <summary>Refresh a little early so a long refresh does not race the expiry.</summary>
    public bool NeedsRefresh(DateTimeOffset now) =>
        ExpiresAt is { } e && e <= now + TimeSpan.FromMinutes(5);
}

/// <summary>
/// Reads <c>~/.claude/.credentials.json</c>.
/// </summary>
/// <remarks>
/// Claude Code owns this file and rewrites it on its own refresh cycle, so a read can land
/// mid-write and see truncated JSON. That is expected, not exceptional: the reader retries briefly
/// and then reports "not logged in" rather than throwing. Cadence never writes to this file.
/// </remarks>
public static class ClaudeCredentialReader
{
    private const int MaxAttempts = 3;

    public static bool Exists(string? path = null) => File.Exists(path ?? KnownPaths.ClaudeCredentials);

    public static async Task<ClaudeOAuthToken?> ReadAsync(string? path = null, CancellationToken ct = default)
    {
        var file = path ?? KnownPaths.ClaudeCredentials;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!File.Exists(file)) return null;

            try
            {
                // Share ReadWrite so we never block Claude Code's own write.
                await using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return Parse(document.RootElement);
            }
            catch (JsonException)
            {
                // Half-written file. Give the writer a moment.
                if (attempt == MaxAttempts - 1) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                if (attempt == MaxAttempts - 1) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Pure parse step, exposed for fixture tests.</summary>
    public static ClaudeOAuthToken? Parse(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return null;

        // The blob is normally nested under claudeAiOauth; tolerate a flat shape too.
        var node = root.TryGetProperty("claudeAiOauth", out var nested) && nested.ValueKind is JsonValueKind.Object
            ? nested
            : root;

        var accessToken = GetString(node, "accessToken") ?? GetString(node, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        DateTimeOffset? expiresAt = null;
        if ((node.TryGetProperty("expiresAt", out var exp) || node.TryGetProperty("expires_at", out exp))
            && ClaudeUsageParser.TryReadTimestamp(exp, out var parsed))
        {
            expiresAt = parsed;
        }

        var scopes = new List<string>();
        if (node.TryGetProperty("scopes", out var scopeArray) && scopeArray.ValueKind is JsonValueKind.Array)
        {
            scopes.AddRange(scopeArray.EnumerateArray()
                .Where(s => s.ValueKind is JsonValueKind.String)
                .Select(s => s.GetString()!));
        }
        else if (GetString(node, "scope") is { Length: > 0 } scopeText)
        {
            scopes.AddRange(scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return new ClaudeOAuthToken(
            accessToken,
            // The refresh token is deliberately left unread: Cadence never refreshes, so it has no
            // use for the one long-lived secret in this file.
            expiresAt,
            scopes);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Reads identity from <c>~/.claude.json</c>. Metadata only; this file holds no tokens.
/// </summary>
public static class ClaudeAccountMetadataReader
{
    public static async Task<(string? Email, string? Organization)> ReadAsync(
        string? path = null, CancellationToken ct = default)
    {
        var file = path ?? KnownPaths.ClaudeAccountMetadata;
        if (!File.Exists(file)) return (null, null);

        try
        {
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("oauthAccount", out var account) ||
                account.ValueKind is not JsonValueKind.Object)
            {
                return (null, null);
            }

            return (
                Read(account, "emailAddress") ?? Read(account, "email"),
                Read(account, "organizationName") ?? Read(account, "organization_name"));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        static string? Read(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
    }
}

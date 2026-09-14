using System.Text.Json;
using Cadence.Core.Credentials;
using Cadence.Core.Providers.Claude;

namespace Cadence.Core.Providers.Gemini;

/// <summary>Google OAuth material from <c>~/.gemini/oauth_creds.json</c>.</summary>
public sealed record GeminiToken(string AccessToken, DateTimeOffset? ExpiresAt, string? Email)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
}

/// <summary>
/// Reads the Gemini CLI credential file.
/// </summary>
/// <remarks>
/// Only used by Code Assist mode, which the user has to enable explicitly. Google has stated that
/// third-party use of Gemini CLI OAuth credentials is a policy-violating use case that can trigger
/// abuse detection, so this is never on a default path.
/// </remarks>
public static class GeminiCredentialReader
{
    public static async Task<GeminiToken?> ReadAsync(string? path = null, CancellationToken ct = default)
    {
        var file = path ?? KnownPaths.GeminiOAuthCredentials;
        if (!File.Exists(file)) return null;

        try
        {
            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Pure parse step, exposed for fixture tests.</summary>
    public static GeminiToken? Parse(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return null;

        var accessToken = GetString(root, "access_token") ?? GetString(root, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        DateTimeOffset? expiresAt = null;
        foreach (var field in new[] { "expiry_date", "expiryDate", "expires_at" })
        {
            if (root.TryGetProperty(field, out var value) && ClaudeUsageParser.TryReadTimestamp(value, out var parsed))
            {
                expiresAt = parsed;
                break;
            }
        }

        return new GeminiToken(
            accessToken,
            // The refresh token is deliberately left unread: Cadence never refreshes, so it has no
            // use for the one long-lived secret in this file.
            expiresAt,
            GetString(root, "email") ?? GetString(root, "id_token_email"));
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
}

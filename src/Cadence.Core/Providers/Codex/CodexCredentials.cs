using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Cadence.Core.Credentials;
using Tomlyn;
using Tomlyn.Model;

namespace Cadence.Core.Providers.Codex;

/// <summary>Where the Codex CLI keeps its credentials.</summary>
public enum CodexCredentialStore
{
    /// <summary><c>auth.json</c> in CODEX_HOME. The default.</summary>
    File,

    /// <summary>Windows Credential Manager. <c>auth.json</c> may be absent or stale.</summary>
    Keyring,
}

/// <summary>Tokens from <c>auth.json</c>, plus what the id_token claims say about the account.</summary>
public sealed record CodexAuth(
    string AccessToken,
    string? IdToken,
    string? AccountId,
    DateTimeOffset? LastRefresh,
    string? Email,
    string? PlanType)
{
    /// <summary>Codex refreshes roughly weekly; past this the token is likely stale.</summary>
    public bool IsStale(DateTimeOffset now) =>
        LastRefresh is { } last && now - last > TimeSpan.FromDays(8);
}

/// <summary>
/// Reads <c>auth.json</c> and <c>config.toml</c>.
/// </summary>
/// <remarks>
/// Recent Codex builds honour <c>cli_auth_credentials_store</c>. When it is set to <c>keyring</c>
/// the tokens live in Windows Credential Manager and <c>auth.json</c> is absent or stale, so
/// reading the file blindly would report a working setup as broken, or worse, use an old token.
/// </remarks>
public static class CodexCredentialReader
{
    public static CodexCredentialStore ReadStoreSetting(string? configPath = null)
    {
        var path = configPath ?? KnownPaths.CodexConfig;
        if (!File.Exists(path)) return CodexCredentialStore.File;

        try
        {
            var model = Toml.ToModel(File.ReadAllText(path));
            var value = model.TryGetValue("cli_auth_credentials_store", out var raw) ? raw as string : null;

            return string.Equals(value, "keyring", StringComparison.OrdinalIgnoreCase)
                ? CodexCredentialStore.Keyring
                : CodexCredentialStore.File;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TomlException)
        {
            // A config we cannot parse should not stop us reading auth.json.
            return CodexCredentialStore.File;
        }
    }

    public static async Task<CodexAuth?> ReadAsync(string? authPath = null, CancellationToken ct = default)
    {
        var path = authPath ?? KnownPaths.CodexAuth;
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Pure parse step, exposed for fixture tests.</summary>
    public static CodexAuth? Parse(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object) return null;

        var tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind is JsonValueKind.Object ? t : root;

        var accessToken = GetString(tokens, "access_token") ?? GetString(tokens, "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        var idToken = GetString(tokens, "id_token") ?? GetString(tokens, "idToken");
        var claims = JwtClaims.Read(idToken);

        DateTimeOffset? lastRefresh = null;
        if (GetString(root, "last_refresh") is { Length: > 0 } refreshed &&
            DateTimeOffset.TryParse(refreshed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            lastRefresh = parsed;
        }

        return new CodexAuth(
            accessToken,
            // The refresh token is deliberately left unread: Cadence never refreshes, so it has no
            // use for the one long-lived secret in this file.
            idToken,
            GetString(tokens, "account_id") ?? GetString(tokens, "accountId"),
            lastRefresh,
            claims.Email,
            claims.PlanType);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind is JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind is JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// Reads display claims out of a JWT payload.
/// </summary>
/// <remarks>
/// The signature is deliberately not verified. This token is not being trusted for authorisation —
/// it is the user's own local credential, and the only thing taken from it is an email address and
/// a plan name to print in the UI. The access token is what actually authenticates, and the server
/// validates that.
/// </remarks>
public static class JwtClaims
{
    public static (string? Email, string? PlanType) Read(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return (null, null);

        var parts = jwt.Split('.');
        if (parts.Length < 2) return (null, null);

        if (!TryDecodeBase64Url(parts[1], out var payload)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var email = GetString(root, "email");
            var plan = GetString(root, "chatgpt_plan_type") ?? GetString(root, "plan_type");

            // OpenAI nests the interesting claims under a namespaced object.
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is not JsonValueKind.Object) continue;
                email ??= GetString(property.Value, "email");
                plan ??= GetString(property.Value, "chatgpt_plan_type") ?? GetString(property.Value, "plan_type");
            }

            return (email, plan);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? GetString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
    }

    private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
    {
        var normalised = segment.Replace('-', '+').Replace('_', '/');
        normalised = (normalised.Length % 4) switch
        {
            2 => normalised + "==",
            3 => normalised + "=",
            0 => normalised,
            _ => normalised, // length %4 == 1 is malformed; let the decode fail
        };

        bytes = [];
        var buffer = new byte[normalised.Length];
        if (!Convert.TryFromBase64String(normalised, buffer, out var written)) return false;

        bytes = buffer[..written];
        return written > 0;
    }
}

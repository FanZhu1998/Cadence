using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Cadence.Core.Credentials;

/// <summary>
/// Encrypts secrets at rest. Only tokens the user pastes in are ever stored; tokens read fresh
/// from a CLI's own credential file each cycle are never persisted by Cadence at all.
/// </summary>
public interface ISecretStore
{
    /// <summary>Encrypts to a base64 blob safe to place in config.json.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>, or returns null if the blob is unreadable.</summary>
    string? Unprotect(string protectedBase64);
}

/// <summary>
/// DPAPI-backed store, scoped to the current user.
/// </summary>
/// <remarks>
/// <see cref="DataProtectionScope.CurrentUser"/> means the ciphertext is useless to any other
/// account on the machine, and useless if the config file is copied elsewhere. The entropy value
/// is a fixed application salt: it does not add secrecy on its own, it just means a blob from
/// another DPAPI-using application cannot be decrypted through this path by mistake.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Cadence.v1.secret");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public string? Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            // Config copied from another machine or user, or hand-edited. Treat as "no secret"
            // and let the provider report NotLoggedIn rather than crashing at startup.
            return null;
        }
    }
}

/// <summary>
/// Non-persisting store for tests and for non-Windows hosts.
/// </summary>
/// <remarks>
/// Deliberately not a plaintext store. If DPAPI is unavailable the correct behaviour is to lose
/// the secret on restart and ask again, not to write an unencrypted token to disk.
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    private int _next;

    public string Protect(string plaintext)
    {
        var handle = $"mem:{Interlocked.Increment(ref _next)}";
        lock (_values) _values[handle] = plaintext;
        return handle;
    }

    public string? Unprotect(string protectedBase64)
    {
        lock (_values) return _values.GetValueOrDefault(protectedBase64);
    }
}

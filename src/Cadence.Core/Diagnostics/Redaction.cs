using System.Text.RegularExpressions;

namespace Cadence.Core.Diagnostics;

/// <summary>
/// Strips credentials out of anything heading for a log file, a diagnostics bundle, or the
/// "Test connection" pane.
/// </summary>
/// <remarks>
/// Cadence reads the user's AI credentials, so a token reaching a log on disk is the worst bug the
/// app could have. This runs on every string before it reaches a sink, and is covered by tests
/// that assert each token shape is caught.
/// </remarks>
public static partial class Redaction
{
    public const string Placeholder = "[redacted]";

    /// <summary>Applies every rule. Safe on null and on text containing no secrets.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        text = AnthropicKey().Replace(text, Placeholder);
        text = OpenAiKey().Replace(text, Placeholder);
        text = GoogleToken().Replace(text, Placeholder);
        text = BearerHeader().Replace(text, $"Bearer {Placeholder}");
        text = CookieHeader().Replace(text, $"Cookie: {Placeholder}");
        text = SessionKey().Replace(text, $"sessionKey={Placeholder}");
        text = JsonTokenField().Replace(text, m => $"\"{m.Groups[1].Value}\": \"{Placeholder}\"");
        text = JwtLike().Replace(text, Placeholder);
        text = CsrfToken().Replace(text, $"csrf_token={Placeholder}");

        return text;
    }

    /// <summary>Keeps a short prefix for identification, e.g. "sk-ant-oat01-…" -> "sk-ant-oat01-[redacted]".</summary>
    public static string Fingerprint(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return Placeholder;
        var visible = Math.Min(12, secret.Length / 4);
        return visible <= 0 ? Placeholder : $"{secret[..visible]}…{Placeholder}";
    }

    // sk-ant-oat01-..., sk-ant-ort01-..., sk-ant-admin..., sk-ant-api03-...
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{4,}", RegexOptions.None, 500)]
    private static partial Regex AnthropicKey();

    // sk-proj-..., sk-..., but not the sk-ant- forms already handled above.
    [GeneratedRegex(@"\bsk-(?!ant-)[A-Za-z0-9_\-]{8,}", RegexOptions.None, 500)]
    private static partial Regex OpenAiKey();

    // Google OAuth access tokens.
    [GeneratedRegex(@"ya29\.[A-Za-z0-9_\-]+", RegexOptions.None, 500)]
    private static partial Regex GoogleToken();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-~+/]+=*", RegexOptions.IgnoreCase, 500)]
    private static partial Regex BearerHeader();

    [GeneratedRegex(@"Cookie:\s*[^\r\n]+", RegexOptions.IgnoreCase, 500)]
    private static partial Regex CookieHeader();

    [GeneratedRegex(@"sessionKey=[^;\s""]+", RegexOptions.IgnoreCase, 500)]
    private static partial Regex SessionKey();

    // JSON fields whose name says the value is a secret.
    [GeneratedRegex(
        @"""(accessToken|refreshToken|access_token|refresh_token|id_token|apiKey|api_key|OPENAI_API_KEY|client_secret|csrf_token|sessionKey)""\s*:\s*""[^""]*""",
        RegexOptions.IgnoreCase, 500)]
    private static partial Regex JsonTokenField();

    // Bare JWTs (three base64url segments), e.g. an id_token pasted into a log line.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.None, 500)]
    private static partial Regex JwtLike();

    // Antigravity language-server CSRF token on a command line.
    [GeneratedRegex(@"--csrf_token[=\s]+\S+", RegexOptions.IgnoreCase, 500)]
    private static partial Regex CsrfTokenArg();

    [GeneratedRegex(@"csrf_token=[^&\s""]+", RegexOptions.IgnoreCase, 500)]
    private static partial Regex CsrfToken();

    /// <summary>Scrubs a process command line, which carries CSRF tokens and ports.</summary>
    public static string ScrubCommandLine(string? commandLine)
        => CsrfTokenArg().Replace(Scrub(commandLine), $"--csrf_token {Placeholder}");
}

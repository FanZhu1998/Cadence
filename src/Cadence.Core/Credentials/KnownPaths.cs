namespace Cadence.Core.Credentials;

/// <summary>
/// Where the provider CLIs keep their state on Windows.
/// </summary>
/// <remarks>
/// Every path is overridable through the environment variable the tool itself honours, so a user
/// with a relocated <c>CODEX_HOME</c> or <c>CLAUDE_CONFIG_DIR</c> is found without configuration.
/// Kept in one place because these move between CLI releases and this is the file to patch.
/// </remarks>
public static class KnownPaths
{
    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RoamingAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ---- Cadence's own storage -------------------------------------------------------------

    public static string ConfigDirectory => Path.Combine(RoamingAppData, "Cadence");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public static string DataDirectory => Path.Combine(LocalAppData, "Cadence");

    public static string HistoryDatabase => Path.Combine(DataDirectory, "history.db");

    /// <summary>What has already been announced, so a restart never repeats a notification.</summary>
    public static string AlertStateFile => Path.Combine(DataDirectory, "alerts.json");

    public static string CostCacheDirectory => Path.Combine(DataDirectory, "cost-cache");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string BacktestDirectory => Path.Combine(DataDirectory, "backtests");

    // ---- Claude ------------------------------------------------------------------------------

    public static string ClaudeHome => Path.Combine(UserProfile, ".claude");

    public static string ClaudeCredentials => Path.Combine(ClaudeHome, ".credentials.json");

    /// <summary>Identity metadata only (email, org). Carries no tokens.</summary>
    public static string ClaudeAccountMetadata => Path.Combine(UserProfile, ".claude.json");

    /// <summary>
    /// Every directory that may hold Claude session transcripts. <c>CLAUDE_CONFIG_DIR</c> is
    /// comma-separated when set.
    /// </summary>
    public static IEnumerable<string> ClaudeProjectDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var dir in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(dir, "projects");
        }

        yield return Path.Combine(ClaudeHome, "projects");
    }

    // ---- Codex -------------------------------------------------------------------------------

    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(UserProfile, ".codex");

    public static string CodexAuth => Path.Combine(CodexHome, "auth.json");

    public static string CodexConfig => Path.Combine(CodexHome, "config.toml");

    public static IEnumerable<string> CodexSessionDirectories()
    {
        yield return Path.Combine(CodexHome, "sessions");
        yield return Path.Combine(CodexHome, "archived_sessions");
    }

    /// <summary>Places the codex binary is commonly installed, in probe order.</summary>
    public static IEnumerable<string> CodexBinaryCandidates()
    {
        yield return Path.Combine(RoamingAppData, "npm", "codex.cmd");
        yield return Path.Combine(LocalAppData, "OpenAI", "Codex", "bin", "codex.exe");
        yield return Path.Combine(RoamingAppData, "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
    }

    // ---- Gemini ------------------------------------------------------------------------------

    public static string GeminiHome => Path.Combine(UserProfile, ".gemini");

    public static string GeminiOAuthCredentials => Path.Combine(GeminiHome, "oauth_creds.json");

    public static string GeminiSettings => Path.Combine(GeminiHome, "settings.json");

    /// <summary>Creates every directory Cadence writes to. Safe to call repeatedly.</summary>
    public static void EnsureCadenceDirectories()
    {
        foreach (var dir in new[] { ConfigDirectory, DataDirectory, CostCacheDirectory, LogDirectory, BacktestDirectory })
            Directory.CreateDirectory(dir);
    }
}

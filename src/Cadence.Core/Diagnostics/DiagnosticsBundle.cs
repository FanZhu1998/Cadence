using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cadence.Core.Credentials;
using Cadence.Core.Model;

namespace Cadence.Core.Diagnostics;

/// <summary>
/// Writes a support bundle: logs, a redacted config, and an environment summary.
/// </summary>
/// <remarks>
/// Worth having from day one. Every bug report about a provider endpoint is unanswerable without
/// knowing which credential path resolved, which strategy ran and what came back — and asking a
/// user to paste that by hand invites them to paste a token along with it.
/// </remarks>
public static class DiagnosticsBundle
{
    /// <summary>Writes a zip to the data directory and returns its full path.</summary>
    public static string Write(AppSettings settings, string? outputPath = null)
    {
        KnownPaths.EnsureCadenceDirectories();

        var path = outputPath ?? Path.Combine(
            KnownPaths.DataDirectory,
            $"cadence-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "environment.txt", BuildEnvironmentReport());
        WriteEntry(archive, "settings.json", RedactSettings(settings));

        // Logs are already scrubbed on the way in, but scrub again: a bundle is the one artefact
        // that definitely leaves the machine.
        CopyLogs(archive);

        return path;
    }

    private static void CopyLogs(ZipArchive archive)
    {
        if (!Directory.Exists(KnownPaths.LogDirectory)) return;

        foreach (var log in Directory.EnumerateFiles(KnownPaths.LogDirectory, "*.log"))
        {
            try
            {
                // Share ReadWrite: the live log is held open by the running app.
                using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source);

                WriteEntry(archive, $"logs/{Path.GetFileName(log)}", Redaction.Scrub(reader.ReadToEnd()));
            }
            catch (IOException)
            {
                // Skip a log we cannot read rather than failing the whole bundle.
            }
        }
    }

    /// <summary>Serialises settings with every protected blob removed.</summary>
    private static string RedactSettings(AppSettings settings)
    {
        var providers = settings.Providers.ToDictionary(
            kv => kv.Key,
            kv => kv.Value with
            {
                // These are DPAPI blobs rather than plaintext, but they are still secrets and have
                // no diagnostic value whatsoever.
                ManualTokenProtected = kv.Value.ManualTokenProtected is null ? null : "[redacted]",
                AdminKeyProtected = kv.Value.AdminKeyProtected is null ? null : "[redacted]",
            });

        return JsonSerializer.Serialize(settings with { Providers = providers }, SettingsStore.Json);
    }

    private static string BuildEnvironmentReport()
    {
        var report = new StringBuilder();

        report.AppendLine($"generated       {DateTimeOffset.UtcNow:u}");
        report.AppendLine($"os              {Environment.OSVersion}");
        report.AppendLine($"architecture    {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        report.AppendLine($"runtime         {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"timezone        {TimeZoneInfo.Local.Id}");
        report.AppendLine();

        report.AppendLine("provider files (presence only, contents never read into this bundle)");
        foreach (var (label, file) in new[]
                 {
                     ("claude credentials", KnownPaths.ClaudeCredentials),
                     ("claude account", KnownPaths.ClaudeAccountMetadata),
                     ("codex auth", KnownPaths.CodexAuth),
                     ("codex config", KnownPaths.CodexConfig),
                     ("gemini creds", KnownPaths.GeminiOAuthCredentials),
                 })
        {
            report.AppendLine($"  {label,-20} {(File.Exists(file) ? "present" : "absent")}");
        }

        report.AppendLine();
        report.AppendLine("transcripts");
        foreach (var directory in KnownPaths.ClaudeProjectDirectories().Concat(KnownPaths.CodexSessionDirectories()))
        {
            var count = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Count()
                : 0;

            report.AppendLine($"  {count,5}  {directory}");
        }

        return report.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
    }
}

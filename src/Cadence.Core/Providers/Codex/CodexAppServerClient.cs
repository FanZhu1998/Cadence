using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cadence.Core.Diagnostics;

namespace Cadence.Core.Providers.Codex;

/// <summary>
/// Drives <c>codex app-server</c> over stdin/stdout JSON-RPC for one bounded query.
/// </summary>
/// <remarks>
/// Run read-only and untrusted: this is a usage query and must never be able to touch the user's
/// files. Every stage has its own timeout and the child is killed on any failure path, because an
/// unreaped child leaves the stdout reader blocked forever and the refresh loop never ticks again.
/// On Windows the process is also placed in a kill-on-close Job Object, so a crash of Cadence
/// cannot leave a stray codex.exe running.
/// </remarks>
public static class CodexAppServerClient
{
    /// <summary>Cold start includes runtime resolution, so this is generous.</summary>
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Per-method budget once the server is up.</summary>
    private static readonly TimeSpan MethodTimeout = TimeSpan.FromSeconds(8);

    public sealed record Result(string? RateLimitsJson, string? Email, string? PlanType, string? Error)
    {
        public static Result Failed(string error) => new(null, null, null, error);
    }

    public static async Task<Result> QueryAsync(string binaryPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in new[] { "-s", "read-only", "-a", "untrusted", "app-server" })
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            return Result.Failed("codex did not start.");

        // Kill-on-close job so a crash of Cadence cannot strand a codex.exe holding credentials.
        IDisposable? job = null;
        if (OperatingSystem.IsWindows())
        {
            var windowsJob = JobObject.CreateKillOnClose();
            windowsJob?.Assign(process);
            job = windowsJob;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var initialize = await CallAsync(process, id: 1, "initialize", new
            {
                clientInfo = new { name = "Cadence", version = "0.1.0" },
            }, InitializeTimeout, linked.Token).ConfigureAwait(false);

            if (initialize is null) return Result.Failed("Timed out waiting for the app-server to initialize.");

            var account = await CallAsync(process, id: 2, "account/read", new { }, MethodTimeout, linked.Token)
                .ConfigureAwait(false);

            var rateLimits = await CallAsync(process, id: 3, "account/rateLimits/read", new { }, MethodTimeout, linked.Token)
                .ConfigureAwait(false);

            if (rateLimits is null) return Result.Failed("The app-server did not return rate limits.");

            string? email = null, plan = null;
            if (account is { } accountJson)
            {
                using var document = JsonDocument.Parse(accountJson);
                email = ReadString(document.RootElement, "email");
                plan = ReadString(document.RootElement, "plan_type") ?? ReadString(document.RootElement, "planType");
            }

            return new Result(rateLimits, email, plan, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Result.Failed(Redaction.Scrub(e.Message));
        }
        finally
        {
            TryKill(process);
            job?.Dispose();
        }
    }

    private static async Task<string?> CallAsync(
        Process process, int id, string method, object parameters, TimeSpan timeout, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

        await process.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            // Skip notifications and responses to other ids until ours arrives.
            while (!deadline.Token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null) return null; // stream closed
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // banner or log line on stdout
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var responseId)) continue;
                    if (responseId.ValueKind is not JsonValueKind.Number || responseId.GetInt32() != id) continue;

                    if (root.TryGetProperty("error", out var error))
                        throw new InvalidOperationException($"{method}: {error}");

                    return root.TryGetProperty("result", out var result) ? result.GetRawText() : "{}";
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // our own deadline, not the caller's cancellation
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.ValueKind is JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
           v.ValueKind is JsonValueKind.String
            ? v.GetString()
            : null;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
        {
            // Already gone, or we lost the right to signal it. Nothing useful to do.
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadence.Core.Cost;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.Forecast;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Providers;
using Cadence.Core.Providers.Claude;
using Cadence.Core.Providers.Codex;
using Cadence.Core.Providers.Gemini;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadence.Cli;

/// <summary>
/// The headless face of Cadence.
/// </summary>
/// <remarks>
/// Exists so the whole core can be driven without a UI: it is how the provider layer gets verified
/// against live endpoints, and how anyone can wire Cadence into their own status bar. Reference
/// tools grew their plugin ecosystems entirely through an equivalent of <c>usage --json</c>.
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
            {
                "usage" => await UsageCommand(args, cancellation.Token).ConfigureAwait(false),
                "sources" => await SourcesCommand(args, cancellation.Token).ConfigureAwait(false),
                "cost" => await CostCommand(args, cancellation.Token).ConfigureAwait(false),
                "forecast" => await ForecastCommand(args, cancellation.Token).ConfigureAwait(false),
                "doctor" => await DoctorCommand(cancellation.Token).ConfigureAwait(false),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            cadence — AI quota monitor and forecaster

              cadence usage    [--provider claude|codex|gemini] [--json] [--verbose]
              cadence sources  [--provider ...]        what credentials this machine offers
              cadence cost     [--days 30] [--json]    local token/cost totals from session logs
              cadence forecast backtest --window <id> [--provider claude] [--days 30]
              cadence doctor                           paths, files and versions, redacted

            Cadence talks only to the providers' own APIs. It never sends anything anywhere else.
            """);

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'cadence --help'.");
        return 2;
    }

    // ---- usage ---------------------------------------------------------------------------------

    private static async Task<int> UsageCommand(string[] args, CancellationToken ct)
    {
        var asJson = HasFlag(args, "--json");
        var verbose = HasFlag(args, "--verbose");
        var only = ParseProvider(args);

        using var http = CreateHttpClient();
        var settings = await new SettingsStore().LoadAsync(ct).ConfigureAwait(false);
        var diagnostics = verbose ? new ConsoleDiagnostics() : null;

        var results = new List<UsageSnapshot>();

        foreach (var provider in BuildProviders())
        {
            if (only is { } id && provider.Id != id) continue;

            var context = new FetchContext(
                http, DateTimeOffset.UtcNow, settings.For(provider.Id), NullLogger.Instance, diagnostics);

            results.Add(await provider.FetchAsync(context, ct).ConfigureAwait(false));
        }

        if (results.Count == 0) return Fail("No matching provider.");

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, Json));
        }
        else
        {
            foreach (var snapshot in results) PrintSnapshot(snapshot);
        }

        // Non-zero when nothing at all worked, so shell pipelines can react.
        return results.Any(r => r.IsHealthy) ? 0 : 1;
    }

    private static void PrintSnapshot(UsageSnapshot snapshot)
    {
        var identity = snapshot.Identity;
        var header = snapshot.Provider.ToString();

        if (identity?.PlanLabel is { } plan) header += $"  {plan}";
        if (identity?.Email is { } email) header += $"  {email}";

        Console.WriteLine();
        Console.WriteLine(header);
        Console.WriteLine(new string('-', Math.Max(header.Length, 40)));

        if (snapshot.Error is { } error)
        {
            Console.WriteLine($"  {error.Kind}: {error.Message}");
            if (error.Detail is { Length: > 0 } detail) Console.WriteLine($"  {Redaction.Scrub(detail)}");
            if (snapshot.Windows.Count == 0) return;
            Console.WriteLine("  (showing last known values)");
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var window in snapshot.Windows)
        {
            // An en dash, never a zero: unknown is not the same as untouched.
            var used = window.UsedPercent is { } pct ? $"{pct,5:F1}%" : "    –";
            var resets = window.TimeUntilReset(now) is { } remaining ? $"resets in {Humanise(remaining)}" : "";

            Console.WriteLine($"  {window.Title,-28} {used}  {Bar(window.UsedPercent)}  {resets}");

            if (window.Forecast is { IsAvailable: true } forecast)
                Console.WriteLine($"  {"",-28} {ForecastLine(forecast, now)}");
        }

        if (snapshot.Spend is { } spend)
        {
            if (spend.SpentThisPeriod is not null || spend.PeriodLimit is not null)
                Console.WriteLine($"  {"Extra usage",-28} {MoneyFormat.FormatSpend(spend.SpentThisPeriod, spend.PeriodLimit, spend.Currency)}");
            if (spend.CreditBalance is { } credits)
                Console.WriteLine($"  {"Credits",-28} {MoneyFormat.Format(credits, spend.Currency)}");
        }

        Console.WriteLine($"  source: {snapshot.SourceLabel}");
    }

    private static string ForecastLine(Forecast forecast, DateTimeOffset now)
    {
        if (forecast.LeadWithExhaustion && forecast.ExhaustsAt is { } at)
        {
            return $"runs out ~{at.ToLocalTime():HH:mm} "
                   + $"({forecast.ExhaustProbability:P0} chance before reset)";
        }

        var band = $"{forecast.P10:F0}–{forecast.P90:F0}%";
        var pace = forecast.PaceDelta > 1 ? $", {forecast.PaceDelta:F0}pp in deficit"
            : forecast.PaceDelta < -1 ? $", {-forecast.PaceDelta:F0}pp in reserve"
            : "";

        return $"→ {forecast.Median:F0}% at reset  [{band}]{pace}";
    }

    private static string Bar(double? percent, int width = 20)
    {
        if (percent is not { } value) return new string('·', width);

        var filled = (int)Math.Round(Math.Clamp(value, 0, 100) / 100 * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    private static string Humanise(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        _ => $"{(int)span.TotalMinutes}m",
    };

    // ---- sources -------------------------------------------------------------------------------

    private static async Task<int> SourcesCommand(string[] args, CancellationToken ct)
    {
        var only = ParseProvider(args);

        foreach (var provider in BuildProviders())
        {
            if (only is { } id && provider.Id != id) continue;

            Console.WriteLine();
            Console.WriteLine(provider.Descriptor.DisplayName);

            foreach (var source in await provider.DiscoverSourcesAsync(ct).ConfigureAwait(false))
            {
                var mark = source.IsAvailable ? "available" : "not set up";
                Console.WriteLine($"  {source.StrategyLabel,-12} {mark,-12} {source.DisplayName}");
                if (source.Location is { Length: > 0 } location) Console.WriteLine($"  {"",-12} {location}");
            }
        }

        return 0;
    }

    // ---- cost ----------------------------------------------------------------------------------

    private static async Task<int> CostCommand(string[] args, CancellationToken ct)
    {
        var days = ParseInt(args, "--days") ?? 30;
        var asJson = HasFlag(args, "--json");

        await using var history = await HistoryRepository.OpenAsync(ct: ct).ConfigureAwait(false);
        var pricing = PricingTable.Load();

        var scanner = new CostScanner(history, pricing, NullLogger<CostScanner>.Instance);
        var report = await scanner.ScanAsync(TimeSpan.FromDays(days), DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
        var totals = await history.ReadCostTotalsAsync(since, ct: ct).ConfigureAwait(false);

        // The user's day, not UTC's: a UTC boundary reports near-zero all evening for anyone west
        // of Greenwich.
        var now = DateTimeOffset.Now;
        var today = await history.ReadCostTotalsAsync(
            new DateTimeOffset(now.Date, now.Offset), ct: ct).ConfigureAwait(false);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { days, scan = report, today, window = totals }, Json));
            return 0;
        }

        Console.WriteLine($"Scanned {report.FilesRead} of {report.FilesSeen} transcripts ({report.EntriesFound} new entries)");
        Console.WriteLine();
        Console.WriteLine($"  Today   {Tokens(today.TotalTokens),12}   {MoneyFormat.Format(today.CostUsd),10}");
        Console.WriteLine($"  {days,3}d    {Tokens(totals.TotalTokens),12}   {MoneyFormat.Format(totals.CostUsd),10}");
        Console.WriteLine();
        Console.WriteLine($"  input {Tokens(totals.InputTokens)} · output {Tokens(totals.OutputTokens)} "
                          + $"· cache read {Tokens(totals.CacheReadTokens)} · cache write {Tokens(totals.CacheWriteTokens)}");

        if (report.UnpricedModels.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  note: no price for {string.Join(", ", report.UnpricedModels)} — totals exclude them.");
            Console.WriteLine($"        add them to {PricingTable.OverridePath}");
        }

        return 0;
    }

    private static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000.0:F2}B",
        >= 1_000_000 => $"{value / 1_000_000.0:F2}M",
        >= 1_000 => $"{value / 1_000.0:F1}k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    // ---- forecast backtest -----------------------------------------------------------------------

    private static async Task<int> ForecastCommand(string[] args, CancellationToken ct)
    {
        if (args.Length < 2 || args[1] is not "backtest")
            return Fail("Usage: cadence forecast backtest --window <id> [--provider claude] [--days 30]");

        var windowId = ParseValue(args, "--window");
        if (windowId is null) return Fail("--window is required. Run 'cadence usage' to see window ids.");

        var provider = ParseProvider(args) ?? ProviderId.Claude;
        var days = ParseInt(args, "--days") ?? 30;

        await using var history = await HistoryRepository.OpenAsync(ct: ct).ConfigureAwait(false);
        var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
        var samples = await history.ReadSamplesAsync(provider, windowId, since, ct).ConfigureAwait(false);

        if (samples.Count < 10)
        {
            Console.Error.WriteLine(
                $"Only {samples.Count} samples stored for {provider}/{windowId}. Let Cadence run for a while first.");
            return 1;
        }

        var kind = windowId.Contains("five_hour", StringComparison.OrdinalIgnoreCase)
                   || windowId.Contains("primary", StringComparison.OrdinalIgnoreCase)
            ? WindowKind.Session
            : WindowKind.Weekly;

        var length = kind is WindowKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

        var blended = Backtester.Run(samples, windowId, kind, length,
            new ForecastOptions { Mode = EstimatorMode.Blended });
        var baseline = Backtester.Run(samples, windowId, kind, length,
            new ForecastOptions { Mode = EstimatorMode.EvenPaceOnly });

        Console.WriteLine($"Backtest {provider}/{windowId} over {days}d — {samples.Count} samples, {blended.EpochCount} complete epochs");
        Console.WriteLine();
        Console.WriteLine($"  {"",-22}{"blended",12}{"even-pace",12}");
        PrintRow("MAE (pp)", blended.MeanAbsoluteError, baseline.MeanAbsoluteError);
        PrintRow("median AE (pp)", blended.MedianAbsoluteError, baseline.MedianAbsoluteError);
        PrintRow("pinball P10", blended.PinballP10, baseline.PinballP10);
        PrintRow("pinball P50", blended.PinballP50, baseline.PinballP50);
        PrintRow("pinball P90", blended.PinballP90, baseline.PinballP90);
        PrintRow("P10–P90 coverage", blended.BandCoverage, baseline.BandCoverage);
        PrintRow("Brier", blended.BrierScore, baseline.BrierScore);
        Console.WriteLine($"  {"climatology Brier",-22}{blended.ClimatologyBrierScore,12:F4}");
        Console.WriteLine();

        // The decision the design demands be made on evidence rather than on preference.
        Console.WriteLine(blended.MeanAbsoluteError <= baseline.MeanAbsoluteError
            ? "  The blended estimator beats even-pace on your history."
            : "  Even-pace wins on your history. Turn the blend off in Settings → Forecast.");

        Console.WriteLine(blended.BeatsClimatology
            ? "  Exhaustion probabilities beat the base rate."
            : "  Exhaustion probabilities do not beat the base rate; treat them with suspicion.");

        Console.WriteLine($"  Band coverage is {blended.BandCoverage:P0} against an 80% target "
                          + $"({blended.CalibrationError:F0}pp off).");

        return 0;

        static void PrintRow(string label, double blended, double baseline)
            => Console.WriteLine($"  {label,-22}{blended,12:F4}{baseline,12:F4}");
    }

    // ---- doctor --------------------------------------------------------------------------------

    private static async Task<int> DoctorCommand(CancellationToken ct)
    {
        Console.WriteLine("Cadence doctor");
        Console.WriteLine();
        Console.WriteLine("Cadence storage");
        foreach (var (label, path) in new[]
                 {
                     ("config", KnownPaths.ConfigFile),
                     ("history", KnownPaths.HistoryDatabase),
                     ("logs", KnownPaths.LogDirectory),
                     ("pricing override", PricingTable.OverridePath),
                 })
        {
            Console.WriteLine($"  {label,-18} {(File.Exists(path) || Directory.Exists(path) ? "present" : "absent"),-8} {path}");
        }

        Console.WriteLine();
        Console.WriteLine("Provider files");
        foreach (var (label, path) in new[]
                 {
                     ("claude creds", KnownPaths.ClaudeCredentials),
                     ("claude account", KnownPaths.ClaudeAccountMetadata),
                     ("codex auth", KnownPaths.CodexAuth),
                     ("codex config", KnownPaths.CodexConfig),
                     ("gemini creds", KnownPaths.GeminiOAuthCredentials),
                 })
        {
            Console.WriteLine($"  {label,-18} {(File.Exists(path) ? "present" : "absent"),-8} {path}");
        }

        Console.WriteLine();
        Console.WriteLine("Session logs");
        foreach (var directory in KnownPaths.ClaudeProjectDirectories().Concat(KnownPaths.CodexSessionDirectories()))
        {
            var count = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories).Count()
                : 0;

            Console.WriteLine($"  {count,5} transcripts  {directory}");
        }

        Console.WriteLine();
        Console.WriteLine("Credential store");
        var store = CodexCredentialReader.ReadStoreSetting();
        Console.WriteLine($"  codex uses: {store}");

        Console.WriteLine();
        Console.WriteLine("Pricing");
        var pricing = PricingTable.Load();
        Console.WriteLine($"  {pricing.ModelCount} models, table dated {pricing.LastUpdated ?? "unknown"}");

        var settings = await new SettingsStore().LoadAsync(ct).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Settings schema v{settings.SchemaVersion}, cadence {settings.Cadence}");

        return 0;
    }

    // ---- shared --------------------------------------------------------------------------------

    private static IReadOnlyList<IUsageProvider> BuildProviders()
    {
        ISecretStore secrets = OperatingSystem.IsWindows()
            ? new DpapiSecretStore()
            : new InMemorySecretStore();

        return [new ClaudeProvider(secrets), new CodexProvider(), new GeminiProvider()];
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Identify honestly. Pretending to be a browser against undocumented endpoints is exactly
        // the behaviour that gets a whole class of tools blocked.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cadence/0.1 (+https://github.com/FanZhu1998/Cadence)");

        return http;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ParseValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? ParseInt(string[] args, string name)
        => int.TryParse(ParseValue(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static ProviderId? ParseProvider(string[] args)
        => Enum.TryParse<ProviderId>(ParseValue(args, "--provider"), ignoreCase: true, out var id) ? id : null;

    /// <summary>Prints the strategy chain and redacted responses for <c>--verbose</c>.</summary>
    private sealed class ConsoleDiagnostics : IDiagnosticsSink
    {
        public void Step(string strategy, string message)
            => Console.Error.WriteLine($"  [{strategy}] {Redaction.Scrub(message)}");

        public void Response(string strategy, int statusCode, string redactedBodyPrefix)
        {
            Console.Error.WriteLine($"  [{strategy}] HTTP {statusCode}");
            Console.Error.WriteLine($"  [{strategy}] {Redaction.Scrub(redactedBodyPrefix)}");
        }
    }
}

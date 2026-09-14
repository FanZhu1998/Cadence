using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using Cadence.App.Controls;
using Cadence.Core.Credentials;
using Cadence.Core.Forecast;
using Cadence.Core.History;
using Cadence.Core.Model;

namespace Cadence.App.History;

/// <summary>One row of the cost table.</summary>
public sealed record CostRow(string Day, string Model, string Tokens, string Cost);

/// <summary>
/// Usage over time, cost by day and model, and the forecast's own scorecard.
/// </summary>
/// <remarks>
/// The forecast-accuracy tab is the one that matters. Any tool can draw a projection; this is where
/// the user finds out whether to believe it, on their own data rather than on a claim in a README.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class HistoryWindow : Window
{
    private readonly HistoryRepository _history;
    private readonly IReadOnlyList<ProviderId> _providers;
    private bool _loading = true;

    public HistoryWindow(HistoryRepository history, IReadOnlyList<ProviderId> providers)
    {
        InitializeComponent();

        _history = history;
        _providers = providers;

        ProviderBox.ItemsSource = providers.Select(p => p.ToString()).ToArray();
        RangeBox.ItemsSource = new[] { "Last 7 days", "Last 30 days", "Last 90 days" };

        ProviderBox.SelectedIndex = 0;
        RangeBox.SelectedIndex = 1;

        ProviderBox.SelectionChanged += async (_, _) => await ReloadWindowsAsync().ConfigureAwait(true);
        WindowBox.SelectionChanged += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        RangeBox.SelectionChanged += async (_, _) => await ReloadAsync().ConfigureAwait(true);

        Loaded += async (_, _) =>
        {
            _loading = false;
            await ReloadWindowsAsync().ConfigureAwait(true);
        };
    }

    private ProviderId SelectedProvider =>
        ProviderBox.SelectedIndex >= 0 && ProviderBox.SelectedIndex < _providers.Count
            ? _providers[ProviderBox.SelectedIndex]
            : ProviderId.Claude;

    private string? SelectedWindowId => WindowBox.SelectedItem as string;

    private TimeSpan SelectedRange => RangeBox.SelectedIndex switch
    {
        0 => TimeSpan.FromDays(7),
        2 => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(30),
    };

    private async Task ReloadWindowsAsync()
    {
        if (_loading) return;

        var ids = await _history.ListWindowIdsAsync(SelectedProvider).ConfigureAwait(true);

        WindowBox.ItemsSource = ids;
        WindowBox.SelectedIndex = ids.Count > 0 ? 0 : -1;

        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        if (_loading) return;

        await LoadUsageAsync().ConfigureAwait(true);
        await LoadCostAsync().ConfigureAwait(true);
    }

    private async Task LoadUsageAsync()
    {
        if (SelectedWindowId is not { } windowId)
        {
            Chart.Series = [];
            UsageSummary.Text = "No usage recorded yet. Cadence stores a sample on every refresh.";
            return;
        }

        var since = DateTimeOffset.UtcNow - SelectedRange;
        var samples = await _history.ReadSamplesAsync(SelectedProvider, windowId, since).ConfigureAwait(true);

        var kind = InferKind(windowId);
        var epochs = EpochDetector.Split(samples, windowId, kind, InferLength(kind));

        // One series per epoch, so the line breaks at each reset instead of plunging through it.
        Chart.Series =
        [
            .. epochs
                .Where(e => e.Samples.Count > 1)
                .Select((e, index) => new UsageSeries(
                    $"epoch {index}",
                    [.. e.Samples.Where(s => s.UsedPercent is not null)
                        .Select(s => (s.Timestamp, s.UsedPercent!.Value))])),
        ];

        var completed = Math.Max(0, epochs.Count - 1);
        UsageSummary.Text =
            $"{samples.Count:N0} samples over {SelectedRange.TotalDays:N0} days, "
            + $"{epochs.Count} window{(epochs.Count == 1 ? string.Empty : "s")} "
            + $"({completed} complete). Vertical rules mark resets.";
    }

    private async Task LoadCostAsync()
    {
        var since = DateTimeOffset.UtcNow - SelectedRange;

        var totals = await _history.ReadCostTotalsAsync(since, SelectedProvider).ConfigureAwait(true);
        var daily = await _history.ReadDailyCostAsync(since, SelectedProvider).ConfigureAwait(true);

        CostSummary.Text =
            $"{Tokens(totals.TotalTokens)} tokens · {MoneyFormat.Format(totals.CostUsd)} at API list rates "
            + $"(input {Tokens(totals.InputTokens)}, output {Tokens(totals.OutputTokens)}, "
            + $"cache read {Tokens(totals.CacheReadTokens)}, cache write {Tokens(totals.CacheWriteTokens)})";

        CostGrid.ItemsSource = daily
            .OrderByDescending(d => d.Day)
            .ThenByDescending(d => d.CostUsd)
            .Select(d => new CostRow(
                d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.Model,
                Tokens(d.TotalTokens),
                MoneyFormat.Format(d.CostUsd)))
            .ToArray();
    }

    private async void OnRunBacktest(object sender, RoutedEventArgs e)
    {
        if (SelectedWindowId is not { } windowId)
        {
            BacktestBox.Text = "Select a window first.";
            return;
        }

        BacktestButton.IsEnabled = false;
        BacktestBox.Text = "Replaying history…";

        try
        {
            var since = DateTimeOffset.UtcNow - SelectedRange;
            var samples = await _history.ReadSamplesAsync(SelectedProvider, windowId, since).ConfigureAwait(true);

            if (samples.Count < 10)
            {
                BacktestBox.Text =
                    $"Only {samples.Count} samples stored for this window. "
                    + "Let Cadence run for a while — a backtest needs at least one complete window to score against.";
                return;
            }

            var kind = InferKind(windowId);
            var length = InferLength(kind);

            // Scoring happens off the UI thread: a 90-day replay is thousands of projections.
            var report = await Task.Run(() =>
            {
                var blended = Backtester.Run(samples, windowId, kind, length,
                    new ForecastOptions { Mode = EstimatorMode.Blended });
                var baseline = Backtester.Run(samples, windowId, kind, length,
                    new ForecastOptions { Mode = EstimatorMode.EvenPaceOnly });

                return Format(blended, baseline, windowId, samples.Count);
            }).ConfigureAwait(true);

            BacktestBox.Text = report;
        }
        catch (Exception ex)
        {
            BacktestBox.Text = ex.Message;
        }
        finally
        {
            BacktestButton.IsEnabled = true;
        }
    }

    private static string Format(BacktestResult blended, BacktestResult baseline, string windowId, int sampleCount)
    {
        var text = new StringBuilder();

        text.AppendLine($"{windowId} — {sampleCount:N0} samples, {blended.EpochCount} complete windows, "
                        + $"{blended.PointCount:N0} scored forecasts");
        text.AppendLine();

        if (blended.PointCount == 0)
        {
            text.AppendLine("No complete window has both a forecast and a known outcome yet.");
            return text.ToString();
        }

        text.AppendLine($"{"",-24}{"blended",12}{"even-pace",12}");
        Row("mean abs error (pp)", blended.MeanAbsoluteError, baseline.MeanAbsoluteError);
        Row("median abs error", blended.MedianAbsoluteError, baseline.MedianAbsoluteError);
        Row("pinball P10", blended.PinballP10, baseline.PinballP10);
        Row("pinball P50", blended.PinballP50, baseline.PinballP50);
        Row("pinball P90", blended.PinballP90, baseline.PinballP90);
        Row("P10-P90 coverage", blended.BandCoverage, baseline.BandCoverage);
        Row("Brier score", blended.BrierScore, baseline.BrierScore);

        text.AppendLine($"{"climatology Brier",-24}{blended.ClimatologyBrierScore,12:F4}");
        text.AppendLine();

        text.AppendLine(blended.MeanAbsoluteError <= baseline.MeanAbsoluteError
            ? "The blended estimator beats even-pace on your history."
            : "Even-pace wins on your history. Turn the blend off in Settings, Forecast.");

        text.AppendLine(blended.BeatsClimatology
            ? "Exhaustion probabilities beat the base rate."
            : "Exhaustion probabilities do not beat the base rate; treat them with suspicion.");

        text.AppendLine($"Band coverage is {blended.BandCoverage:P0} against an 80% target "
                        + $"({blended.CalibrationError:F0}pp off).");

        text.AppendLine();
        text.AppendLine("reliability (predicted vs observed exhaustion rate)");
        foreach (var bin in blended.Reliability.Where(b => b.Count > 0))
        {
            text.AppendLine($"  {bin.LowerBound:P0}-{bin.UpperBound:P0}  n={bin.Count,-5} "
                            + $"predicted {bin.MeanPredicted:P0}  observed {bin.ObservedFrequency:P0}");
        }

        return text.ToString();

        void Row(string label, double a, double b) => text.AppendLine($"{label,-24}{a,12:F4}{b,12:F4}");
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (SelectedWindowId is not { } windowId) return;

        try
        {
            var since = DateTimeOffset.UtcNow - SelectedRange;
            var samples = await _history.ReadSamplesAsync(SelectedProvider, windowId, since).ConfigureAwait(true);

            var path = Path.Combine(
                KnownPaths.DataDirectory,
                $"cadence-{SelectedProvider}-{Sanitise(windowId)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");

            var csv = new StringBuilder();
            csv.AppendLine("timestamp_utc,used_percent,used_units,resets_at_utc");

            foreach (var sample in samples)
            {
                csv.AppendLine(string.Join(',',
                    sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                    sample.UsedPercent?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty,
                    sample.UsedUnits?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    sample.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty));
            }

            await File.WriteAllTextAsync(path, csv.ToString()).ConfigureAwait(true);

            MessageBox.Show(this, $"Exported {samples.Count:N0} samples to:\n{path}",
                "Cadence", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cadence", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Sanitise(string value)
        => new([.. value.Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

    /// <summary>
    /// Guesses a window's kind from its id.
    /// </summary>
    /// <remarks>
    /// History rows store the id but not the kind, because the kind is a presentation decision that
    /// can change between releases while the stored samples stay valid.
    /// </remarks>
    private static WindowKind InferKind(string windowId)
        => windowId.Contains("five_hour", StringComparison.OrdinalIgnoreCase)
           || windowId.Contains("session", StringComparison.OrdinalIgnoreCase)
           || windowId.Contains("primary", StringComparison.OrdinalIgnoreCase)
            ? WindowKind.Session
            : WindowKind.Weekly;

    private static TimeSpan InferLength(WindowKind kind)
        => kind is WindowKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);

    private static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000.0:F2}B"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000.0:F2}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000.0:F1}k"),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}

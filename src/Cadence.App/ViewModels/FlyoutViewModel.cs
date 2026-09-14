using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Refresh;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadence.App.ViewModels;

/// <summary>One quota window as the flyout renders it.</summary>
public sealed partial class WindowRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private double? _used;
    [ObservableProperty] private string _usedText = "–";
    [ObservableProperty] private double? _forecastValue;
    [ObservableProperty] private double? _elapsedFraction;
    [ObservableProperty] private string _resetText = string.Empty;
    [ObservableProperty] private string _narrative = string.Empty;
    [ObservableProperty] private string _bandText = string.Empty;
    [ObservableProperty] private bool _isSecondary;
    [ObservableProperty] private bool _isWarning;
    [ObservableProperty] private string _tooltip = string.Empty;

    public static WindowRowViewModel From(QuotaWindow window, DateTimeOffset now)
    {
        var row = new WindowRowViewModel
        {
            Title = window.Title,
            Used = window.UsedPercent,

            // An en dash, never a zero. Unknown and untouched are different facts.
            UsedText = window.UsedPercent is { } pct
                ? string.Create(CultureInfo.InvariantCulture, $"{pct:F0}%")
                : "–",

            ElapsedFraction = window.ElapsedFraction(now),
            ResetText = ForecastNarrator.ResetText(window.ResetsAt, now).ToUpperInvariant(),
            Narrative = ForecastNarrator.Describe(window.Forecast, window.ResetsAt, now),
            BandText = ForecastNarrator.Band(window.Forecast),
            IsSecondary = window.SecondaryByDefault,
            IsWarning = window.UsedPercent >= 90 || window.Severity >= IncidentSeverity.Minor,
        };

        // Only draw a forecast that actually extends beyond current usage; a projection at or
        // below the fill would render as a sliver of hatch that reads as a rendering artefact.
        if (window.Forecast is { IsAvailable: true } forecast && window.UsedPercent is { } used
            && forecast.Median > used + 0.5)
        {
            row.ForecastValue = forecast.Median;
        }

        row.Tooltip = BuildTooltip(window, now);
        return row;
    }

    /// <summary>The numeric detail, kept out of the card and available on hover.</summary>
    private static string BuildTooltip(QuotaWindow window, DateTimeOffset now)
    {
        var lines = new List<string> { window.Title };

        if (window.UsedPercent is { } used)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{used:F1}% used"));

        if (window.UsedUnits is { } usedUnits && window.LimitUnits is { } limit)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{usedUnits:N0} of {limit:N0}"));

        if (window.ResetsAt is { } reset)
            lines.Add($"resets {reset.ToLocalTime():ddd d MMM HH:mm}");

        if (window.Forecast is { IsAvailable: true } f)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"projected {f.Median:F0}% (P10 {f.P10:F0}%, P90 {f.P90:F0}%)"));
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{f.BurnRatePerHour:F1}%/h now, {f.SustainableRatePerHour:F1}%/h sustainable"));
            lines.Add(ForecastNarrator.PaceToken(f));
            lines.Add($"confidence {f.Confidence.ToString().ToLowerInvariant()} · {f.SampleCount} samples");
        }

        return string.Join('\n', lines.Where(l => l.Length > 0));
    }
}

/// <summary>One provider's card in the flyout.</summary>
public sealed partial class ProviderCardViewModel : ObservableObject
{
    [ObservableProperty] private ProviderId _provider;

    /// <summary>Provider and plan on one tracked mono line, e.g. "CLAUDE · MAX 5X".</summary>
    [ObservableProperty] private string _eyebrow = string.Empty;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _shortName = string.Empty;
    [ObservableProperty] private string _accentHex = "#888888";
    [ObservableProperty] private string _planLabel = string.Empty;
    [ObservableProperty] private string _account = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _errorAction = string.Empty;
    [ObservableProperty] private string _spendText = string.Empty;
    [ObservableProperty] private bool _hasSpend;

    public ObservableCollection<WindowRowViewModel> Windows { get; } = [];

    public void Update(ProviderState state, ProviderDescriptorInfo descriptor, DateTimeOffset now)
    {
        Provider = state.Provider;
        DisplayName = descriptor.DisplayName;
        ShortName = descriptor.ShortName;
        AccentHex = descriptor.AccentHex;

        IsRefreshing = state.IsRefreshing;
        HasError = state.Error is not null;
        IsStale = state.IsStale(now, TimeSpan.FromMinutes(20));

        var snapshot = state.Snapshot;

        PlanLabel = snapshot?.Identity?.PlanLabel ?? string.Empty;
        Account = snapshot?.Identity?.Email ?? string.Empty;

        // Uppercased here rather than in XAML: the label is tracked monospace, and mixed case in a
        // widely letterspaced line reads as broken rather than as emphasis.
        Eyebrow = PlanLabel.Length > 0
            ? $"{descriptor.DisplayName.ToUpperInvariant()} · {PlanLabel.ToUpperInvariant()}"
            : descriptor.DisplayName.ToUpperInvariant();

        if (state.Error is { } error)
        {
            ErrorMessage = error.Message;
            ErrorAction = ActionFor(error.Kind);
        }
        else
        {
            ErrorMessage = string.Empty;
            ErrorAction = string.Empty;
        }

        StatusText = state.Error is { } e && state.Snapshot is null
            ? e.Message
            : ForecastNarrator.AgeText(state.Age(now)).ToUpperInvariant();

        if (snapshot?.Spend is { } spend && (spend.SpentThisPeriod is not null || spend.CreditBalance is not null))
        {
            HasSpend = true;
            SpendText = (spend.CreditBalance is { } credits
                ? $"{MoneyFormat.Format(credits, spend.Currency)} in credits"
                : $"Extra usage {MoneyFormat.FormatSpend(spend.SpentThisPeriod, spend.PeriodLimit, spend.Currency)}").ToUpperInvariant();
        }
        else
        {
            HasSpend = false;
            SpendText = string.Empty;
        }

        SyncWindows(snapshot?.Windows ?? [], now);
    }

    /// <summary>
    /// Reconciles rows in place rather than clearing and rebuilding.
    /// </summary>
    /// <remarks>
    /// Rebuilding the collection on every refresh makes the flyout flicker and loses scroll
    /// position, which is very visible at a one-minute cadence with the panel open.
    /// </remarks>
    private void SyncWindows(IReadOnlyList<QuotaWindow> windows, DateTimeOffset now)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            var row = WindowRowViewModel.From(windows[i], now);

            if (i < Windows.Count)
            {
                var existing = Windows[i];
                existing.Title = row.Title;
                existing.Used = row.Used;
                existing.UsedText = row.UsedText;
                existing.ForecastValue = row.ForecastValue;
                existing.ElapsedFraction = row.ElapsedFraction;
                existing.ResetText = row.ResetText;
                existing.Narrative = row.Narrative;
                existing.BandText = row.BandText;
                existing.IsSecondary = row.IsSecondary;
                existing.IsWarning = row.IsWarning;
                existing.Tooltip = row.Tooltip;
            }
            else
            {
                Windows.Add(row);
            }
        }

        while (Windows.Count > windows.Count) Windows.RemoveAt(Windows.Count - 1);
    }

    /// <summary>The one thing the user can do about this failure.</summary>
    private static string ActionFor(FetchErrorKind kind) => kind switch
    {
        FetchErrorKind.NotLoggedIn => "Sign in",
        FetchErrorKind.TokenExpired => "Sign in again",
        FetchErrorKind.ScopeMissing => "Re-authenticate",
        FetchErrorKind.NotRunning => "Open app",
        FetchErrorKind.TierDeprecated => "Change mode",
        _ => string.Empty,
    };
}

/// <summary>The parts of a provider descriptor the UI needs, without a Core reference in XAML.</summary>
public sealed record ProviderDescriptorInfo(string DisplayName, string ShortName, string AccentHex);

/// <summary>The flyout as a whole.</summary>
public sealed partial class FlyoutViewModel : ObservableObject
{
    private readonly UsageStore _store;
    private readonly IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> _descriptors;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly HistoryRepository? _history;

    [ObservableProperty] private string _todayText = "—";
    [ObservableProperty] private string _monthText = "—";
    [ObservableProperty] private bool _hasCost;
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];

    public FlyoutViewModel(
        UsageStore store,
        IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> descriptors,
        Func<CancellationToken, Task> refresh,
        HistoryRepository? history = null)
    {
        _store = store;
        _descriptors = descriptors;
        _refresh = refresh;
        _history = history;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ICommand RefreshCommand { get; }

    /// <summary>Rebuilds the card list from the store. Call on the UI thread.</summary>
    public void Sync(DateTimeOffset now)
    {
        var states = _store.All()
            .Where(s => _descriptors.ContainsKey(s.Provider))
            .OrderBy(s => s.Provider)
            .ToArray();

        for (var i = 0; i < states.Length; i++)
        {
            if (i >= Cards.Count) Cards.Add(new ProviderCardViewModel());

            Cards[i].Update(states[i], _descriptors[states[i].Provider], now);
        }

        while (Cards.Count > states.Length) Cards.RemoveAt(Cards.Count - 1);

        IsRefreshing = states.Any(s => s.IsRefreshing);
    }

    /// <summary>Refreshes the local cost totals shown at the foot of the flyout.</summary>
    public async Task RefreshCostAsync(CancellationToken ct = default)
    {
        if (_history is null) return;

        try
        {
            var now = DateTimeOffset.Now;

            // "Today" means the user's day, not UTC's. Using the UTC date reports almost nothing
            // all evening for anyone west of Greenwich — at 20:00 in New York the UTC day is
            // twenty-five minutes old.
            var startOfDay = new DateTimeOffset(now.Date, now.Offset);

            var today = await _history.ReadCostTotalsAsync(startOfDay, ct: ct).ConfigureAwait(true);
            var month = await _history.ReadCostTotalsAsync(now.AddDays(-30), ct: ct).ConfigureAwait(true);

            TodayText = Format(today);
            MonthText = Format(month);
            HasCost = today.TotalTokens > 0 || month.TotalTokens > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            HasCost = false;
        }

        static string Format(CostTotals totals)
            => $"{Tokens(totals.TotalTokens)} tokens · {MoneyFormat.Format(totals.CostUsd)}";
    }

    /// <summary>
    /// Explains what the money figure actually means.
    /// </summary>
    /// <remarks>
    /// These totals are computed from local transcripts at public API rates. On a subscription
    /// plan the user is not billed any of it — the number is "what this would have cost through
    /// the API", which is genuinely interesting and would be a lie presented as a bill.
    /// </remarks>
    public string CostDisclaimer => "at API list rates · subscription usage is not billed this way";

    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await _refresh(CancellationToken.None).ConfigureAwait(true);
            Sync(DateTimeOffset.UtcNow);
            await RefreshCostAsync().ConfigureAwait(true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static string Tokens(long value) => value switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000.0:F2}B"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000.0:F2}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000.0:F1}k"),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}

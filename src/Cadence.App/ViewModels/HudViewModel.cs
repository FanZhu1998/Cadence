using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.Core.Model;
using Cadence.Core.Refresh;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadence.App.ViewModels;

/// <summary>One provider's line in the HUD.</summary>
public sealed partial class HudRowViewModel : ObservableObject
{
    [ObservableProperty] private string _shortName = string.Empty;
    [ObservableProperty] private string _accentHex = "#888888";
    [ObservableProperty] private string _percentText = "–";
    [ObservableProperty] private double? _used;
    [ObservableProperty] private string _paceText = string.Empty;
    [ObservableProperty] private string _outlookText = string.Empty;
    [ObservableProperty] private bool _isWarning;
    [ObservableProperty] private bool _isCritical;
}

/// <summary>
/// The floating readout: the compact text the Windows tray cannot show.
/// </summary>
/// <remarks>
/// A macOS menu bar can render <c>Codex 42% · ↑11% · runs out 16:40</c> directly. The Windows
/// notification area gives one 16×16 square and no text at all, so that whole vocabulary has to
/// live somewhere else. This is that somewhere: an optional always-on-top strip the user puts
/// wherever they actually look.
/// <para>
/// Every token earns its place. At this size there is room for a percentage, a pace arrow and one
/// outlook phrase per provider, and nothing more.
/// </para>
/// </remarks>
public sealed partial class HudViewModel : ObservableObject
{
    private readonly UsageStore _store;
    private readonly IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> _descriptors;

    [ObservableProperty] private bool _isHovered;
    [ObservableProperty] private bool _hasAnyData;

    public HudViewModel(UsageStore store, IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> descriptors)
    {
        _store = store;
        _descriptors = descriptors;
    }

    public ObservableCollection<HudRowViewModel> Rows { get; } = [];

    /// <summary>Rebuilds the rows from the store. Call on the UI thread.</summary>
    public void Sync(DateTimeOffset now)
    {
        var states = _store.All()
            .Where(s => _descriptors.ContainsKey(s.Provider))
            .OrderBy(s => s.Provider)
            .ToArray();

        for (var i = 0; i < states.Length; i++)
        {
            if (i >= Rows.Count) Rows.Add(new HudRowViewModel());
            Update(Rows[i], states[i], now);
        }

        while (Rows.Count > states.Length) Rows.RemoveAt(Rows.Count - 1);

        HasAnyData = states.Any(s => WindowFor(s.Snapshot)?.UsedPercent is not null);
    }

    private void Update(HudRowViewModel row, ProviderState state, DateTimeOffset now)
    {
        var descriptor = _descriptors[state.Provider];

        row.ShortName = descriptor.ShortName;
        row.AccentHex = descriptor.AccentHex;

        var window = WindowFor(state.Snapshot);
        var used = window?.UsedPercent;

        row.Used = used;

        // An en dash for unknown, exactly as everywhere else. A HUD that shows 0% for an account
        // that reports nothing is the same lie in a smaller font.
        row.PercentText = used is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{value:F0}%")
            : "–";

        row.IsWarning = used >= 80;
        row.IsCritical = used >= 95 || state.Error is { IsTerminal: true };

        row.PaceText = PaceToken(window?.Forecast);
        row.OutlookText = OutlookToken(state, window, now);
    }

    /// <summary>
    /// Which of a provider's windows the HUD reports: the current session, and only that.
    /// </summary>
    /// <remarks>
    /// The HUD is a glance, and a glance should answer "can I keep working right now". Weekly and
    /// per-model windows answer a different question on a different timescale: a weekly model
    /// limit at 78% in the corner of the screen reads as an emergency that is days away. Those
    /// windows stay in the panel and the tray, where there is room to explain them. This is the
    /// one place to change if the HUD ever offers a choice of window.
    /// </remarks>
    internal static QuotaWindow? WindowFor(UsageSnapshot? snapshot) => snapshot?.SessionWindow;

    /// <summary>
    /// The pace arrow: how far ahead of or behind even consumption.
    /// </summary>
    /// <remarks>
    /// An arrow and a number rather than words, because this has to read at a glance from across a
    /// desk. Up means burning faster than the window can sustain.
    /// </remarks>
    internal static string PaceToken(Forecast? forecast)
    {
        if (forecast is not { IsAvailable: true } f) return string.Empty;

        var delta = Math.Abs(f.PaceDelta);
        if (delta < 1.5) return "→";

        return string.Create(CultureInfo.InvariantCulture, $"{(f.InDeficit ? '↑' : '↓')}{delta:F0}");
    }

    /// <summary>
    /// The right-hand phrase: an exhaustion time when there is one, otherwise the projection.
    /// </summary>
    internal static string OutlookToken(ProviderState state, QuotaWindow? window, DateTimeOffset now)
    {
        if (state.Error is { IsTerminal: true }) return "sign in";
        if (state.CircuitOpen) return "paused";
        // "…" means nothing has arrived yet. A provider that has reported but has no session
        // window has nothing for the HUD to say, which is not the same as still loading.
        if (window is null) return state.Error is not null ? "stale" : state.Snapshot is null ? "…" : string.Empty;

        if (window.Forecast is { IsAvailable: true } forecast)
        {
            // The thing worth interrupting someone for is a time, not a percentage.
            if (forecast.LeadWithExhaustion && forecast.ExhaustsAt is { } exhaustsAt)
            {
                var local = exhaustsAt.ToLocalTime();
                var sameDay = local.Date == now.ToLocalTime().Date;

                return sameDay
                    ? $"out {local:HH:mm}"
                    : $"out {local:ddd HH:mm}";
            }

            return string.Create(CultureInfo.InvariantCulture, $"→{forecast.Median:F0}%");
        }

        // No forecast yet, so fall back to the countdown, which is always true.
        var remaining = window.TimeUntilReset(now);
        return remaining is { } left ? Compact(left) : string.Empty;
    }

    /// <summary>A countdown short enough for a strip: "3h12", "2d", "18m".</summary>
    internal static string Compact(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d{span.Hours}h"),
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h{span.Minutes:D2}"),
        { TotalMinutes: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m"),
        _ => "now",
    };
}

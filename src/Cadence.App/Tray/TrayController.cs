using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Cadence.App.Interop;
using Cadence.App.ViewModels;
using Cadence.Core.Model;
using Cadence.Core.Refresh;
using H.NotifyIcon;

namespace Cadence.App.Tray;

/// <summary>
/// Owns the notification-area icons and keeps them in step with the store.
/// </summary>
/// <remarks>
/// Merged mode is the default on Windows: one icon with a switcher, because tray real estate is
/// far worse than a menu bar's and most people already have a crowded overflow flyout.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TrayController : IDisposable
{
    private readonly TrayIconRenderer _renderer = new();
    private readonly Dictionary<ProviderId, TaskbarIcon> _icons = [];

    /// <summary>Last state rendered per icon, so an unchanged refresh does no GDI work at all.</summary>
    private readonly Dictionary<TaskbarIcon, TrayIconState> _lastState = [];
    private readonly UsageStore _store;
    private readonly IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> _descriptors;

    private TaskbarIcon? _merged;
    private DisplaySettings _display = new();
    private bool _dark = true;
    private bool _disposed;

    /// <summary>Raised on a left click, with the clicked icon's screen rectangle when known.</summary>
    public event Action<Win32.Rect?>? Activated;

    public TrayController(UsageStore store, IReadOnlyDictionary<ProviderId, ProviderDescriptorInfo> descriptors)
    {
        _store = store;
        _descriptors = descriptors;
    }

    /// <summary>Icons rendered so far. The soak test asserts this stops growing.</summary>
    public int RenderCount => _renderer.RenderCount;

    /// <summary>An icon notifications can be raised through. Any registered icon will do.</summary>
    public TaskbarIcon? NotificationHost => _merged ?? _icons.Values.FirstOrDefault();

    public void Initialize(DisplaySettings display, bool dark)
    {
        _display = display;
        _dark = dark;

        Rebuild();
        Refresh();
    }

    /// <summary>Applies new display settings, rebuilding icons only when the layout actually changes.</summary>
    public void UpdateSettings(DisplaySettings display, bool dark)
    {
        var layoutChanged = display.MergeIcons != _display.MergeIcons;
        var repaintNeeded = layoutChanged
                            || display.IconStyle != _display.IconStyle
                            || display.ShowRemaining != _display.ShowRemaining
                            || dark != _dark;

        _display = display;
        _dark = dark;

        if (layoutChanged) Rebuild();

        // A theme or style change invalidates every cached bitmap.
        if (repaintNeeded) _renderer.Flush();

        Refresh();
    }

    /// <summary>Re-renders every icon from current store state.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        var size = Win32.SnapIconSize(Win32.SmallIconSize());

        if (_display.MergeIcons)
        {
            RefreshMerged(size);
            return;
        }

        foreach (var (provider, icon) in _icons)
        {
            var state = _store.Get(provider);
            Apply(icon, state, _descriptors.GetValueOrDefault(provider), size);
        }
    }

    private void RefreshMerged(int size)
    {
        if (_merged is null) return;

        var states = _store.All().Where(s => _descriptors.ContainsKey(s.Provider)).ToArray();

        // The merged icon shows the provider under most pressure: the one the user is about to
        // have a problem with, not an average that hides it.
        var worst = states
            .Where(s => s.Snapshot?.PrimaryWindow?.UsedPercent is not null)
            .OrderByDescending(s => s.Snapshot!.PrimaryWindow!.UsedPercent)
            .FirstOrDefault();

        if (worst is null)
        {
            var first = states.FirstOrDefault();
            Apply(_merged, first ?? new ProviderState { Provider = ProviderId.Claude },
                first is null ? null : _descriptors.GetValueOrDefault(first.Provider), size, states);
            return;
        }

        Apply(_merged, worst, _descriptors.GetValueOrDefault(worst.Provider), size, states);
    }

    private void Apply(
        TaskbarIcon icon,
        ProviderState state,
        ProviderDescriptorInfo? descriptor,
        int size,
        IReadOnlyList<ProviderState>? allStates = null)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = state.Snapshot;

        var primary = snapshot?.PrimaryWindow?.UsedPercent;
        var secondary = snapshot?.Windows
            .Where(w => w.UsageKnown && w.Id != snapshot.PrimaryWindow?.Id)
            .OrderByDescending(w => w.UsedPercent)
            .FirstOrDefault()?.UsedPercent;

        var iconState = new TrayIconState(
            Style: _display.IconStyle,
            Primary: primary,
            Secondary: secondary,
            Dark: _dark,
            Size: size,
            AccentHex: descriptor?.AccentHex ?? "#888888",
            Glyph: descriptor?.ShortName,
            Stale: state.IsStale(now, TimeSpan.FromMinutes(20)),
            Error: state.Error is { IsTerminal: true } || state.CircuitOpen,
            Incident: snapshot?.Status is { Severity: > IncidentSeverity.None },
            ShowRemaining: _display.ShowRemaining);

        // Skip the swap entirely when nothing visible changed. At a one-minute cadence this is the
        // difference between a handful of GDI operations a day and fifteen hundred.
        if (_lastState.TryGetValue(icon, out var previousState) && previousState.Equals(iconState))
        {
            icon.ToolTipText = BuildTooltip(allStates ?? [state], now);
            return;
        }

        var replaced = icon.Icon;

        icon.Icon = _renderer.Get(iconState);
        icon.ToolTipText = BuildTooltip(allStates ?? [state], now);
        _lastState[icon] = iconState;

        // The renderer hands out owned clones, so the outgoing handle is ours to free. Leaking one
        // per refresh exhausts the 10,000-object GDI quota and the icon silently disappears.
        replaced?.Dispose();
    }

    /// <summary>
    /// The long-form text Windows shows on hover.
    /// </summary>
    /// <remarks>
    /// This carries everything the macOS menu bar would have shown as a text label. It is the main
    /// consolation for the tray having no room for text, so it is worth writing well.
    /// </remarks>
    private string BuildTooltip(IReadOnlyList<ProviderState> states, DateTimeOffset now)
    {
        var builder = new StringBuilder();

        foreach (var state in states.OrderBy(s => s.Provider))
        {
            var name = _descriptors.GetValueOrDefault(state.Provider)?.DisplayName ?? state.Provider.ToString();

            if (state.Snapshot is not { Windows.Count: > 0 } snapshot)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"{name} — {state.Error?.Message ?? "no data yet"}");
                continue;
            }

            if (snapshot.PrimaryWindow is { } window)
            {
                var used = window.UsedPercent is { } pct
                    ? string.Create(CultureInfo.InvariantCulture, $"{pct:F0}% used")
                    : "usage unknown";

                var reset = ForecastNarrator.ResetText(window.ResetsAt, now);
                var line = $"{name} — {window.Title} {used}";
                if (reset.Length > 0) line += $", {reset}";

                builder.AppendLine(line);

                if (window.Forecast is { IsAvailable: true } forecast)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture,
                        $"   projected {forecast.Median:F0}% at reset");
                }
            }

            if (state.IsStale(now, TimeSpan.FromMinutes(20)))
                builder.AppendLine(CultureInfo.InvariantCulture, $"   last updated {ForecastNarrator.AgeText(state.Age(now))}");
        }

        var text = builder.ToString().TrimEnd();

        // Windows truncates a tooltip at 127 characters on older shells; keep it inside that.
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    private void Rebuild()
    {
        DisposeIcons();

        if (_display.MergeIcons)
        {
            _merged = CreateIcon("Cadence");
            return;
        }

        foreach (var (provider, descriptor) in _descriptors)
            _icons[provider] = CreateIcon($"Cadence — {descriptor.DisplayName}");
    }

    private TaskbarIcon CreateIcon(string name)
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = name,
            Visibility = System.Windows.Visibility.Visible,
            NoLeftClickDelay = true,
        };

        icon.TrayLeftMouseUp += (_, _) => Activated?.Invoke(TryGetRect(icon));
        icon.ForceCreate(enablesEfficiencyMode: false);

        return icon;
    }

    /// <summary>
    /// The icon's screen rectangle, or null when the shell will not say.
    /// </summary>
    /// <remarks>
    /// Returns null while the icon is inside the overflow flyout, which is common. The flyout has
    /// a corner fallback for exactly that case.
    /// </remarks>
    private static Win32.Rect? TryGetRect(TaskbarIcon icon)
    {
        try
        {
            var tray = icon.TrayIcon;
            if (tray is null) return null;

            // Registered by GUID, so it has to be looked up by GUID.
            var rect = Win32.GetTrayIconRect(tray.Id);
            if (rect is not null) return rect;

            return tray.WindowHandle != nint.Zero ? Win32.GetTrayIconRect(tray.WindowHandle, 0) : null;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void DisposeIcons()
    {
        foreach (var icon in _icons.Values) icon.Icon?.Dispose();
        _merged?.Icon?.Dispose();
        _lastState.Clear();

        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();

        _merged?.Dispose();
        _merged = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeIcons();

        // Must outlive the icons: destroying a handle the shell still references is what makes a
        // ghost icon linger in the tray until it is hovered.
        _renderer.Dispose();
    }
}

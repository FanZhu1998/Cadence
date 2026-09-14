using System.Text.Json.Serialization;

namespace Cadence.Core.Model;

public enum RefreshCadence { Adaptive, Manual, Fixed1, Fixed2, Fixed5, Fixed15, Fixed30 }

public enum TrayIconStyle { TwoBar, Numeric, BarWithIncident, Ring }

public enum ThemeMode { System, Light, Dark }

/// <summary>Per-provider configuration.</summary>
public sealed record ProviderSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Pin one strategy by label and disable fallback. Null means walk the whole chain.</summary>
    public string? PinnedStrategy { get; init; }

    /// <summary>DPAPI-protected blob for a user-pasted token. Never plaintext.</summary>
    public string? ManualTokenProtected { get; init; }

    /// <summary>DPAPI-protected admin API key, enabling the officially supported reporting route.</summary>
    public string? AdminKeyProtected { get; init; }

    /// <summary>Window ids the user has hidden.</summary>
    public IReadOnlyList<string> HiddenWindowIds { get; init; } = [];

    /// <summary>Gemini only: which of the three modes to use. See the Gemini provider.</summary>
    public string? Mode { get; init; }

    public static readonly ProviderSettings Default = new();
}

public sealed record DisplaySettings
{
    public TrayIconStyle IconStyle { get; init; } = TrayIconStyle.TwoBar;

    /// <summary>One merged icon with a provider switcher, rather than one icon per provider.</summary>
    public bool MergeIcons { get; init; } = true;

    /// <summary>Show remaining rather than used. Some people think in headroom.</summary>
    public bool ShowRemaining { get; init; }

    public bool ShowHud { get; init; }

    /// <summary>
    /// Keeps the HUD permanently click-through, so it can never intercept a click.
    /// </summary>
    /// <remarks>
    /// Unlocked, the strip becomes interactive while the cursor is over it, which means you cannot
    /// click something directly behind it. Locking makes it a pure readout at the cost of being
    /// unable to drag it.
    /// </remarks>
    public bool HudLocked { get; init; }

    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>HUD position, remembered per monitor layout signature.</summary>
    public IReadOnlyDictionary<string, HudPlacement> HudPlacements { get; init; } =
        new Dictionary<string, HudPlacement>();
}

public sealed record HudPlacement(double Left, double Top);

/// <summary>Which notifications to show. How often they may appear is <see cref="Alerts.UsageAlertPolicy"/>'s job, not a setting.</summary>
public sealed record NotificationSettings
{
    /// <summary>Usage reaches 50, 75, 90, 95, 98 or 100 percent, each announced once per window until it resets.</summary>
    public bool OnUsageThresholds { get; init; } = true;

    public bool OnForecastExhaustion { get; init; } = true;

    public bool OnWeeklyReset { get; init; }

    public bool OnCredentialsExpired { get; init; } = true;

    public bool OnProviderIncident { get; init; }

    public bool OnCreditsExpiring { get; init; } = true;
}

public sealed record CostSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Rolling retention for scanned session entries, 1-365 days.</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>Minimum gap between full scans.</summary>
    public TimeSpan MinimumScanInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The whole persisted configuration.
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> exists from v1 precisely because this model will change. A config
/// written by a later build is left alone rather than silently downgraded.
/// </remarks>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool LaunchAtStartup { get; init; }

    /// <summary>
    /// Set once the first-launch welcome has been seen, so it appears exactly once.
    /// </summary>
    public bool FirstRunCompleted { get; init; }

    public RefreshCadence Cadence { get; init; } = RefreshCadence.Adaptive;

    public DisplaySettings Display { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public CostSettings Cost { get; init; } = new();

    public ForecastSettings Forecast { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public IReadOnlyDictionary<string, ProviderSettings> Providers { get; init; } =
        new Dictionary<string, ProviderSettings>
        {
            [nameof(ProviderId.Claude)] = ProviderSettings.Default,
            [nameof(ProviderId.Codex)] = ProviderSettings.Default,
            [nameof(ProviderId.Gemini)] = ProviderSettings.Default with { Enabled = false, Mode = "antigravity" },
        };

    public ProviderSettings For(ProviderId id) =>
        Providers.TryGetValue(id.ToString(), out var s) ? s : ProviderSettings.Default;

    public static readonly AppSettings Default = new();
}

public sealed record ForecastSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Use the credibility blend, or fall back to plain even-pace.</summary>
    public bool UseBlendedEstimator { get; init; } = true;

    /// <summary>Learn the weekly working rhythm rather than assuming a flat one.</summary>
    public bool UseIntensityProfile { get; init; } = true;

    /// <summary>Manual working-hours-per-day override; null learns it from history.</summary>
    public double? WorkHoursPerDay { get; init; }

    public bool ShowBands { get; init; } = true;
}

public sealed record UpdateSettings
{
    /// <summary>
    /// Check the release feed in the background and download new versions as they appear. A
    /// downloaded version installs the next time Cadence starts.
    /// </summary>
    public bool CheckAutomatically { get; init; } = true;
}

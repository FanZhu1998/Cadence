using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.Model;
using Cadence.App.Updates;
using Cadence.Core.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using ThemeMode = Cadence.Core.Model.ThemeMode;

namespace Cadence.App.Settings;

/// <summary>
/// The settings window, including the connection tester.
/// </summary>
/// <remarks>
/// Settings are applied and saved as they change rather than behind an OK button: there is no
/// coherent "cancel" for a tray app whose state is already live.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class SettingsWindow : Window
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly UpdateService? _updates;
    private AppSettings _settings;
    private bool _loading = true;

    /// <summary>Raised whenever settings change, so the host can apply them live.</summary>
    public event Action<AppSettings>? SettingsChanged;

    public SettingsWindow(
        AppSettings settings, SettingsStore store, IReadOnlyList<IUsageProvider> providers, HttpClient http,
        UpdateService? updates = null)
    {
        InitializeComponent();

        _updates = updates;

        _settings = settings;
        _store = store;
        _providers = providers;
        _http = http;

        PopulateChoices();
        LoadFrom(settings);
        WireChangeHandlers();

        if (_updates is not null)
        {
            _updates.StatusChanged += OnUpdateStatusChanged;
            Closed += (_, _) => _updates.StatusChanged -= OnUpdateStatusChanged;
        }

        ShowUpdateStatus(_updates?.Status ?? new UpdateStatus(UpdateState.Unavailable));

        _loading = false;
    }

    private void PopulateChoices()
    {
        CadenceBox.ItemsSource = new[]
        {
            "Adaptive (recommended)", "Every minute", "Every 2 minutes", "Every 5 minutes",
            "Every 15 minutes", "Every 30 minutes", "Manual only",
        };

        ThemeBox.ItemsSource = new[] { "Follow Windows", "Light", "Dark" };
        IconStyleBox.ItemsSource = new[] { "Two bars", "Number", "Bar with incident dot", "Ring" };

        WorkHoursBox.ItemsSource = new[] { "Learn it from my history", "4 hours", "6 hours", "8 hours", "10 hours", "12 hours", "24 hours" };

        ProviderBox.ItemsSource = _providers.Select(p => p.Descriptor.DisplayName).ToArray();
        ProviderBox.SelectedIndex = 0;

        PathsBox.Text = string.Join(Environment.NewLine,
        [
            $"config   {KnownPaths.ConfigFile}",
            $"history  {KnownPaths.HistoryDatabase}",
            $"logs     {KnownPaths.LogDirectory}",
        ]);
    }

    private void LoadFrom(AppSettings settings)
    {
        CadenceBox.SelectedIndex = settings.Cadence switch
        {
            RefreshCadence.Adaptive => 0,
            RefreshCadence.Fixed1 => 1,
            RefreshCadence.Fixed2 => 2,
            RefreshCadence.Fixed5 => 3,
            RefreshCadence.Fixed15 => 4,
            RefreshCadence.Fixed30 => 5,
            _ => 6,
        };

        ThemeBox.SelectedIndex = (int)settings.Display.Theme;
        IconStyleBox.SelectedIndex = (int)settings.Display.IconStyle;
        MergeIconsCheck.IsChecked = settings.Display.MergeIcons;
        ShowRemainingCheck.IsChecked = settings.Display.ShowRemaining;
        ShowHudCheck.IsChecked = settings.Display.ShowHud;
        LockHudCheck.IsChecked = settings.Display.HudLocked;
        // The Run entry is the truth, not the saved preference: uninstalling, or another copy of
        // Cadence, can change it behind this one's back. Showing the preference instead would claim
        // autostart is on when it is not, and the next change to any setting would quietly re-create it.
        StartupCheck.IsChecked = Launch.StartupIntegration.IsLaunchAtSignInEnabled();
        AutoUpdateCheck.IsChecked = settings.Updates.CheckAutomatically;

        ForecastEnabledCheck.IsChecked = settings.Forecast.Enabled;
        BlendedCheck.IsChecked = settings.Forecast.UseBlendedEstimator;
        ProfileCheck.IsChecked = settings.Forecast.UseIntensityProfile;
        BandsCheck.IsChecked = settings.Forecast.ShowBands;

        WorkHoursBox.SelectedIndex = settings.Forecast.WorkHoursPerDay switch
        {
            null => 0, 4 => 1, 6 => 2, 8 => 3, 10 => 4, 12 => 5, 24 => 6, _ => 0,
        };

        ThresholdsCheck.IsChecked = settings.Notifications.OnUsageThresholds;
        ExhaustCheck.IsChecked = settings.Notifications.OnForecastExhaustion;
        ResetCheck.IsChecked = settings.Notifications.OnWeeklyReset;
        AuthCheck.IsChecked = settings.Notifications.OnCredentialsExpired;
        IncidentCheck.IsChecked = settings.Notifications.OnProviderIncident;
        CreditsCheck.IsChecked = settings.Notifications.OnCreditsExpiring;

        CostEnabledCheck.IsChecked = settings.Cost.Enabled;

        UpdateProviderPane();
    }

    private void WireChangeHandlers()
    {
        foreach (var box in new[] { CadenceBox, ThemeBox, IconStyleBox, WorkHoursBox })
            box.SelectionChanged += (_, _) => Apply();

        ProviderBox.SelectionChanged += (_, _) => UpdateProviderPane();

        foreach (var check in new[]
                 {
                     MergeIconsCheck, ShowRemainingCheck, ShowHudCheck, LockHudCheck, StartupCheck, AutoUpdateCheck, ForecastEnabledCheck, BlendedCheck,
                     ProfileCheck, BandsCheck, ThresholdsCheck, ExhaustCheck, ResetCheck, AuthCheck,
                     IncidentCheck, CreditsCheck, CostEnabledCheck, ProviderEnabledCheck,
                 })
        {
            check.Checked += (_, _) => Apply();
            check.Unchecked += (_, _) => Apply();
        }
    }

    private void UpdateProviderPane()
    {
        if (SelectedProvider() is not { } provider) return;

        ProviderEnabledCheck.IsChecked = _settings.For(provider.Id).Enabled;
    }

    private IUsageProvider? SelectedProvider()
        => ProviderBox.SelectedIndex >= 0 && ProviderBox.SelectedIndex < _providers.Count
            ? _providers[ProviderBox.SelectedIndex]
            : null;

    private async void Apply()
    {
        if (_loading) return;

        var providers = _settings.Providers.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (SelectedProvider() is { } provider)
        {
            providers[provider.Id.ToString()] =
                _settings.For(provider.Id) with { Enabled = ProviderEnabledCheck.IsChecked is true };
        }

        _settings = _settings with
        {
            Cadence = CadenceBox.SelectedIndex switch
            {
                0 => RefreshCadence.Adaptive,
                1 => RefreshCadence.Fixed1,
                2 => RefreshCadence.Fixed2,
                3 => RefreshCadence.Fixed5,
                4 => RefreshCadence.Fixed15,
                5 => RefreshCadence.Fixed30,
                _ => RefreshCadence.Manual,
            },

            LaunchAtStartup = StartupCheck.IsChecked is true,

            Display = _settings.Display with
            {
                Theme = (ThemeMode)Math.Max(0, ThemeBox.SelectedIndex),
                IconStyle = (TrayIconStyle)Math.Max(0, IconStyleBox.SelectedIndex),
                MergeIcons = MergeIconsCheck.IsChecked is true,
                ShowRemaining = ShowRemainingCheck.IsChecked is true,
                ShowHud = ShowHudCheck.IsChecked is true,
                HudLocked = LockHudCheck.IsChecked is true,
            },

            Forecast = _settings.Forecast with
            {
                Enabled = ForecastEnabledCheck.IsChecked is true,
                UseBlendedEstimator = BlendedCheck.IsChecked is true,
                UseIntensityProfile = ProfileCheck.IsChecked is true,
                ShowBands = BandsCheck.IsChecked is true,
                WorkHoursPerDay = WorkHoursBox.SelectedIndex switch
                {
                    1 => 4, 2 => 6, 3 => 8, 4 => 10, 5 => 12, 6 => 24, _ => null,
                },
            },

            Notifications = _settings.Notifications with
            {
                OnUsageThresholds = ThresholdsCheck.IsChecked is true,
                OnForecastExhaustion = ExhaustCheck.IsChecked is true,
                OnWeeklyReset = ResetCheck.IsChecked is true,
                OnCredentialsExpired = AuthCheck.IsChecked is true,
                OnProviderIncident = IncidentCheck.IsChecked is true,
                OnCreditsExpiring = CreditsCheck.IsChecked is true,
            },

            Cost = _settings.Cost with { Enabled = CostEnabledCheck.IsChecked is true },
            Updates = _settings.Updates with { CheckAutomatically = AutoUpdateCheck.IsChecked is true },
            Providers = providers,
        };

        ApplyStartupRegistration(_settings.LaunchAtStartup);

        SettingsChanged?.Invoke(_settings);
        await _store.SaveAsync(_settings).ConfigureAwait(true);
    }

    /// <summary>
    /// Adds or removes the Run-key entry.
    /// </summary>
    /// <remarks>
    /// HKCU rather than HKLM, so this never needs elevation and only affects this user.
    /// </remarks>
    private static void ApplyStartupRegistration(bool enabled) => Launch.StartupIntegration.SetLaunchAtSignIn(enabled);

    // ---- updates ------------------------------------------------------------------------------------

    private void OnUpdateStatusChanged(UpdateStatus status) => Dispatcher.BeginInvoke(() => ShowUpdateStatus(status));

    private void ShowUpdateStatus(UpdateStatus status)
    {
        var current = _updates?.CurrentVersion ?? BuildVersion;

        UpdateStatusText.Text = status.State switch
        {
            UpdateState.Unavailable => $"Version {current}",
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => $"Version {current} is the latest",
            UpdateState.Downloading => $"Downloading version {status.Version}…",
            UpdateState.ReadyToInstall => $"Version {status.Version} is ready to install",
            UpdateState.Failed => "Couldn't check for updates. The log has the details.",
            _ => $"Version {current}",
        };

        UpdateExplainer.Text = status.State is UpdateState.Unavailable
            ? "This copy runs straight from its exe rather than being installed, so it never changes itself. Install Cadence with its Setup to get updates."
            : "New versions come from Cadence's releases on GitHub. One downloads in the background and installs the next time Cadence starts, usually when you next sign in. Restart now installs it straight away.";

        UpdateButton.Content = status.State is UpdateState.ReadyToInstall ? "Restart now" : "Check now";
        UpdateButton.IsEnabled = status.State is not (UpdateState.Unavailable or UpdateState.Checking or UpdateState.Downloading);
        AutoUpdateCheck.IsEnabled = status.State is not UpdateState.Unavailable;
    }

    private async void OnUpdateButton(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        if (_updates.Status.State is UpdateState.ReadyToInstall)
        {
            _updates.InstallNow(Application.Current.Shutdown);
            return;
        }

        await _updates.CheckNowAsync().ConfigureAwait(true);
    }

    /// <summary>The version this build was compiled as, for copies the installer does not manage.</summary>
    private static string BuildVersion =>
        typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    // ---- connection test ---------------------------------------------------------------------------

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        if (SelectedProvider() is not { } provider) return;

        TestButton.IsEnabled = false;
        var sink = new TextDiagnostics();

        sink.Step("cadence", $"testing {provider.Descriptor.DisplayName}");

        try
        {
            foreach (var source in await provider.DiscoverSourcesAsync(CancellationToken.None).ConfigureAwait(true))
            {
                sink.Step(source.StrategyLabel,
                    $"{(source.IsAvailable ? "available" : "not set up")}  {source.Location ?? string.Empty}");
            }

            var context = new FetchContext(
                _http, DateTimeOffset.UtcNow, _settings.For(provider.Id), NullLogger.Instance, sink);

            var snapshot = await provider.FetchAsync(context, CancellationToken.None).ConfigureAwait(true);

            sink.Step("result", snapshot.Error is { } error
                ? $"{error.Kind}: {error.Message}"
                : $"ok, {snapshot.Windows.Count} windows via {snapshot.SourceLabel}");

            foreach (var window in snapshot.Windows)
            {
                var used = window.UsedPercent is { } pct ? $"{pct:F1}%" : "unknown";
                sink.Step("window", $"{window.Id,-34} {used,8}  resets {window.ResetsAt:u}");
            }
        }
        catch (Exception ex)
        {
            sink.Step("error", ex.Message);
        }
        finally
        {
            DiagnosticsBox.Text = sink.ToString();
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Collects the strategy chain and redacted responses into readable text.</summary>
    private sealed class TextDiagnostics : IDiagnosticsSink
    {
        private readonly StringBuilder _builder = new();

        public void Step(string strategy, string message)
            => _builder.AppendLine($"[{strategy}] {Redaction.Scrub(message)}");

        public void Response(string strategy, int statusCode, string redactedBodyPrefix)
        {
            _builder.AppendLine($"[{strategy}] HTTP {statusCode}");
            _builder.AppendLine(Redaction.Scrub(redactedBodyPrefix));
            _builder.AppendLine();
        }

        public override string ToString() => _builder.ToString();
    }

    // ---- advanced actions ----------------------------------------------------------------------------

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) => OpenFolder(KnownPaths.ConfigDirectory);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(KnownPaths.LogDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            // Nothing useful to do if the shell refuses to open a folder.
        }
    }

    private void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DiagnosticsBundle.Write(_settings);

            MessageBox.Show(this,
                $"Diagnostics written to:\n{path}\n\nEvery token has been stripped.",
                "Cadence", MessageBoxButton.OK, MessageBoxImage.Information);

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cadence", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Forgets every remembered HUD position, so it returns to the default corner.</summary>
    private async void OnResetHudPosition(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            Display = _settings.Display with { HudPlacements = new Dictionary<string, HudPlacement>() },
        };

        SettingsChanged?.Invoke(_settings);
        await _store.SaveAsync(_settings).ConfigureAwait(true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

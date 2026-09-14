using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Cadence.App.Flyout;
using Cadence.App.Refresh;
using Cadence.App.Theme;
using Cadence.App.Tray;
using Cadence.App.ViewModels;
using Cadence.Core.Cost;
using Cadence.Core.Credentials;
using Cadence.Core.Diagnostics;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Providers;
using Cadence.Core.Providers.Claude;
using Cadence.Core.Providers.Codex;
using Cadence.Core.Providers.Gemini;
using Cadence.Core.Refresh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;

namespace Cadence.App;

/// <summary>
/// Application host: composes the core, owns the tray and flyout, and shuts everything down cleanly.
/// </summary>
/// <remarks>
/// Runs with no main window. A named mutex enforces single instance, because two copies would poll
/// the same private endpoints twice as often for no benefit.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class App : Application
{
    private Launch.SingleInstance? _singleInstance;
    private Updates.UpdateService? _updates;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private HttpClient? _http;
    private HistoryRepository? _history;
    private UsageStore? _store;
    private RefreshCoordinator? _coordinator;
    private RefreshScheduler? _scheduler;
    private TrayController? _tray;
    private FlyoutWindow? _flyout;
    private ThemeManager? _theme;
    private SettingsStore? _settingsStore;
    private AppSettings _settings = AppSettings.Default;
    private DispatcherTimer? _uiTick;
    private CostScanner? _costScanner;
    private Settings.SettingsWindow? _settingsWindow;
    private History.HistoryWindow? _historyWindow;
    private Hud.HudWindow? _hud;
    private ViewModels.HudViewModel? _hudViewModel;
    private Notifications.NotificationManager? _notifier;
    private IReadOnlyList<IUsageProvider> _providers = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Screenshot runs render one surface and exit, so they must also work while the real copy
        // is running: they neither take the lock nor wake that copy.
        if (!IsAutomationLaunch() && !ClaimSingleInstance())
        {
            // A second launch is a request to see the panel, not to run a second copy. Hand that
            // request to the copy already running and step aside. Before this, double-clicking the
            // exe again did nothing visible at all.
            _singleInstance?.SignalFirstInstance();
            Shutdown();
            return;
        }

        // Unhandled exceptions must not silently kill a tray app: the icon would vanish with no
        // explanation and the user would never know why.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _loggerFactory.CreateLogger<App>().LogCritical(args.ExceptionObject as Exception, "Unhandled exception");

        try
        {
            await StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Cadence could not start.\n\n{ex.Message}",
                "Cadence", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private async Task StartAsync()
    {
        KnownPaths.EnsureCadenceDirectories();
        ConfigureLogging();

        var logger = _loggerFactory.CreateLogger<App>();
        logger.LogInformation("Cadence starting");

        _settingsStore = new SettingsStore();
        _settings = await _settingsStore.LoadAsync().ConfigureAwait(true);

        _updates = new Updates.UpdateService(
            new Updates.VelopackUpdateEngine(),
            () => _settings.Updates.CheckAutomatically,
            _loggerFactory.CreateLogger<Updates.UpdateService>());
        if (_updates.IsInstalled) logger.LogInformation("Installed copy, version {Version}", _updates.CurrentVersion);

        _theme = new ThemeManager();
        _theme.Apply(_settings.Display.Theme);

        _history = await HistoryRepository.OpenAsync().ConfigureAwait(true);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Cadence/0.1 (+https://github.com/FanZhu1998/Cadence)");

        ISecretStore secrets = new DpapiSecretStore();

        _providers =
        [
            new ClaudeProvider(secrets),
            new CodexProvider(),
            new GeminiProvider(),
        ];
        var providers = _providers;

        _store = new UsageStore();
        _coordinator = new RefreshCoordinator(
            providers, _store, _history, _http,
            _loggerFactory.CreateLogger<RefreshCoordinator>(),
            () => _settings);

        var descriptors = providers.ToDictionary(
            p => p.Id,
            p => new ProviderDescriptorInfo(p.Descriptor.DisplayName, p.Descriptor.ShortName, p.Descriptor.AccentHex));

        _tray = new TrayController(_store, descriptors);
        _tray.Activated += OnTrayActivated;
        _tray.Initialize(_settings.Display, _theme.IsShellDark);

        _notifier = new Notifications.NotificationManager(
            () => _settings.Notifications, _loggerFactory.CreateLogger<Notifications.NotificationManager>())
        {
            Host = _tray.NotificationHost,
        };

        _flyout = new FlyoutWindow
        {
            DataContext = new FlyoutViewModel(_store, descriptors, ct => _scheduler!.RefreshNowAsync(ct), _history),
        };

        _flyout.QuitRequested += () => Shutdown();
        _flyout.SettingsRequested += ShowSettings;
        _flyout.HistoryRequested += ShowHistory;
        _flyout.IsVisibleChanged += (_, _) =>
        {
            var open = _flyout.IsVisible;
            _scheduler?.SetFlyoutOpen(open);

            // While the panel is open Cadence is the user's foreground concern; the rest of the
            // time it is background work and should be scheduled as such.
            ProcessFootprint.SetEfficiencyMode(!open);
            if (!open) ProcessFootprint.Trim();
        };

        // Realise the window handle once at startup so the first open is instant and DWM
        // attributes can be applied before anything is shown.
        _flyout.Show();
        _flyout.Hide();
        _flyout.ApplyTheme(_theme.IsAppDark);

        _hudViewModel = new ViewModels.HudViewModel(_store, descriptors);
        ApplyHudVisibility();

        _theme.Changed += OnThemeChanged;

        _store.Changed += (provider, state) => Dispatcher.BeginInvoke(() =>
        {
            RefreshUi();

            // Notify only on a fresh, healthy snapshot: a stale one would re-fire the same
            // warning every tick while a provider is down.
            if (state is { Error: null, Snapshot: { } snapshot }) _notifier?.Evaluate(snapshot, DateTimeOffset.UtcNow);
            else if (state.Error is { IsTerminal: true } terminal) _notifier?.NotifyAuthProblem(provider, terminal, DateTimeOffset.UtcNow);
        });

        _scheduler = new RefreshScheduler(
            _coordinator, _store, () => _settings, _loggerFactory.CreateLogger<RefreshScheduler>());
        _scheduler.Start();

        StartCostScanner();
        StartUiTick();

        logger.LogInformation("Cadence started with {Count} providers", providers.Count);

        // Any later launch (a second double-click, the Start menu entry) brings this copy forward.
        _singleInstance?.ListenForActivation(() =>
        {
            logger.LogInformation("Launched again; bringing the running copy forward");
            Dispatcher.BeginInvoke(ShowPanelFromLaunch);
        });

        // First launch: the tray icon usually starts hidden behind the taskbar overflow arrow, so
        // without a welcome the app would appear to do nothing at all.
        if (ShouldShowWelcome()) ShowWelcome();

        if (!IsAutomationLaunch()) _updates.Start();

        // Startup inflates the working set with JIT, XAML parsing and the first transcript scan,
        // none of which is needed afterwards. Give it a moment to finish, then hand the pages back
        // and drop to background scheduling.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(
            _ => Dispatcher.BeginInvoke(() =>
            {
                if (_flyout?.IsVisible is not true)
                {
                    ProcessFootprint.SetEfficiencyMode(true);
                    ProcessFootprint.Trim();
                }
            }),
            TaskScheduler.Default);

        var args = Environment.GetCommandLineArgs();

        // --show opens the panel straight away. Useful for a first run, where the tray icon may be
        // hidden in the overflow flyout and the app otherwise looks like it did nothing.
        if (args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
        {
            await _scheduler.RefreshNowAsync().ConfigureAwait(true);
            RefreshUi();
            _flyout.ShowAnchored(null);
        }

        // --screenshot <path> renders the flyout to a PNG and exits. Capturing the screen instead
        // does not work: the panel closes on deactivation, so it is gone before the capture runs.
        var screenshotIndex = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            await CaptureFlyoutAsync(args[screenshotIndex + 1]).ConfigureAwait(true);
            Shutdown();
        }

        // Renders Settings or History to a PNG and exits, so a theme change can be checked on
        // every surface rather than only the one that is easy to photograph.
        var windowShotIndex = Array.FindIndex(args, a => a.Equals("--screenshot-window", StringComparison.OrdinalIgnoreCase));
        if (windowShotIndex >= 0 && windowShotIndex + 2 < args.Length)
        {
            await _scheduler.RefreshNowAsync().ConfigureAwait(true);

            Window? target = args[windowShotIndex + 1].ToLowerInvariant() switch
            {
                "settings" => new Settings.SettingsWindow(_settings, _settingsStore!, _providers, _http!, _updates),
                "history" => new History.HistoryWindow(_history!, [.. _providers.Select(p => p.Id)]),
                "welcome" => new Launch.WelcomeWindow(launchAtSignIn: true, offerStartMenu: !_updates.IsInstalled, dark: _theme?.IsAppDark ?? true),
                _ => null,
            };

            if (target is not null)
            {
                target.Show();
                await CaptureElementAsync(target, args[windowShotIndex + 2], chequerboard: false).ConfigureAwait(true);
            }

            Shutdown();
        }

        var hudShotIndex = Array.FindIndex(args, a => a.Equals("--screenshot-hud", StringComparison.OrdinalIgnoreCase));
        if (hudShotIndex >= 0 && hudShotIndex + 1 < args.Length)
        {
            _settings = _settings with { Display = _settings.Display with { ShowHud = true } };
            ApplyHudVisibility();

            await _scheduler.RefreshNowAsync().ConfigureAwait(true);
            RefreshUi();

            await CaptureElementAsync(_hud, args[hudShotIndex + 1]).ConfigureAwait(true);
            Shutdown();
        }
    }

    /// <summary>
    /// Repaints countdowns without polling.
    /// </summary>
    /// <remarks>
    /// "resets in 3h 12m" has to keep counting down between fetches. A ten-second tick is
    /// imperceptibly coarse for a countdown in minutes and costs nothing while the panel is shut.
    /// </remarks>
    private void StartUiTick()
    {
        _uiTick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };

        _uiTick.Tick += (_, _) => RefreshUi();
        _uiTick.Start();
    }

    private void RefreshUi()
    {
        var now = DateTimeOffset.UtcNow;

        _tray?.Refresh();
        _hudViewModel?.Sync(now);

        if (_flyout?.DataContext is FlyoutViewModel viewModel)
        {
            viewModel.Sync(now);
            _flyout.SetRefreshing(viewModel.IsRefreshing);
        }
    }

    /// <summary>
    /// Creates, shows or hides the HUD to match the current setting.
    /// </summary>
    /// <remarks>
    /// The window is created lazily and then kept: it is opt-in, so most users never pay for it,
    /// and the ones who do should not wait for a window to be built each time they toggle it.
    /// </remarks>
    private void ApplyHudVisibility()
    {
        if (!_settings.Display.ShowHud)
        {
            _hud?.HideHud();
            return;
        }

        if (_hud is null)
        {
            _hud = new Hud.HudWindow { DataContext = _hudViewModel };

            // Clicking the strip opens the full panel, which is the natural next step once
            // something on it has caught the user's eye.
            _hud.Clicked += () => OnTrayActivated(null);
            _hud.Moved += SaveHudPlacement;
            // Below the default level: silent in normal use, available when a user reports the strip
            // appearing somewhere unexpected, which is otherwise almost impossible to diagnose remotely.
            _hud.PlacementTrace = message => _loggerFactory.CreateLogger<App>().LogDebug("HUD {Message}", message);
        }

        _hud.IsLocked = _settings.Display.HudLocked;
        _hudViewModel?.Sync(DateTimeOffset.UtcNow);
        _hud.Restore(_settings.Display.HudPlacements);
    }

    /// <summary>Persists the HUD position against the current monitor arrangement.</summary>
    private async void SaveHudPlacement(double left, double top)
    {
        if (_hud is null || _settingsStore is null) return;

        var placements = _settings.Display.HudPlacements.ToDictionary(kv => kv.Key, kv => kv.Value);
        placements[_hud.CurrentLayoutSignature()] = new HudPlacement(left, top);

        _settings = _settings with { Display = _settings.Display with { HudPlacements = placements } };

        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);
    }

    /// <summary>
    /// Scans local session transcripts on a slow background loop.
    /// </summary>
    /// <remarks>
    /// Independent of provider polling: it reads only local files, so it keeps working when an
    /// endpoint is down and gives the forecaster a second signal between fetches.
    /// </remarks>
    private void StartCostScanner()
    {
        if (!_settings.Cost.Enabled || _history is null) return;

        _costScanner = new CostScanner(_history, PricingTable.Load(), _loggerFactory.CreateLogger<CostScanner>());

        _ = Task.Run(async () =>
        {
            var retention = TimeSpan.FromDays(Math.Clamp(_settings.Cost.RetentionDays, 1, 365));

            while (true)
            {
                try
                {
                    await _costScanner.ScanAsync(retention, DateTimeOffset.UtcNow).ConfigureAwait(false);

                    // DataContext is a DependencyProperty, so even reading it belongs to the UI
                    // thread. Marshal first, then touch the view model.
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (_flyout?.DataContext is FlyoutViewModel viewModel)
                            await viewModel.RefreshCostAsync().ConfigureAwait(true);
                    });
                }
                catch (Exception e)
                {
                    _loggerFactory.CreateLogger<CostScanner>().LogWarning(e, "Cost scan failed");
                }

                // Well above the 60s floor: transcripts are large and the totals are not urgent.
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Renders the flyout's visual tree to a PNG.
    /// </summary>
    /// <remarks>
    /// Renders the element rather than grabbing the screen, so it works headless, captures the
    /// panel at an exact size, and is unaffected by focus. This is what the snapshot tests use.
    /// </remarks>
    /// <summary>
    /// Renders any window's content to a PNG, on a chequerboard so translucency is visible.
    /// </summary>
    /// <remarks>
    /// The HUD is deliberately semi-transparent, so capturing it on a flat colour would hide
    /// exactly the property worth checking.
    /// </remarks>
    private static async Task CaptureElementAsync(Window? window, string path, bool chequerboard = true)
    {
        if (window is null) return;

        await Task.Delay(500).ConfigureAwait(true);
        window.UpdateLayout();

        var root = (System.Windows.Media.Visual)window.Content;
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) return;

        const double Scale = 2.0;
        const int Square = 12;

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale,
            System.Windows.Media.PixelFormats.Pbgra32);

        var drawing = new System.Windows.Media.DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var light = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x5A, 0x6B, 0x84));
            var dark = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x46, 0x54, 0x69));

            if (chequerboard)
            {
                for (var y = 0; y < height; y += Square)
                {
                    for (var x = 0; x < width; x += Square)
                    {
                        var brush = ((x / Square) + (y / Square)) % 2 == 0 ? light : dark;
                        dc.DrawRectangle(brush, null, new Rect(x, y, Square, Square));
                    }
                }
            }
            else if (window.Background is { } ground)
            {
                dc.DrawRectangle(ground, null, new Rect(0, 0, width, height));
            }

            dc.DrawRectangle(
                new System.Windows.Media.VisualBrush(root)
                {
                    Stretch = System.Windows.Media.Stretch.None,
                    AlignmentX = System.Windows.Media.AlignmentX.Left,
                    AlignmentY = System.Windows.Media.AlignmentY.Top,
                },
                null, new Rect(0, 0, width, height));
        }

        target.Render(drawing);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task CaptureFlyoutAsync(string path)
    {
        if (_flyout is null || _scheduler is null) return;

        await _scheduler.RefreshNowAsync().ConfigureAwait(true);
        RefreshUi();

        _flyout.ShowAnchored(null);

        // Let binding, layout and the forecast text settle before the frame is taken.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await Task.Delay(400).ConfigureAwait(true);
        _flyout.UpdateLayout();

        var root = (System.Windows.Media.Visual)_flyout.Content;
        var bounds = System.Windows.Media.VisualTreeHelper.GetDescendantBounds(root);

        var width = (int)Math.Ceiling(_flyout.ActualWidth);
        var height = (int)Math.Ceiling(Math.Max(bounds.Height, _flyout.ActualHeight));
        if (width <= 0 || height <= 0) return;

        // 2x so the capture shows what a 200% DPI display would.
        const double Scale = 2.0;

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale,
            System.Windows.Media.PixelFormats.Pbgra32);

        // Paint the window background first: the visual itself is transparent where Mica shows.
        var drawing = new System.Windows.Media.DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(_flyout.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(
                new System.Windows.Media.VisualBrush(root) { Stretch = System.Windows.Media.Stretch.None,
                    AlignmentX = System.Windows.Media.AlignmentX.Left,
                    AlignmentY = System.Windows.Media.AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }

        target.Render(drawing);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OnTrayActivated(Interop.Win32.Rect? iconRect)
        => Dispatcher.BeginInvoke(() =>
        {
            if (_flyout is null) return;

            RefreshUi();
            _flyout.Toggle(iconRect);
        });

    private void OnThemeChanged()
    {
        if (_theme is null) return;

        _tray?.UpdateSettings(_settings.Display, _theme.IsShellDark);
        _flyout?.ApplyTheme(_theme.IsAppDark);
    }

    private void ConfigureLogging()
    {
        var logPath = Path.Combine(KnownPaths.LogDirectory, "cadence-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Redaction sits between the app and every sink, so no token can reach a file even if a
        // provider echoes one back inside an error message.
        _loggerFactory = new SerilogLoggerFactory(Log.Logger);
        _loggerFactory = new RedactingLoggerFactory(_loggerFactory);
    }

    /// <summary>
    /// Claims the per-session instance lock, or reports that another copy already holds it.
    /// </summary>
    /// <remarks>
    /// The lock name used to be derived from <c>string.GetHashCode()</c>, which .NET randomises per
    /// process. Every launch computed a different name, the guard never matched, and a second
    /// double-click started a second copy polling the same private endpoints. The name is now a
    /// constant; see <see cref="Launch.SingleInstance"/>.
    /// </remarks>
    private bool ClaimSingleInstance()
    {
        _singleInstance = new Launch.SingleInstance();
        return _singleInstance.IsFirstInstance;
    }

    /// <summary>Opens Settings, reusing the window if it is already up.</summary>
    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new Settings.SettingsWindow(_settings, _settingsStore!, _providers, _http!, _updates);
        _settingsWindow.SettingsChanged += OnSettingsChanged;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>Applies changed settings live, without a restart.</summary>
    private void OnSettingsChanged(AppSettings updated)
    {
        _settings = updated;

        _theme?.Apply(updated.Display.Theme);
        _tray?.UpdateSettings(updated.Display, _theme?.IsShellDark ?? true);
        _flyout?.ApplyTheme(_theme?.IsAppDark ?? true);
        ApplyHudVisibility();

        RefreshUi();
    }

    /// <summary>Opens the history window, reusing it if already open.</summary>
    private void ShowHistory()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }

        if (_history is null) return;

        _historyWindow = new History.HistoryWindow(_history, [.. _providers.Select(p => p.Id)]);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggerFactory.CreateLogger<App>().LogError(e.Exception, "Unhandled UI exception");

        // Keep running: a rendering glitch in one card must not take the tray icon down.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _uiTick?.Stop();
        _hud?.Close();
        _scheduler?.Dispose();
        _updates?.Dispose();
        _notifier?.Save();
        _tray?.Dispose();
        _theme?.Dispose();
        _history?.Dispose();
        _http?.Dispose();

        Log.CloseAndFlush();

        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}


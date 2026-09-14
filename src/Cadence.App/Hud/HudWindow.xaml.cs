using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cadence.App.Interop;
using Cadence.App.ViewModels;

namespace Cadence.App.Hud;

/// <summary>
/// The floating readout: an always-on-top strip the user puts wherever they actually look.
/// </summary>
/// <remarks>
/// This exists because the Windows notification area has no text. macOS can render
/// <c>Codex 42% · ↑11% · runs out 16:40</c> straight into the menu bar; Windows offers a 16×16
/// square, so the expressive form needs its own surface.
/// <para>
/// The interaction problem is that "click-through except when hovered" is circular: a window with
/// <c>WS_EX_TRANSPARENT</c> receives no mouse messages, so it cannot be told it is being hovered.
/// The way out is to poll the cursor and toggle the style, which is cheap and, unlike a low-level
/// mouse hook, does not look like a keylogger to antivirus software.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public partial class HudWindow : Window
{
    /// <summary>
    /// Cursor poll interval. Fast enough that the strip is interactive by the time a hand arrives,
    /// slow enough to be free: this is one <c>GetCursorPos</c> call.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>Movement under this many pixels during a press counts as a click, not a drag.</summary>
    private const double DragThreshold = 3;

    private readonly DispatcherTimer _cursorPoll;

    /// <summary>
    /// Mirrors the window's <c>WS_EX_TRANSPARENT</c> bit so the style is only touched on a change.
    /// </summary>
    /// <remarks>
    /// Starts false because that is a fresh window's real state. Initialising it to true to match
    /// the intended default made the first <see cref="ApplyClickThrough"/> call a no-op, and the
    /// strip silently swallowed every click aimed at whatever sat behind it.
    /// </remarks>
    private bool _clickThrough;
    private bool _locked;

    /// <summary>Raised on a click, so the host can open the flyout.</summary>
    public event Action? Clicked;

    /// <summary>Raised after a drag settles, with the position to persist.</summary>
    public event Action<double, double>? Moved;

    /// <summary>Diagnostic hook for placement, used when positioning misbehaves.</summary>
    public Action<string>? PlacementTrace { get; set; }

    public HudWindow()
    {
        InitializeComponent();

        _cursorPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _cursorPoll.Tick += (_, _) => UpdateHoverState();

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _cursorPoll.Start();
            else _cursorPoll.Stop();
        };

        MouseLeftButtonDown += OnMouseLeftButtonDown;

        // The strip is sized to its content, and at startup there is no content yet: providers
        // have not been polled. Without re-anchoring, it is placed against an empty window and
        // then grows, leaving it adrift from the corner it was supposed to sit in.
        SizeChanged += (_, _) => Reanchor();
    }

    public HudViewModel? ViewModel => DataContext as HudViewModel;

    /// <summary>
    /// When locked the strip is permanently click-through: a pure readout that can never
    /// intercept a click meant for the window behind it.
    /// </summary>
    public bool IsLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            UpdateHoverState();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // No taskbar button, no Alt-Tab entry, and never steals focus from what the user is doing.
        Win32.MakeToolWindow(this);
        Win32.SetNoActivate(this, true);
        ApplyClickThrough(true);
    }

    /// <summary>True once the user has dragged the strip, or a saved position was restored.</summary>
    private bool _hasExplicitPosition;

    /// <summary>Places the HUD, restoring a remembered position for this monitor arrangement.</summary>
    public void Restore(IReadOnlyDictionary<string, Core.Model.HudPlacement> saved)
    {
        Show();
        UpdateLayout();

        var workAreas = WorkAreasInWindowUnits();

        if (saved.TryGetValue(CurrentLayoutSignature(), out var placement))
        {
            _hasExplicitPosition = true;

            // A remembered position can name a monitor that has since been unplugged.
            var (savedLeft, savedTop) =
                HudPlacement.Clamp(placement.Left, placement.Top, ActualWidth, ActualHeight, workAreas);

            Left = savedLeft;
            Top = savedTop;
            return;
        }

        _hasExplicitPosition = false;
        MoveToDefaultCorner(workAreas);
    }

    private void MoveToDefaultCorner(IReadOnlyList<Bounds> workAreas)
    {
        var (left, top) = HudPlacement.DefaultPosition(ActualWidth, ActualHeight, workAreas);

        Left = left;
        Top = top;
    }

    private bool _reanchorQueued;

    /// <summary>
    /// Keeps the strip where it belongs after its content changes size.
    /// </summary>
    /// <remarks>
    /// Queued at <see cref="DispatcherPriority.Loaded"/> rather than run inline.
    /// <c>SizeChanged</c> fires during layout and reports intermediate sizes, so positioning from
    /// it directly anchors against a width the window is only passing through — which left the
    /// strip about eighty pixels adrift of the corner it was aimed at. Waiting until layout has
    /// settled means <see cref="FrameworkElement.ActualWidth"/> is the final one.
    /// </remarks>
    private void Reanchor()
    {
        if (_reanchorQueued || !IsVisible) return;

        _reanchorQueued = true;

        Dispatcher.BeginInvoke(
            () =>
            {
                _reanchorQueued = false;

                if (!IsVisible || ActualWidth <= 0) return;

                var workAreas = WorkAreasInWindowUnits();

                PlacementTrace?.Invoke(
                    $"reanchor: actual={ActualWidth:F1}x{ActualHeight:F1} dpi={VisualTreeHelper.GetDpi(this).DpiScaleX:F3} "
                    + $"work={string.Join(" | ", workAreas.Select(a => $"{a.Left:F0},{a.Top:F0} {a.Width:F0}x{a.Height:F0}"))} "
                    + $"left={Left:F1} top={Top:F1} explicit={_hasExplicitPosition}");

                if (!_hasExplicitPosition)
                {
                    MoveToDefaultCorner(workAreas);
                    return;
                }

                var (left, top) = HudPlacement.Clamp(Left, Top, ActualWidth, ActualHeight, workAreas);

                Left = left;
                Top = top;
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>The key this arrangement's position is stored under.</summary>
    public string CurrentLayoutSignature()
        => HudPlacement.LayoutSignature(WorkAreasInWindowUnits());

    /// <summary>
    /// Monitor work areas converted from physical pixels into this window's units.
    /// </summary>
    /// <remarks>
    /// Win32 reports physical pixels while WPF positions windows in device-independent units. The
    /// conversion uses this window's own DPI, which is exact on a uniform-DPI desktop and
    /// approximate across mixed-DPI displays — enough for snapping and for keeping the strip
    /// on screen, which is all this is used for.
    /// </remarks>
    private IReadOnlyList<Bounds> WorkAreasInWindowUnits()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var monitors = Win32.GetMonitors();
        if (monitors.Count == 0)
        {
            var fallback = SystemParameters.WorkArea;
            return [new Bounds(fallback.Left, fallback.Top, fallback.Width, fallback.Height)];
        }

        // Primary first, so DefaultPosition lands on the display the user is most likely watching.
        return
        [
            .. monitors
                .OrderByDescending(m => m.IsPrimary)
                .Select(m => new Bounds(
                    m.WorkArea.Left / scaleX,
                    m.WorkArea.Top / scaleY,
                    m.WorkArea.Width / scaleX,
                    m.WorkArea.Height / scaleY)),
        ];
    }

    /// <summary>
    /// Toggles interactivity based on whether the cursor is over the strip.
    /// </summary>
    private void UpdateHoverState()
    {
        if (!IsVisible) return;

        var hovered = !_locked && CursorIsOver();

        if (ViewModel is { } model) model.IsHovered = hovered;
        Grip.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;

        ApplyClickThrough(!hovered);
    }

    private bool CursorIsOver()
    {
        if (Win32.GetCursorPosition() is not { } cursor) return false;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        // Cursor comes back in physical pixels; the window is positioned in DIU.
        var x = cursor.X / scaleX;
        var y = cursor.Y / scaleY;

        return new Bounds(Left, Top, ActualWidth, ActualHeight).Contains(x, y);
    }

    private void ApplyClickThrough(bool clickThrough)
    {
        if (clickThrough == _clickThrough) return;

        _clickThrough = clickThrough;
        Win32.SetClickThrough(this, clickThrough);
    }

    /// <summary>
    /// Handles press, drag and click.
    /// </summary>
    /// <remarks>
    /// <see cref="Window.DragMove"/> blocks until the button is released, so a click and a drag are
    /// told apart afterwards by whether the window actually moved. Doing it the other way, by
    /// waiting to see movement before starting a drag, loses the first few pixels and makes the
    /// strip feel like it sticks.
    /// </remarks>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;

        var startLeft = Left;
        var startTop = Top;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Raised when the button was already released; nothing to do.
            return;
        }

        var moved = Math.Abs(Left - startLeft) > DragThreshold || Math.Abs(Top - startTop) > DragThreshold;

        if (!moved)
        {
            Clicked?.Invoke();
            return;
        }

        var workAreas = WorkAreasInWindowUnits();
        var (left, top) = HudPlacement.Snap(Left, Top, ActualWidth, ActualHeight, workAreas);
        (left, top) = HudPlacement.Clamp(left, top, ActualWidth, ActualHeight, workAreas);

        _hasExplicitPosition = true;

        Left = left;
        Top = top;

        Moved?.Invoke(left, top);
    }

    /// <summary>Hides and stops polling. Cheaper than closing, and keeps the position.</summary>
    public void HideHud()
    {
        _cursorPoll.Stop();
        Hide();
    }
}


using System.Globalization;
using System.Text;

namespace Cadence.App.Hud;

/// <summary>A rectangle in device-independent units, independent of WPF and Win32 types.</summary>
public readonly record struct Bounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>True when this rectangle overlaps <paramref name="other"/> at all.</summary>
    public bool Intersects(Bounds other)
        => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

/// <summary>
/// Where the HUD sits, and how it remembers.
/// </summary>
/// <remarks>
/// Pure arithmetic, deliberately free of WPF and Win32 types, because placement is the part most
/// likely to be wrong and the part hardest to check by eye. Docking a laptop rearranges every
/// coordinate on screen, and a HUD that reappears half off the edge of a monitor that no longer
/// exists is worse than one that never moved.
/// </remarks>
public static class HudPlacement
{
    /// <summary>Snap to an edge when within this many device-independent pixels of it.</summary>
    public const double SnapThreshold = 18;

    /// <summary>Keep at least this much of the HUD on screen when clamping.</summary>
    private const double MinimumVisible = 24;

    /// <summary>
    /// A stable key for the current monitor arrangement.
    /// </summary>
    /// <remarks>
    /// Positions are stored per arrangement, so undocking and redocking returns the HUD to where
    /// the user put it in each setup rather than making them drag it back twice a day. Monitors
    /// are sorted so enumeration order, which is not stable, cannot change the key.
    /// </remarks>
    public static string LayoutSignature(IEnumerable<Bounds> monitors)
    {
        var builder = new StringBuilder();

        foreach (var monitor in monitors
                     .OrderBy(m => m.Left)
                     .ThenBy(m => m.Top)
                     .ThenBy(m => m.Width)
                     .ThenBy(m => m.Height))
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{monitor.Left:F0},{monitor.Top:F0},{monitor.Width:F0},{monitor.Height:F0};");
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    /// <summary>
    /// Snaps a dragged position to a nearby monitor edge.
    /// </summary>
    /// <remarks>
    /// Snapping is to the <em>work area</em>, not the monitor bounds, so the HUD lands above the
    /// taskbar rather than underneath it.
    /// </remarks>
    public static (double Left, double Top) Snap(
        double left, double top, double width, double height, IReadOnlyList<Bounds> workAreas)
    {
        if (workAreas.Count == 0) return (left, top);

        var area = MonitorFor(left, top, width, height, workAreas);

        var snappedLeft = left;
        if (Math.Abs(left - area.Left) <= SnapThreshold) snappedLeft = area.Left;
        else if (Math.Abs(area.Right - (left + width)) <= SnapThreshold) snappedLeft = area.Right - width;

        var snappedTop = top;
        if (Math.Abs(top - area.Top) <= SnapThreshold) snappedTop = area.Top;
        else if (Math.Abs(area.Bottom - (top + height)) <= SnapThreshold) snappedTop = area.Bottom - height;

        return (snappedLeft, snappedTop);
    }

    /// <summary>
    /// Brings a remembered position back on screen.
    /// </summary>
    /// <remarks>
    /// A saved position can reference a monitor that has since been unplugged, which would leave
    /// the HUD invisible at coordinates nothing can reach. When the position no longer overlaps
    /// any work area at all it is discarded and the default corner used instead.
    /// </remarks>
    public static (double Left, double Top) Clamp(
        double left, double top, double width, double height, IReadOnlyList<Bounds> workAreas)
    {
        if (workAreas.Count == 0) return (left, top);

        var window = new Bounds(left, top, width, height);

        if (!workAreas.Any(area => area.Intersects(window)))
            return DefaultPosition(width, height, workAreas);

        var target = MonitorFor(left, top, width, height, workAreas);

        return (
            Math.Clamp(left, target.Left - width + MinimumVisible, target.Right - MinimumVisible),
            Math.Clamp(top, target.Top - height + MinimumVisible, target.Bottom - MinimumVisible));
    }

    /// <summary>
    /// The default corner: bottom-right of the primary work area, just above the clock.
    /// </summary>
    /// <remarks>
    /// The same corner Windows uses for its own notifications, and the one place a user's eye
    /// already goes when checking a background status.
    /// </remarks>
    public static (double Left, double Top) DefaultPosition(
        double width, double height, IReadOnlyList<Bounds> workAreas)
    {
        const double Margin = 12;

        if (workAreas.Count == 0) return (Margin, Margin);

        var primary = workAreas[0];
        return (primary.Right - width - Margin, primary.Bottom - height - Margin);
    }

    /// <summary>
    /// The work area the window sits mostly within, by overlap area.
    /// </summary>
    /// <remarks>
    /// Picking by the top-left corner instead would attach a window straddling two displays to
    /// whichever it barely touches, and snap it to the wrong edge.
    /// </remarks>
    private static Bounds MonitorFor(
        double left, double top, double width, double height, IReadOnlyList<Bounds> workAreas)
    {
        var window = new Bounds(left, top, width, height);

        Bounds best = workAreas[0];
        var bestOverlap = -1d;

        foreach (var area in workAreas)
        {
            var overlap =
                Math.Max(0, Math.Min(window.Right, area.Right) - Math.Max(window.Left, area.Left)) *
                Math.Max(0, Math.Min(window.Bottom, area.Bottom) - Math.Max(window.Top, area.Top));

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = area;
            }
        }

        return best;
    }
}

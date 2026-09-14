using Cadence.App.Hud;

namespace Cadence.App.Tests;

/// <summary>
/// Placement is the part of the HUD hardest to check by eye and easiest to get wrong: a window
/// positioned off-screen is invisible, and the user has no way to drag back something they cannot
/// see.
/// </summary>
public class HudPlacementTests
{
    private static readonly Bounds Laptop = new(0, 0, 1920, 1040);        // work area, taskbar excluded
    private static readonly Bounds External = new(1920, 0, 2560, 1400);

    private static readonly IReadOnlyList<Bounds> Dual = [Laptop, External];
    private static readonly IReadOnlyList<Bounds> Single = [Laptop];

    // ---- layout signature ----------------------------------------------------------------------

    [Fact]
    public void LayoutSignature_IsStableRegardlessOfEnumerationOrder()
    {
        // Monitor enumeration order is not guaranteed. If it leaked into the key, a user would
        // lose their HUD position at random.
        Assert.Equal(
            HudPlacement.LayoutSignature([Laptop, External]),
            HudPlacement.LayoutSignature([External, Laptop]));
    }

    [Fact]
    public void LayoutSignature_DistinguishesDifferentArrangements()
    {
        Assert.NotEqual(
            HudPlacement.LayoutSignature(Single),
            HudPlacement.LayoutSignature(Dual));

        // Same monitor count, different placement: docking on the other side is a real change.
        Assert.NotEqual(
            HudPlacement.LayoutSignature([Laptop, External]),
            HudPlacement.LayoutSignature([Laptop, new Bounds(-2560, 0, 2560, 1400)]));
    }

    [Fact]
    public void LayoutSignature_HandlesNoMonitors()
        => Assert.Equal("none", HudPlacement.LayoutSignature([]));

    // ---- snapping ------------------------------------------------------------------------------

    [Fact]
    public void Snap_PullsToTheNearestLeftEdge()
    {
        var (left, top) = HudPlacement.Snap(8, 500, 220, 60, Single);

        Assert.Equal(0, left);
        Assert.Equal(500, top);
    }

    [Fact]
    public void Snap_PullsToTheRightEdgeByTheWindowsFarSide()
    {
        // 1920 - 220 = 1700 is flush right; starting 10px short should snap out to it.
        var (left, _) = HudPlacement.Snap(1690, 500, 220, 60, Single);

        Assert.Equal(1700, left);
    }

    [Fact]
    public void Snap_PullsToTheBottomEdgeOfTheWorkAreaNotTheScreen()
    {
        // 1040, not 1080: snapping to the monitor bounds would put the HUD under the taskbar.
        var (_, top) = HudPlacement.Snap(500, 975, 220, 60, Single);

        Assert.Equal(980, top);
    }

    [Fact]
    public void Snap_LeavesAPositionAloneWhenItIsNowhereNearAnEdge()
    {
        var (left, top) = HudPlacement.Snap(800, 500, 220, 60, Single);

        Assert.Equal(800, left);
        Assert.Equal(500, top);
    }

    [Fact]
    public void Snap_UsesTheMonitorTheWindowMostlySitsOn()
    {
        // Mostly on the external display, so it should snap to that display's left edge (1920),
        // not to the laptop's. Choosing by top-left corner alone would get this backwards.
        var (left, _) = HudPlacement.Snap(1910, 400, 220, 60, Dual);

        Assert.Equal(1920, left);
    }

    [Fact]
    public void Snap_IsANoOpWithoutMonitors()
    {
        var (left, top) = HudPlacement.Snap(50, 60, 220, 60, []);

        Assert.Equal(50, left);
        Assert.Equal(60, top);
    }

    // ---- clamping ------------------------------------------------------------------------------

    [Fact]
    public void Clamp_LeavesAnOnScreenPositionUntouched()
    {
        var (left, top) = HudPlacement.Clamp(800, 500, 220, 60, Dual);

        Assert.Equal(800, left);
        Assert.Equal(500, top);
    }

    [Fact]
    public void Clamp_RescuesAPositionOnAMonitorThatIsGone()
    {
        // Saved while docked, restored on the laptop alone. Without this the strip sits at x=2400
        // on a 1920-wide desktop: running, invisible, and unreachable.
        var (left, top) = HudPlacement.Clamp(2400, 900, 220, 60, Single);

        var expected = HudPlacement.DefaultPosition(220, 60, Single);

        Assert.Equal(expected.Left, left);
        Assert.Equal(expected.Top, top);
    }

    [Fact]
    public void Clamp_KeepsAPartlyOffscreenWindowReachable()
    {
        // Overlaps the work area, so it is kept rather than reset — but pulled back far enough
        // that there is something left to grab.
        var (left, _) = HudPlacement.Clamp(1880, 500, 220, 60, Single);

        Assert.True(left <= Laptop.Right - 24, $"left {left} leaves nothing grabbable");
        Assert.True(left > 1000, "a window still mostly on screen should not jump to the default corner");
    }

    [Fact]
    public void Clamp_KeepsAPositionValidOnTheSecondMonitor()
    {
        var (left, top) = HudPlacement.Clamp(3000, 700, 220, 60, Dual);

        Assert.Equal(3000, left);
        Assert.Equal(700, top);
    }

    // ---- default -------------------------------------------------------------------------------

    [Fact]
    public void DefaultPosition_IsTheBottomRightOfThePrimaryWorkArea()
    {
        var (left, top) = HudPlacement.DefaultPosition(220, 60, Dual);

        // 12px margin from the primary work area's far corner: where Windows puts its own
        // notifications, and where the eye already goes.
        Assert.Equal(1920 - 220 - 12, left);
        Assert.Equal(1040 - 60 - 12, top);
    }

    [Fact]
    public void DefaultPosition_SurvivesHavingNoMonitors()
    {
        var (left, top) = HudPlacement.DefaultPosition(220, 60, []);

        Assert.True(left >= 0);
        Assert.True(top >= 0);
    }

    // ---- geometry ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(219, 59, true)]
    [InlineData(220, 30, false)]   // right edge is exclusive
    [InlineData(-1, 30, false)]
    public void Bounds_ContainsIsHalfOpen(double x, double y, bool expected)
        => Assert.Equal(expected, new Bounds(0, 0, 220, 60).Contains(x, y));

    [Fact]
    public void Bounds_IntersectsRequiresRealOverlap()
    {
        var window = new Bounds(0, 0, 100, 100);

        Assert.True(window.Intersects(new Bounds(50, 50, 100, 100)));
        Assert.False(window.Intersects(new Bounds(100, 0, 100, 100)));  // touching, not overlapping
        Assert.False(window.Intersects(new Bounds(200, 200, 50, 50)));
    }
}

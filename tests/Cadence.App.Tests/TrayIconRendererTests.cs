using System.Diagnostics;
using System.Runtime.InteropServices;
using Cadence.App.Tray;
using Cadence.Core.Model;

namespace Cadence.App.Tests;

/// <summary>
/// Guards the tray renderer, where the failure modes are quiet and fatal: a leaked GDI handle makes
/// the icon vanish hours later, and an unreadable icon makes the whole app pointless.
/// </summary>
public sealed partial class TrayIconRendererTests
{
    private static TrayIconState State(
        double? primary = 42, double? secondary = 67, TrayIconStyle style = TrayIconStyle.TwoBar,
        int size = 16, bool dark = true, bool stale = false, bool error = false, bool incident = false)
        => new(style, primary, secondary, dark, size, "#D97757", "CLD", stale, error, incident, ShowRemaining: false);

    [Fact]
    public void RendersAnIconForEveryStyle()
    {
        using var renderer = new TrayIconRenderer();

        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            using var icon = renderer.Get(State(style: style));

            Assert.NotEqual(nint.Zero, icon.Handle);
            Assert.Equal(16, icon.Width);
        }
    }

    [Theory]
    [InlineData(16)]  // 100% DPI
    [InlineData(20)]  // 125%
    [InlineData(24)]  // 150%
    [InlineData(32)]  // 200%
    public void RendersAtEveryDpiStep(int size)
    {
        using var renderer = new TrayIconRenderer();
        using var icon = renderer.Get(State(size: size));

        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void RendersEveryVisualState()
    {
        using var renderer = new TrayIconRenderer();

        foreach (var state in new[]
                 {
                     State(),
                     State(primary: null, secondary: null),   // unknown
                     State(primary: 0),                       // empty
                     State(primary: 100, secondary: 100),     // full
                     State(stale: true),
                     State(error: true),
                     State(incident: true),
                 })
        {
            using var icon = renderer.Get(state);
            Assert.NotEqual(nint.Zero, icon.Handle);
        }
    }

    [Fact]
    public void IdenticalStatesAreNotReRendered()
    {
        // At a one-minute cadence an uncached renderer performs 1,440 pointless Skia renders and
        // GDI allocations a day.
        using var renderer = new TrayIconRenderer();

        for (var i = 0; i < 50; i++) renderer.Get(State()).Dispose();

        Assert.Equal(1, renderer.RenderCount);
    }

    [Fact]
    public void DifferentStatesRenderSeparately()
    {
        using var renderer = new TrayIconRenderer();

        renderer.Get(State(primary: 10)).Dispose();
        renderer.Get(State(primary: 20)).Dispose();
        renderer.Get(State(primary: 10, dark: false)).Dispose();

        Assert.Equal(3, renderer.RenderCount);
    }

    [Fact]
    public void EachCallReturnsAnIndependentlyOwnedIcon()
    {
        // The shell wrapper disposes whatever icon is assigned to it. If the cache handed out its
        // own instance, the second refresh would assign an already-destroyed handle.
        using var renderer = new TrayIconRenderer();

        var first = renderer.Get(State());
        var second = renderer.Get(State());

        Assert.NotEqual(first.Handle, second.Handle);

        first.Dispose();

        // Disposing one must leave the other, and the cache, entirely usable.
        Assert.NotEqual(nint.Zero, second.Handle);
        using var third = renderer.Get(State());
        Assert.NotEqual(nint.Zero, third.Handle);

        second.Dispose();
    }

    [Fact]
    public void FlushDropsTheCacheSoTheNextGetRendersAgain()
    {
        using var renderer = new TrayIconRenderer();

        renderer.Get(State()).Dispose();
        renderer.Flush();
        renderer.Get(State()).Dispose();

        Assert.Equal(2, renderer.RenderCount);
        Assert.Equal(1, renderer.CacheSize);
    }

    /// <summary>
    /// The soak test: many icon swaps must not grow the process's GDI object count.
    /// </summary>
    /// <remarks>
    /// The default per-process GDI quota is 10,000 objects. Leaking one handle per refresh exhausts
    /// it in well under a day of normal use, and the symptom is the icon silently disappearing —
    /// which is untraceable after the fact. This asserts the count is flat, which is the only way
    /// to know the ownership rules actually hold.
    /// </remarks>
    [Fact]
    public void SoakTest_GdiObjectCountStaysFlatAcrossManyUpdates()
    {
        using var renderer = new TrayIconRenderer();

        // Warm up so one-off allocations do not count against the measurement.
        for (var i = 0; i < 100; i++)
        {
            using var warm = renderer.Get(State(primary: i % 50));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GdiObjectCount();

        // Simulates the real pattern: get an icon, then release the one it replaced.
        System.Drawing.Icon? current = null;
        for (var i = 0; i < 10_000; i++)
        {
            var next = renderer.Get(State(primary: i % 50));
            current?.Dispose();
            current = next;
        }

        current?.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GdiObjectCount();

        // A small drift is normal from unrelated runtime activity; a leak of one per iteration
        // would show as thousands.
        Assert.True(after - before < 200,
            $"GDI objects grew from {before} to {after} across 10,000 icon updates");

        // And the cache must not have grown without bound either.
        Assert.True(renderer.CacheSize <= 96, $"cache grew to {renderer.CacheSize}");
    }

    [Fact]
    public void DisposedRendererRefusesFurtherWork()
    {
        var renderer = new TrayIconRenderer();
        renderer.Get(State()).Dispose();
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => renderer.Get(State()));
    }

    private const int GdiObjects = 0;

    private static int GdiObjectCount()
        => GetGuiResources(Process.GetCurrentProcess().Handle, GdiObjects);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetGuiResources(nint process, int flags);
}

using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cadence.Core.Model;
using SkiaSharp;

namespace Cadence.App.Tray;

/// <summary>Everything that changes what the tray icon looks like. Also the render cache key.</summary>
public readonly record struct TrayIconState(
    TrayIconStyle Style,
    double? Primary,
    double? Secondary,
    bool Dark,
    int Size,
    string AccentHex,
    string? Glyph,
    bool Stale,
    bool Error,
    bool Incident,
    bool ShowRemaining);

/// <summary>
/// Renders the notification-area icon.
/// </summary>
/// <remarks>
/// Windows gives one 16x16 logical square and no text label, so the icon has to carry the whole
/// at-a-glance signal: roughly one number or two bars, never both.
/// <para>
/// Two rules keep this from destroying the app. Every <c>HICON</c> handed to the shell must be
/// destroyed once replaced — the default GDI quota is 10,000 objects per process, and leaking one
/// icon per refresh makes the tray icon silently vanish after a few hours. And identical states
/// must not be re-rendered, because at a one-minute cadence that is 1,440 pointless GDI
/// allocations a day.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class TrayIconRenderer : IDisposable
{
    private readonly ConcurrentDictionary<TrayIconState, Icon> _cache = new();
    private bool _disposed;

    /// <summary>Bounded so a long session cannot accumulate handles without limit.</summary>
    private const int MaxCachedIcons = 96;

    /// <summary>Icons rendered since construction, for the leak soak test.</summary>
    public int RenderCount { get; private set; }

    public int CacheSize => _cache.Count;

    /// <summary>
    /// Returns an icon for <paramref name="state"/>, rendering only if it is not already cached.
    /// </summary>
    /// <remarks>
    /// The caller <em>owns</em> the returned icon and must dispose it once it has been replaced.
    /// A fresh clone is handed out on every call rather than the cached instance, because the
    /// shell wrapper takes ownership of whatever icon is assigned to it and disposes it. Sharing
    /// the cached instance meant the cache handed out an already-destroyed handle after the first
    /// swap, and freed it a second time at shutdown.
    /// <para>
    /// The cache still does its job: what it saves is the Skia render, which is the expensive
    /// part. Cloning an existing icon is a single GDI copy.
    /// </para>
    /// </remarks>
    public Icon Get(TrayIconState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cache.TryGetValue(state, out var cached)) return (Icon)cached.Clone();

        // A full flush rather than an LRU eviction: the working set is tiny (a handful of
        // percentages rounded to the nearest point) and this only trips on a theme or DPI change.
        if (_cache.Count >= MaxCachedIcons) Flush();

        var icon = Render(state);
        RenderCount++;

        return (Icon)_cache.GetOrAdd(state, icon).Clone();
    }

    /// <summary>Drops every cached icon, destroying its handle. Called on theme or DPI change.</summary>
    public void Flush()
    {
        foreach (var key in _cache.Keys.ToArray())
        {
            if (_cache.TryRemove(key, out var icon)) icon.Dispose();
        }
    }

    private static Icon Render(TrayIconState state)
    {
        var size = state.Size;

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var palette = Palette.For(state);

        switch (state.Style)
        {
            case TrayIconStyle.Numeric:
                DrawNumeric(canvas, size, state, palette);
                break;
            case TrayIconStyle.Ring:
                DrawRing(canvas, size, state, palette);
                break;
            case TrayIconStyle.BarWithIncident:
            case TrayIconStyle.TwoBar:
            default:
                DrawBars(canvas, size, state, palette);
                break;
        }

        if (state.Incident) DrawIncidentDot(canvas, size, palette);
        if (state.Error) DrawErrorStrike(canvas, size, palette);

        using var image = surface.Snapshot();
        return ToIcon(image, size, state.Stale);
    }

    /// <summary>Two stacked bars: session on top, weekly below.</summary>
    private static void DrawBars(SKCanvas canvas, int size, TrayIconState state, Palette palette)
    {
        var inset = Math.Max(1f, size * 0.09f);
        var barHeight = Math.Max(2f, size * 0.26f);
        var gap = Math.Max(1f, size * 0.14f);
        var width = size - (inset * 2);
        var radius = barHeight / 2;

        var top = (size - ((barHeight * 2) + gap)) / 2;

        DrawBar(canvas, inset, top, width, barHeight, radius, state.Primary, state, palette);
        DrawBar(canvas, inset, top + barHeight + gap, width, barHeight, radius, state.Secondary, state, palette);
    }

    private static void DrawBar(
        SKCanvas canvas, float x, float y, float width, float height, float radius,
        double? percent, TrayIconState state, Palette palette)
    {
        var track = new SKRoundRect(new SKRect(x, y, x + width, y + height), radius);

        using (var paint = new SKPaint { Color = palette.Track, IsAntialias = true })
            canvas.DrawRoundRect(track, paint);

        if (percent is not { } value) return;

        // "Show remaining" flips what the fill means; the colour still tracks pressure.
        var shown = state.ShowRemaining ? 100 - value : value;
        var fraction = (float)Math.Clamp(shown / 100.0, 0, 1);
        if (fraction <= 0) return;

        var filled = Math.Max(height, width * fraction);
        var fill = new SKRoundRect(new SKRect(x, y, x + filled, y + height), radius);

        using var fillPaint = new SKPaint { Color = palette.FillFor(value), IsAntialias = true };
        canvas.DrawRoundRect(fill, fillPaint);
    }

    /// <summary>A single percentage, the largest legible glyph the tray allows.</summary>
    private static void DrawNumeric(SKCanvas canvas, int size, TrayIconState state, Palette palette)
    {
        var value = state.Primary;
        var text = value is { } v
            ? Math.Clamp(state.ShowRemaining ? 100 - v : v, 0, 100) is var shown && shown >= 99.5
                ? "99"
                : ((int)Math.Round(shown)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "–";

        // At 16px there is room for two digits at roughly 9px; 100 is rendered as 99 rather than
        // shrinking the type below the legibility floor.
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
        {
            Size = size * (text.Length >= 2 ? 0.62f : 0.78f),
            Subpixel = true,
        };

        using var paint = new SKPaint { Color = palette.FillFor(value), IsAntialias = true };

        var width = font.MeasureText(text);
        var metrics = font.Metrics;
        var baseline = (size - (metrics.Ascent + metrics.Descent)) / 2;

        canvas.DrawText(text, (size - width) / 2, baseline, SKTextAlign.Left, font, paint);
    }

    /// <summary>A ring, for people who read fullness faster than they read numbers.</summary>
    private static void DrawRing(SKCanvas canvas, int size, TrayIconState state, Palette palette)
    {
        var stroke = Math.Max(2f, size * 0.17f);
        var inset = (stroke / 2) + Math.Max(0.5f, size * 0.06f);
        var rect = new SKRect(inset, inset, size - inset, size - inset);

        using (var trackPaint = new SKPaint
               {
                   Color = palette.Track, IsAntialias = true,
                   Style = SKPaintStyle.Stroke, StrokeWidth = stroke,
               })
        {
            canvas.DrawOval(rect, trackPaint);
        }

        if (state.Primary is not { } value) return;

        var shown = state.ShowRemaining ? 100 - value : value;
        var sweep = (float)(Math.Clamp(shown, 0, 100) / 100.0 * 360);
        if (sweep <= 0) return;

        using var arcPaint = new SKPaint
        {
            Color = palette.FillFor(value), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round,
        };

        using var path = new SKPath();
        path.AddArc(rect, -90, sweep);
        canvas.DrawPath(path, arcPaint);
    }

    private static void DrawIncidentDot(SKCanvas canvas, int size, Palette palette)
    {
        var radius = Math.Max(1.5f, size * 0.13f);

        // Punch a transparent gap so the dot reads against a filled bar behind it.
        using (var clear = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src, IsAntialias = true })
            canvas.DrawCircle(size - radius, radius, radius * 1.45f, clear);

        using var paint = new SKPaint { Color = palette.Incident, IsAntialias = true };
        canvas.DrawCircle(size - radius, radius, radius, paint);
    }

    private static void DrawErrorStrike(SKCanvas canvas, int size, Palette palette)
    {
        using var paint = new SKPaint
        {
            Color = palette.Error, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.2f, size * 0.09f), StrokeCap = SKStrokeCap.Round,
        };

        var inset = size * 0.2f;
        canvas.DrawLine(inset, size - inset, size - inset, inset, paint);
    }

    /// <summary>
    /// Converts the rendered bitmap into an <see cref="Icon"/>.
    /// </summary>
    /// <remarks>
    /// <c>Icon.FromHandle</c> does not take ownership, so the raw handle is cloned into a managed
    /// icon and the original destroyed immediately. Without that clone, disposing the managed
    /// wrapper would leave the underlying handle alive forever.
    /// </remarks>
    private static Icon ToIcon(SKImage image, int size, bool stale)
    {
        using var data = image.PeekPixels();
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var locked = bitmap.LockBits(
            new Rectangle(0, 0, size, size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var source = data.GetPixelSpan();
            var rowBytes = size * 4;

            for (var y = 0; y < size; y++)
            {
                var destination = locked.Scan0 + (y * locked.Stride);
                Marshal.Copy(source.Slice(y * data.RowBytes, rowBytes).ToArray(), 0, destination, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        if (stale) ApplyStaleAlpha(bitmap, size);

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the managed Icon owns its own handle, then release the one GetHicon made.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Dims the whole icon to signal the numbers are no longer fresh.</summary>
    private static void ApplyStaleAlpha(Bitmap bitmap, int size)
    {
        const float StaleAlpha = 0.6f;

        var locked = bitmap.LockBits(
            new Rectangle(0, 0, size, size),
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var length = Math.Abs(locked.Stride) * size;
            var buffer = new byte[length];
            Marshal.Copy(locked.Scan0, buffer, 0, length);

            // Premultiplied BGRA: scaling all four channels keeps the premultiplication valid.
            for (var i = 0; i < length; i++) buffer[i] = (byte)(buffer[i] * StaleAlpha);

            Marshal.Copy(buffer, 0, locked.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Flush();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);

    /// <summary>Colours for one render, resolved from theme and pressure.</summary>
    private readonly record struct Palette(SKColor Track, SKColor Normal, SKColor Warning, SKColor Over, SKColor Incident, SKColor Error)
    {
        /// <summary>
        /// The shared palette, resolved for the shell theme the icon sits against.
        /// </summary>
        /// <remarks>
        /// These are the same tokens the rest of the app uses, chosen for the taskbar rather than
        /// the app surface: ink at rest, the accent hue as the warning step, and bear for overrun.
        /// A 16px square has no room for subtlety, so the three steps are the whole vocabulary.
        /// </remarks>
        public static Palette For(TrayIconState state) => state.Dark
            ? new Palette(
                Track: new SKColor(0xE8, 0xEE, 0xF7, 0x38),   // ink at 22%
                Normal: new SKColor(0xA7, 0xB8, 0xCE),        // ink-2
                Warning: new SKColor(0x7D, 0xAB, 0xDD),       // accent
                Over: new SKColor(0xE5, 0x77, 0x6C),          // bear
                Incident: new SKColor(0x7D, 0xAB, 0xDD),
                Error: new SKColor(0xE5, 0x77, 0x6C))
            : new Palette(
                Track: new SKColor(0x0D, 0x18, 0x26, 0x33),
                Normal: new SKColor(0x46, 0x58, 0x6E),
                Warning: new SKColor(0x17, 0x54, 0x8F),
                Over: new SKColor(0xA8, 0x32, 0x1F),
                Incident: new SKColor(0x17, 0x54, 0x8F),
                Error: new SKColor(0xA8, 0x32, 0x1F));

        /// <summary>
        /// Colour by pressure, always keyed to <em>used</em> percentage regardless of whether the
        /// icon is displaying used or remaining.
        /// </summary>
        public SKColor FillFor(double? used) => used switch
        {
            null => Track,
            >= 95 => Over,
            >= 80 => Warning,
            _ => Normal,
        };
    }
}

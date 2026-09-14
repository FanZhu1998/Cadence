using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Cadence.App.Controls;

/// <summary>
/// The usage bar: solid fill for what is spent, a hatched extension for where the forecast says
/// it will land, and a tick at the even-pace mark.
/// </summary>
/// <remarks>
/// Custom-drawn rather than composed from panels because the hatch has to be clipped precisely to
/// the forecast segment and follow the bar's rounded ends. The visual grammar is deliberate: the
/// solid fill is neutral and quiet, and the accent colour appears only on the hatch, because the
/// forecast is the one thing here the user cannot get from the provider's own dashboard.
/// </remarks>
public sealed class UsageBar : FrameworkElement
{
    private static readonly FrameworkPropertyMetadata Redraw =
        new(0d, FrameworkPropertyMetadataOptions.AffectsRender);

    /// <summary>Percentage currently used, 0-100. Null renders an empty track.</summary>
    public static readonly DependencyProperty UsedProperty = DependencyProperty.Register(
        nameof(Used), typeof(double?), typeof(UsageBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Projected percentage at reset. Drawn as a hatch beyond <see cref="Used"/>.</summary>
    public static readonly DependencyProperty ForecastProperty = DependencyProperty.Register(
        nameof(Forecast), typeof(double?), typeof(UsageBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fraction of the window elapsed, 0-1. Drawn as a faint even-pace tick.</summary>
    public static readonly DependencyProperty ElapsedFractionProperty = DependencyProperty.Register(
        nameof(ElapsedFraction), typeof(double?), typeof(UsageBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverrunBrushProperty = DependencyProperty.Register(
        nameof(OverrunBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the even-pace tick. Themed, so it stays visible on a light track.</summary>
    public static readonly DependencyProperty PaceTickBrushProperty = DependencyProperty.Register(
        nameof(PaceTickBrush), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush PaceTickBrush
    {
        get => (Brush)GetValue(PaceTickBrushProperty);
        set => SetValue(PaceTickBrushProperty, value);
    }

    public double? Used
    {
        get => (double?)GetValue(UsedProperty);
        set => SetValue(UsedProperty, value);
    }

    public double? Forecast
    {
        get => (double?)GetValue(ForecastProperty);
        set => SetValue(ForecastProperty, value);
    }

    public double? ElapsedFraction
    {
        get => (double?)GetValue(ElapsedFractionProperty);
        set => SetValue(ElapsedFractionProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush OverrunBrush
    {
        get => (Brush)GetValue(OverrunBrushProperty);
        set => SetValue(OverrunBrushProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = Math.Min(CornerRadius, height / 2);
        var bounds = new Rect(0, 0, width, height);
        var clip = new RectangleGeometry(bounds, radius, radius);

        dc.DrawGeometry(TrackBrush, null, clip);

        // Everything after this is clipped to the rounded track, so no segment can square off an end.
        dc.PushClip(clip);

        try
        {
            var used = Clamp(Used);
            var forecast = Clamp(Forecast);

            // Forecast first, so the solid fill always sits on top of the hatch.
            if (forecast is { } projected && used is { } current && projected > current + 0.01)
            {
                var from = width * (current / 100);
                var to = width * (projected / 100);

                DrawHatch(dc, new Rect(from, 0, Math.Max(0, to - from), height), projected >= 99.5);
            }

            if (used is { } value && value > 0)
            {
                var fill = value >= 99.5 ? OverrunBrush : FillBrush;
                dc.DrawRectangle(fill, null, new Rect(0, 0, width * (value / 100), height));
            }

            // The even-pace mark: where usage would be if it were perfectly level. Its distance
            // from the fill edge is the pace delta, made visible without a number. Themed rather
            // than a fixed white, which would be invisible on a light track.
            if (ElapsedFraction is { } elapsed and > 0.01 and < 0.99)
            {
                var x = width * elapsed;
                var pen = new Pen(PaceTickBrush, 1);
                pen.Freeze();
                dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>Draws the 45-degree ghosted hatch that marks projected consumption.</summary>
    private void DrawHatch(DrawingContext dc, Rect area, bool overrun)
    {
        if (area.Width <= 0) return;

        var colour = ((SolidColorBrush)(overrun ? OverrunBrush : AccentBrush)).Color;

        // A wash under the hatch so a narrow segment still reads at a glance.
        var wash = new SolidColorBrush(Color.FromArgb(38, colour.R, colour.G, colour.B));
        wash.Freeze();
        dc.DrawRectangle(wash, null, area);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, colour.R, colour.G, colour.B)), 1.1);
        pen.Freeze();

        dc.PushClip(new RectangleGeometry(area));

        try
        {
            // 45 degrees: step by height so the diagonals stay evenly spaced whatever the bar size.
            const double Spacing = 4.0;
            for (var x = area.Left - area.Height; x < area.Right; x += Spacing)
                dc.DrawLine(pen, new Point(x, area.Bottom), new Point(x + area.Height, area.Top));
        }
        finally
        {
            dc.Pop();
        }
    }

    private static double? Clamp(double? value)
        => value is { } v && !double.IsNaN(v) ? Math.Clamp(v, 0, 100) : null;

    /// <summary>Screen-reader text, since the bar is otherwise a purely visual element.</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new UsageBarAutomationPeer(this);

    private sealed class UsageBarAutomationPeer(UsageBar owner)
        : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore()
        {
            var bar = (UsageBar)Owner;

            var used = bar.Used is { } u
                ? string.Create(CultureInfo.InvariantCulture, $"{u:F0}% used")
                : "usage unknown";

            return bar.Forecast is { } f
                ? string.Create(CultureInfo.InvariantCulture, $"{used}, projected {f:F0}% at reset")
                : used;
        }

        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.ProgressBar;
    }
}

using System.Windows;
using System.Windows.Media;

namespace Cadence.App.Controls;

/// <summary>One plotted series: usage over time within a single epoch.</summary>
public sealed record UsageSeries(string Label, IReadOnlyList<(DateTimeOffset At, double Value)> Points);

/// <summary>
/// A compact usage-over-time chart with epoch boundaries drawn in.
/// </summary>
/// <remarks>
/// Custom-drawn rather than taking a charting dependency. The requirement is narrow — a step line,
/// vertical rules at resets, and a shaded forecast band forward from now — and a general charting
/// library brings its own visual language that fights the rest of the app.
/// <para>
/// Epoch boundaries are the point of the chart. Without them a reset looks like a crash in usage,
/// and the eye reads a sawtooth as instability rather than as normal weekly rhythm.
/// </para>
/// </remarks>
public sealed class UsageChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<UsageSeries>), typeof(UsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Axis numerals. Separate from the gridlines, which must stay fainter than data.</summary>
    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EpochBrushProperty = DependencyProperty.Register(
        nameof(EpochBrush), typeof(Brush), typeof(UsageChart),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<UsageSeries>? Series
    {
        get => (IReadOnlyList<UsageSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush EpochBrush
    {
        get => (Brush)GetValue(EpochBrushProperty);
        set => SetValue(EpochBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8) return;

        var series = Series;
        if (series is null || series.Count == 0)
        {
            DrawEmpty(dc, width, height);
            return;
        }

        var all = series.SelectMany(s => s.Points).ToArray();
        if (all.Length < 2)
        {
            DrawEmpty(dc, width, height);
            return;
        }

        var start = all.Min(p => p.At);
        var end = all.Max(p => p.At);
        var span = (end - start).TotalSeconds;
        if (span <= 0) return;

        // Usage is a percentage, so the axis is always 0-100. Auto-scaling would make a quiet week
        // look identical to a week spent at the limit.
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        gridPen.Freeze();

        // A gutter for the axis labels. Gridlines without numbers say a line went up, but not
        // whether it went up to 12% or to 90%, which is the only thing worth knowing here.
        const double Gutter = 34;
        var plotLeft = Gutter;
        var plotWidth = Math.Max(1, width - Gutter);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelFace = new Typeface(
            new FontFamily("IBM Plex Mono, Cascadia Mono, Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (var percent = 0; percent <= 100; percent += 25)
        {
            var y = height - (height * percent / 100.0);
            dc.DrawLine(gridPen, new Point(plotLeft, y), new Point(width, y));

            var label = new FormattedText(
                percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, labelFace, 10, AxisBrush, dpi);

            // Nudged so the baseline sits on the rule rather than hanging below it, and clamped
            // at the extremes so 0 and 100 stay inside the box.
            var labelY = Math.Clamp(y - (label.Height / 2), 0, height - label.Height);
            dc.DrawText(label, new Point(Gutter - label.Width - 8, labelY));
        }

        var linePen = new Pen(LineBrush, 1.6) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();

        var epochPen = new Pen(EpochBrush, 1);
        epochPen.Freeze();

        foreach (var one in series)
        {
            if (one.Points.Count < 2) continue;

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = true;
                foreach (var (at, value) in one.Points)
                {
                    var x = plotLeft + ((at - start).TotalSeconds / span * plotWidth);
                    var y = height - (Math.Clamp(value, 0, 100) / 100.0 * height);

                    if (first)
                    {
                        context.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                        first = false;
                    }
                    else
                    {
                        context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, linePen, geometry);

            // A rule at each epoch start, so a reset reads as a reset rather than as a collapse.
            var boundary = plotLeft + ((one.Points[0].At - start).TotalSeconds / span * plotWidth);
            if (boundary > plotLeft + 1) dc.DrawLine(epochPen, new Point(boundary, 0), new Point(boundary, height));
        }
    }

    private void DrawEmpty(DrawingContext dc, double width, double height)
    {
        var text = new FormattedText(
            "Not enough history yet.",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            GridBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}

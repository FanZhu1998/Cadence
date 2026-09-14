using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Cadence.App.Theme;

/// <summary>
/// Turns a provider's <c>#RRGGBB</c> accent into a brush.
/// </summary>
/// <remarks>
/// Provider accents come from the core descriptors, which have no WPF reference, so the conversion
/// happens here rather than in the model. Brushes are cached and frozen: the same handful of
/// colours are re-resolved on every card update.
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return Brushes.Gray;

        lock (Gate)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            SolidColorBrush brush;
            try
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch (FormatException)
            {
                brush = new SolidColorBrush(Colors.Gray);
            }

            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Inverts a boolean, for "collapse when true" bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Cadence.App.Theme;

/// <summary>
/// Letter-spacing for <see cref="TextBlock"/>, which WPF does not provide.
/// </summary>
/// <remarks>
/// The source design system leans on widely tracked uppercase monospace labels for structure, and
/// CSS gets that from <c>letter-spacing</c>. WPF has no equivalent: <c>Typography</c> exposes
/// OpenType features, not tracking, and <see cref="TextBlock"/> offers no hook into glyph advances
/// short of dropping to <c>Glyphs</c> or a custom <c>TextFormatter</c>.
/// <para>
/// The technique is to rebuild the text as alternating character and spacer runs, each spacer a
/// space rendered at whatever size yields the wanted advance. Inserting a space <em>character</em>
/// would not work here: the labels are monospaced, so every space is a full cell wide.
/// </para>
/// <para>
/// Bind <see cref="TextProperty"/> rather than <see cref="TextBlock.Text"/>. Writing to
/// <c>Inlines</c> assigns <c>Text</c> as a local value, which clears any binding on it — a label
/// bound to <c>Text</c> renders once and then never updates again, which is exactly the sort of
/// bug that looks like stale data rather than a broken binding. Routing through a separate
/// attached property leaves the binding somewhere this code never writes.
/// </para>
/// </remarks>
public static class Tracking
{
    /// <summary>
    /// The text to render tracked. Bind this instead of <see cref="TextBlock.Text"/>.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Tracking), new PropertyMetadata(null, OnChanged));

    /// <summary>Tracking in thousandths of an em, matching the CSS convention (0.14em is 140).</summary>
    public static readonly DependencyProperty EmProperty = DependencyProperty.RegisterAttached(
        "Em", typeof(double), typeof(Tracking), new PropertyMetadata(0d, OnChanged));

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetEm(DependencyObject element, double value) => element.SetValue(EmProperty, value);

    public static double GetEm(DependencyObject element) => (double)element.GetValue(EmProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block) Apply(block);
    }

    private static void Apply(TextBlock block)
    {
        var text = GetText(block) ?? string.Empty;
        var em = GetEm(block);

        block.Inlines.Clear();

        if (text.Length == 0) return;

        if (em <= 0 || text.Length < 2)
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        // A space glyph is about half an em in these faces, so the spacer's size is scaled to land
        // near the requested advance. Even, deliberate spacing is the goal, not exactness.
        var spacerSize = Math.Max(0.1, block.FontSize * (em / 1000.0) * 2.0);

        var inlines = new List<Inline>(text.Length * 2);

        for (var i = 0; i < text.Length; i++)
        {
            inlines.Add(new Run(text[i].ToString()));

            // No trailing spacer: it would push a right-aligned label off its own edge.
            if (i < text.Length - 1) inlines.Add(new Run(" ") { FontSize = spacerSize });
        }

        block.Inlines.AddRange(inlines);
    }
}

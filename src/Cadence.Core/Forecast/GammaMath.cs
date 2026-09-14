namespace Cadence.Core.Forecast;

/// <summary>
/// The incomplete-gamma numerics behind the prediction bands.
/// </summary>
/// <remarks>
/// Usage increments are non-negative and right-skewed, so the projection distribution is fitted as
/// a Gamma rather than a Normal. A Normal hands back negative P10s on bursty workloads, which then
/// render as "projected -4% at reset".
/// <para>
/// Series and continued-fraction expansions follow the standard Numerical Recipes formulation of
/// the regularised incomplete gamma function; log-gamma uses the Lanczos approximation.
/// </para>
/// </remarks>
public static class GammaMath
{
    private const int MaxIterations = 300;
    private const double Epsilon = 1e-14;

    // Lanczos g=7, n=9 coefficients.
    private static readonly double[] LanczosCoefficients =
    [
        676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012,
        9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    public static double LogGamma(double x)
    {
        if (double.IsNaN(x) || x <= 0) return double.NaN;

        // Reflection formula for the left half-plane.
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);

        x -= 1;
        var a = 0.99999999999980993;
        var t = x + 7.5;
        for (var i = 0; i < LanczosCoefficients.Length; i++)
            a += LanczosCoefficients[i] / (x + i + 1);

        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(a);
    }

    /// <summary>Regularised lower incomplete gamma P(a, x) = γ(a,x)/Γ(a), in [0,1].</summary>
    public static double LowerRegularized(double a, double x)
    {
        if (a <= 0 || double.IsNaN(a) || double.IsNaN(x)) return double.NaN;
        if (x <= 0) return 0d;
        if (double.IsPositiveInfinity(x)) return 1d;

        // The series converges quickly left of the mode; the continued fraction right of it.
        return x < a + 1d ? SeriesP(a, x) : 1d - ContinuedFractionQ(a, x);
    }

    /// <summary>Regularised upper incomplete gamma Q(a, x) = 1 - P(a, x).</summary>
    public static double UpperRegularized(double a, double x) => 1d - LowerRegularized(a, x);

    private static double SeriesP(double a, double x)
    {
        var term = 1d / a;
        var sum = term;
        for (var n = 1; n < MaxIterations; n++)
        {
            term *= x / (a + n);
            sum += term;
            if (Math.Abs(term) < Math.Abs(sum) * Epsilon) break;
        }

        return sum * Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a));
    }

    private static double ContinuedFractionQ(double a, double x)
    {
        // Modified Lentz's method.
        const double Tiny = 1e-300;
        var b = x + 1d - a;
        var c = 1d / Tiny;
        var d = 1d / b;
        var h = d;

        for (var i = 1; i < MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2d;
            d = (an * d) + b;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = b + (an / c);
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1d / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1d) < Epsilon) break;
        }

        return Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a)) * h;
    }
}

/// <summary>
/// A Gamma distribution parameterised by shape <c>k</c> and scale <c>theta</c>, fitted from the
/// mean and variance of projected additional usage.
/// </summary>
public sealed class GammaDistribution
{
    private GammaDistribution(double shape, double scale)
    {
        Shape = shape;
        Scale = scale;
    }

    public double Shape { get; }

    public double Scale { get; }

    public double Mean => Shape * Scale;

    public double Variance => Shape * Scale * Scale;

    /// <summary>
    /// Method-of-moments fit. Returns a <see cref="Degenerate"/> point mass when the variance is
    /// not usable, so callers never have to special-case a cold start.
    /// </summary>
    public static GammaDistribution FromMoments(double mean, double variance)
    {
        if (!double.IsFinite(mean) || mean <= 0) return Degenerate(Math.Max(0, mean));
        if (!double.IsFinite(variance) || variance <= 0) return Degenerate(mean);

        var shape = mean * mean / variance;
        var scale = variance / mean;

        // Guard the extremes: a huge shape is numerically a point mass, a tiny one makes the
        // quantile solver thrash for no useful gain in the band.
        if (!double.IsFinite(shape) || !double.IsFinite(scale) || shape > 1e8) return Degenerate(mean);

        return new GammaDistribution(Math.Max(shape, 1e-6), Math.Max(scale, 1e-12));
    }

    /// <summary>A point mass at <paramref name="value"/>: all quantiles equal it, CDF is a step.</summary>
    public static GammaDistribution Degenerate(double value) => new(double.PositiveInfinity, Math.Max(value, 0));

    public bool IsDegenerate => double.IsPositiveInfinity(Shape);

    /// <summary>P(X &lt;= x).</summary>
    public double Cdf(double x)
    {
        if (IsDegenerate) return x >= Scale ? 1d : 0d;
        if (x <= 0) return 0d;
        return Math.Clamp(GammaMath.LowerRegularized(Shape, x / Scale), 0d, 1d);
    }

    /// <summary>
    /// Inverse CDF for <paramref name="q"/> in (0,1), by bracket-then-bisect on <see cref="Cdf"/>.
    /// </summary>
    /// <remarks>
    /// Bisection rather than Newton: it cannot diverge, and at the shapes we see (often below 1,
    /// where the density is unbounded at zero) Newton's steps are unreliable. Sixty halvings on a
    /// bracket that starts at the mean converges far past display precision.
    /// </remarks>
    public double Quantile(double q)
    {
        if (IsDegenerate) return Scale;
        q = Math.Clamp(q, 1e-9, 1 - 1e-9);

        var lo = 0d;
        var hi = Math.Max(Mean, 1e-9);

        // Grow the upper bracket until it strictly exceeds the target probability.
        for (var i = 0; i < 200 && Cdf(hi) < q; i++) hi *= 2d;
        if (Cdf(hi) < q) return hi;

        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Cdf(mid) < q) lo = mid; else hi = mid;
        }

        return 0.5 * (lo + hi);
    }
}

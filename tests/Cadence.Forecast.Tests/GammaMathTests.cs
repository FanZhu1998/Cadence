using Cadence.Core.Forecast;

namespace Cadence.Forecast.Tests;

public class GammaMathTests
{
    [Theory]
    // Gamma(n) = (n-1)!  — exact anchors for the Lanczos approximation.
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(3.0, 2.0)]
    [InlineData(4.0, 6.0)]
    [InlineData(5.0, 24.0)]
    [InlineData(10.0, 362880.0)]
    public void LogGamma_MatchesFactorials(double x, double expected)
        => Assert.Equal(Math.Log(expected), GammaMath.LogGamma(x), 9);

    [Fact]
    public void LogGamma_MatchesHalfIntegerClosedForm()
    {
        // Gamma(1/2) = sqrt(pi)
        Assert.Equal(Math.Log(Math.Sqrt(Math.PI)), GammaMath.LogGamma(0.5), 9);
        // Gamma(3/2) = sqrt(pi)/2
        Assert.Equal(Math.Log(Math.Sqrt(Math.PI) / 2), GammaMath.LogGamma(1.5), 9);
    }

    [Theory]
    // For a = 1 the regularized lower incomplete gamma is the exponential CDF, 1 - e^-x.
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    [InlineData(7.0)]
    [InlineData(20.0)]
    public void LowerRegularized_ShapeOne_IsExponentialCdf(double x)
        => Assert.Equal(1 - Math.Exp(-x), GammaMath.LowerRegularized(1.0, x), 10);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(12.0)]
    public void LowerRegularized_CrossesTheSeriesToContinuedFractionBoundarySmoothly(double a)
    {
        // The implementation switches formulation at x == a + 1; the two expansions must agree
        // there. They converge to ~9-10 significant figures rather than to machine epsilon, which
        // is inherent to comparing two different expansions and is nine orders of magnitude finer
        // than anything the UI renders.
        var justBelow = GammaMath.LowerRegularized(a, a + 1 - 1e-9);
        var justAbove = GammaMath.LowerRegularized(a, a + 1 + 1e-9);

        Assert.Equal(justBelow, justAbove, 8);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(50.0)]
    public void LowerRegularized_IsAProperCdf(double shape)
    {
        Assert.Equal(0d, GammaMath.LowerRegularized(shape, 0));

        var previous = 0d;
        for (var x = 0.1; x < 200; x *= 1.3)
        {
            var p = GammaMath.LowerRegularized(shape, x);
            Assert.InRange(p, 0d, 1d);
            Assert.True(p >= previous - 1e-12, $"CDF decreased at x={x} for shape={shape}");
            previous = p;
        }

        Assert.Equal(1d, GammaMath.LowerRegularized(shape, 1e6), 8);
    }

    [Theory]
    [InlineData(2.0, 3.0)]
    [InlineData(0.5, 10.0)]
    [InlineData(9.0, 0.25)]
    public void Quantile_InvertsCdf(double shape, double scale)
    {
        var d = GammaDistribution.FromMoments(shape * scale, shape * scale * scale);

        foreach (var q in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99 })
        {
            var x = d.Quantile(q);
            Assert.Equal(q, d.Cdf(x), 6);
        }
    }

    [Fact]
    public void FromMoments_RecoversTheRequestedMomentsAndOrdersQuantiles()
    {
        var d = GammaDistribution.FromMoments(mean: 40, variance: 100);

        Assert.Equal(40, d.Mean, 6);
        Assert.Equal(100, d.Variance, 6);
        Assert.True(d.Quantile(0.1) < d.Quantile(0.5));
        Assert.True(d.Quantile(0.5) < d.Quantile(0.9));
    }

    [Fact]
    public void Quantile_IsNeverNegative_EvenWhenVarianceDwarfsTheMean()
    {
        // The reason for Gamma over Normal: a Normal P10 here would be far below zero, and would
        // surface in the UI as a negative projected usage.
        var d = GammaDistribution.FromMoments(mean: 5, variance: 400);

        Assert.True(d.Quantile(0.10) >= 0, "P10 must not go negative");
        Assert.True(d.Quantile(0.01) >= 0, "P01 must not go negative");
    }

    [Fact]
    public void Degenerate_BehavesAsAPointMass()
    {
        var d = GammaDistribution.FromMoments(mean: 12, variance: 0);

        Assert.True(d.IsDegenerate);
        Assert.Equal(12, d.Quantile(0.1), 9);
        Assert.Equal(12, d.Quantile(0.9), 9);
        Assert.Equal(0d, d.Cdf(11.9));
        Assert.Equal(1d, d.Cdf(12.1));
    }

    [Fact]
    public void FromMoments_DegradesToAPointMassRatherThanThrowing_OnDegenerateInput()
    {
        // Cold start: no samples yet, so mean and variance are whatever the caller could compute.
        Assert.True(GammaDistribution.FromMoments(0, 0).IsDegenerate);
        Assert.True(GammaDistribution.FromMoments(-3, 10).IsDegenerate);
        Assert.True(GammaDistribution.FromMoments(5, double.NaN).IsDegenerate);
    }
}

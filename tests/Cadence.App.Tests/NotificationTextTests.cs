using Cadence.App.Notifications;
using Cadence.Core.Alerts;
using Cadence.Core.Model;

namespace Cadence.App.Tests;

/// <summary>What a usage notification says. Short, and the number first.</summary>
public class NotificationTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    private static UsageAlert Alert(double threshold, double used, DateTimeOffset? exhaustsAt = null)
        => new(ProviderId.Claude, "session", "5-hour", threshold, used, Now.AddHours(2).AddMinutes(10), exhaustsAt);

    [Fact]
    public void AThreshold_LeadsWithTheActualUsage_ThenTheReset()
    {
        var text = NotificationManager.Describe(Alert(90, 92.4), Now, withReset: true);

        Assert.StartsWith("92% used.", text, StringComparison.Ordinal);
        Assert.Contains("Resets in", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningOut_SaysSoPlainly()
        => Assert.StartsWith("Limit reached.", NotificationManager.Describe(Alert(100, 100), Now, withReset: true), StringComparison.Ordinal);

    [Fact]
    public void AForecastWarning_SaysWhenItRunsOut()
        => Assert.StartsWith(
            "On track to run out around ",
            NotificationManager.Describe(Alert(0, 40, Now.AddHours(1)), Now, withReset: false),
            StringComparison.Ordinal);
}

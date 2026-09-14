using Cadence.App.ViewModels;
using Cadence.Core.Model;
using Cadence.Core.Refresh;

namespace Cadence.App.Tests;

/// <summary>
/// The HUD's whole value is the text. There is room for about forty characters per provider, so
/// each token has to be unambiguous at a glance and never wider than its column.
/// </summary>
public class HudTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static Forecast Available(
        double median = 60, double paceDelta = 0, double exhaustProbability = 0, DateTimeOffset? exhaustsAt = null)
        => new()
        {
            Median = median,
            P10 = median - 10,
            P90 = median + 10,
            PaceDelta = paceDelta,
            ExhaustProbability = exhaustProbability,
            ExhaustsAt = exhaustsAt,
        };

    private static QuotaWindow Window(Forecast? forecast, DateTimeOffset? resetsAt = null)
        => new()
        {
            Id = "test",
            Title = "Session (5h)",
            Kind = WindowKind.Session,
            UsedPercent = 42,
            ResetsAt = resetsAt ?? Now.AddHours(3),
            WindowLength = TimeSpan.FromHours(5),
            Forecast = forecast,
        };

    private static ProviderState State(QuotaWindow? window, FetchError? error = null, bool circuitOpen = false)
        => new()
        {
            Provider = ProviderId.Claude,
            Error = error,
            CircuitOpen = circuitOpen,
            Snapshot = window is null ? null : new UsageSnapshot
            {
                Provider = ProviderId.Claude,
                FetchedAt = Now,
                SourceLabel = "oauth",
                Windows = [window],
            },
        };

    // ---- pace ------------------------------------------------------------------------------------

    [Fact]
    public void Pace_ShowsAnUpArrowWhenBurningTooFast()
        => Assert.Equal("↑11", HudViewModel.PaceToken(Available(paceDelta: 11)));

    [Fact]
    public void Pace_ShowsADownArrowWhenInReserve()
        => Assert.Equal("↓8", HudViewModel.PaceToken(Available(paceDelta: -8)));

    [Fact]
    public void Pace_ShowsAFlatArrowOnEvenPace()
    {
        // A tiny delta is noise, not news. Rendering "↑0" invites the eye to a non-event.
        Assert.Equal("→", HudViewModel.PaceToken(Available(paceDelta: 0.4)));
        Assert.Equal("→", HudViewModel.PaceToken(Available(paceDelta: -1.0)));
    }

    [Fact]
    public void Pace_IsEmptyWithoutAForecast()
    {
        Assert.Empty(HudViewModel.PaceToken(null));
        Assert.Empty(HudViewModel.PaceToken(Forecast.NotAvailable(ForecastUnavailableReason.TooEarly)));
    }

    // ---- outlook ---------------------------------------------------------------------------------

    [Fact]
    public void Outlook_LeadsWithAClockTimeWhenExhaustionIsLikely()
    {
        var forecast = Available(median: 100, exhaustProbability: 0.8, exhaustsAt: Now.AddHours(2));

        var token = HudViewModel.OutlookToken(State(Window(forecast)), Window(forecast), Now);

        // A time is actionable; a percentage at this size is not.
        Assert.StartsWith("out ", token, StringComparison.Ordinal);
        Assert.Contains(Now.AddHours(2).ToLocalTime().ToString("HH:mm"), token, StringComparison.Ordinal);
    }

    [Fact]
    public void Outlook_IncludesTheWeekdayWhenExhaustionIsNotToday()
    {
        var exhaustsAt = Now.AddDays(2);
        var forecast = Available(median: 100, exhaustProbability: 0.9, exhaustsAt: exhaustsAt);

        var token = HudViewModel.OutlookToken(State(Window(forecast)), Window(forecast), Now);

        Assert.Contains(exhaustsAt.ToLocalTime().ToString("ddd"), token, StringComparison.Ordinal);
    }

    [Fact]
    public void Outlook_ShowsTheProjectionWhenExhaustionIsUnlikely()
    {
        var forecast = Available(median: 63, exhaustProbability: 0.05);

        Assert.Equal("→63%", HudViewModel.OutlookToken(State(Window(forecast)), Window(forecast), Now));
    }

    [Fact]
    public void Outlook_FallsBackToTheCountdownWithoutAForecast()
    {
        // Early in a window there is no honest projection, but the reset time is always true.
        var window = Window(Forecast.NotAvailable(ForecastUnavailableReason.TooEarly));

        Assert.Equal("3h00", HudViewModel.OutlookToken(State(window), window, Now));
    }

    [Theory]
    [InlineData("sign in", FetchErrorKind.NotLoggedIn)]
    [InlineData("sign in", FetchErrorKind.TokenExpired)]
    [InlineData("sign in", FetchErrorKind.ScopeMissing)]
    public void Outlook_AsksForTheOneActionThatFixesATerminalError(string expected, FetchErrorKind kind)
    {
        var state = State(Window(Available()), new FetchError(kind, "nope"));

        Assert.Equal(expected, HudViewModel.OutlookToken(state, Window(Available()), Now));
    }

    [Fact]
    public void Outlook_SaysPausedWhenTheCircuitIsOpen()
    {
        var state = State(Window(Available()), circuitOpen: true);

        Assert.Equal("paused", HudViewModel.OutlookToken(state, Window(Available()), Now));
    }

    [Fact]
    public void Outlook_SaysStaleWhenThereIsNoWindowButThereIsAnError()
    {
        var state = State(null, new FetchError(FetchErrorKind.Network, "offline"));

        Assert.Equal("stale", HudViewModel.OutlookToken(state, null, Now));
    }

    // ---- countdown ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 45, "45m")]
    [InlineData(3, 12, "3h12")]
    [InlineData(26, 0, "1d2h")]
    [InlineData(0, 0, "now")]
    public void Compact_FitsACountdownIntoAStrip(int hours, int minutes, string expected)
        => Assert.Equal(expected, HudViewModel.Compact(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Compact_PadsMinutesSoTheColumnDoesNotJitter()
    {
        // "3h05" and "3h12" are the same width; "3h5" and "3h12" are not, and the whole row
        // shifts every time the value crosses ten minutes.
        Assert.Equal("3h05", HudViewModel.Compact(TimeSpan.FromMinutes(185)));
    }
}

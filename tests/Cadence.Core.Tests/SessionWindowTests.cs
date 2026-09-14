using Cadence.Core.Model;

namespace Cadence.Core.Tests;

/// <summary>
/// The session window is what the HUD shows. It has to be the five-hour session even when a weekly
/// or per-model limit is fuller, which is exactly when choosing the fullest window misled.
/// </summary>
public class SessionWindowTests
{
    private static QuotaWindow Window(string id, WindowKind kind, double? used)
        => new() { Id = id, Title = id, Kind = kind, UsedPercent = used };

    private static UsageSnapshot Snapshot(params QuotaWindow[] windows)
        => new() { Provider = ProviderId.Claude, FetchedAt = DateTimeOffset.UnixEpoch, SourceLabel = "test", Windows = windows };

    [Fact]
    public void IsTheSession_EvenWhenAWeeklyModelLimitIsFuller()
    {
        var snapshot = Snapshot(
            Window("claude.session", WindowKind.Session, 2),
            Window("claude.weekly_all", WindowKind.Weekly, 62),
            Window("claude.weekly_scoped.fable", WindowKind.Model, 78));

        Assert.Equal("claude.session", snapshot.SessionWindow?.Id);

        // The tray's choice is unchanged: it still tracks whichever limit is closest to running out.
        Assert.Equal("claude.weekly_scoped.fable", snapshot.PrimaryWindow?.Id);
    }

    [Fact]
    public void IsChosenByKind_NotById()
        => Assert.Equal("anything-5h", Snapshot(
            Window("weekly", WindowKind.Weekly, 90),
            Window("anything-5h", WindowKind.Session, 10)).SessionWindow?.Id);

    [Fact]
    public void OfSeveralSessions_TheFullestWins()
        => Assert.Equal("b", Snapshot(
            Window("a", WindowKind.Session, 20),
            Window("b", WindowKind.Session, 70)).SessionWindow?.Id);

    [Fact]
    public void ASessionWithUnknownUsage_IsStillTheSession()
        => Assert.Equal("s", Snapshot(
            Window("s", WindowKind.Session, null),
            Window("w", WindowKind.Weekly, 50)).SessionWindow?.Id);

    [Fact]
    public void IsNull_WhenTheProviderHasNoSessionWindow()
        => Assert.Null(Snapshot(
            Window("w", WindowKind.Weekly, 50),
            Window("m", WindowKind.Monthly, 10)).SessionWindow);
}

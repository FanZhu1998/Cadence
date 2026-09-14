using Cadence.App.ViewModels;
using Cadence.Core.Model;
using Cadence.Core.Refresh;

namespace Cadence.App.Tests;

/// <summary>
/// The HUD reports each provider's current session and nothing else, so a weekly limit that is days
/// away never turns up in the corner of the screen looking like an emergency.
/// </summary>
public class HudSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<ProviderId, ProviderDescriptorInfo> Descriptors = new()
    {
        [ProviderId.Claude] = new("Claude", "CLD", "#D97757"),
        [ProviderId.Codex] = new("Codex", "DEX", "#10A37F"),
    };

    private static QuotaWindow Window(string id, WindowKind kind, double used)
        => new() { Id = id, Title = id, Kind = kind, UsedPercent = used, ResetsAt = Now.AddHours(3) };

    private static UsageSnapshot Snapshot(ProviderId provider, params QuotaWindow[] windows)
        => new() { Provider = provider, FetchedAt = Now, SourceLabel = "test", Windows = windows };

    private static HudViewModel Hud(params UsageSnapshot[] snapshots)
    {
        var store = new UsageStore();
        foreach (var snapshot in snapshots) store.RecordSuccess(snapshot);

        var hud = new HudViewModel(store, Descriptors);
        hud.Sync(Now);
        return hud;
    }

    [Fact]
    public void ShowsTheSession_NotAFullerWeeklyModelLimit()
    {
        var hud = Hud(Snapshot(ProviderId.Claude,
            Window("claude.session", WindowKind.Session, 2),
            Window("claude.weekly_all", WindowKind.Weekly, 62),
            Window("claude.weekly_scoped.fable", WindowKind.Model, 78)));

        var row = Assert.Single(hud.Rows);
        Assert.Equal("2%", row.PercentText);
        Assert.False(row.IsWarning);
    }

    [Fact]
    public void EachProviderShowsItsOwnSession()
    {
        var hud = Hud(
            Snapshot(ProviderId.Claude,
                Window("claude.session", WindowKind.Session, 12),
                Window("claude.weekly_all", WindowKind.Weekly, 88)),
            Snapshot(ProviderId.Codex,
                Window("codex.primary_window", WindowKind.Session, 40),
                Window("codex.secondary_window", WindowKind.Weekly, 97)));

        Assert.Equal(new[] { "12%", "40%" }, hud.Rows.Select(r => r.PercentText));
    }

    [Fact]
    public void AProviderWithoutASession_ShowsADash_AndDoesNotClaimToBeLoading()
    {
        var hud = Hud(Snapshot(ProviderId.Claude, Window("claude.weekly_all", WindowKind.Weekly, 62)));

        var row = Assert.Single(hud.Rows);
        Assert.Equal("–", row.PercentText);
        Assert.Equal(string.Empty, row.OutlookText);
        Assert.False(hud.HasAnyData);
    }

    [Fact]
    public void BeforeAnythingArrives_TheOutlookStillSaysLoading()
        => Assert.Equal("…", HudViewModel.OutlookToken(new ProviderState { Provider = ProviderId.Claude }, null, Now));
}

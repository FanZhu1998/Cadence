using System.Net.Http;
using Cadence.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cadence.App.Tests;

/// <summary>
/// The update service's decisions, against a fake updater. The real one is exercised end to end by
/// installing a build, publishing a newer one to a local feed, and watching it arrive.
/// </summary>
public class UpdateServiceTests
{
    private sealed class FakeEngine : IUpdateEngine
    {
        private int _checks;

        public bool IsInstalled { get; init; } = true;

        public string? CurrentVersion { get; init; } = "0.1.0";

        public string? PendingVersion { get; init; }

        public string? Available { get; set; }

        public Exception? Failure { get; set; }

        public int Checks => Volatile.Read(ref _checks);

        public int Downloads { get; private set; }

        public int InstallsOnExit { get; private set; }

        public Task<string?> CheckAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _checks);
            return Failure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(Available);
        }

        public Task DownloadAsync(CancellationToken ct)
        {
            Downloads++;
            return Task.CompletedTask;
        }

        public void InstallOnExit() => InstallsOnExit++;
    }

    private static UpdateService Create(FakeEngine engine, bool automatic = true)
        => new(engine, () => automatic, NullLogger<UpdateService>.Instance);

    [Fact]
    public async Task ACopyThatIsNotInstalled_NeverContactsTheFeed()
    {
        var engine = new FakeEngine { IsInstalled = false };
        using var updates = Create(engine);

        updates.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
        var status = await updates.CheckNowAsync();

        Assert.Equal(UpdateState.Unavailable, status.State);
        Assert.Equal(0, engine.Checks);
    }

    [Fact]
    public async Task NothingNewer_ReportsTheCurrentVersionAsLatest()
    {
        using var updates = Create(new FakeEngine());

        Assert.Equal(new UpdateStatus(UpdateState.UpToDate, "0.1.0"), await updates.CheckNowAsync());
    }

    [Fact]
    public async Task ANewerVersion_IsDownloadedAndWaitsToInstall()
    {
        var engine = new FakeEngine { Available = "0.2.0" };
        using var updates = Create(engine);

        Assert.Equal(new UpdateStatus(UpdateState.ReadyToInstall, "0.2.0"), await updates.CheckNowAsync());
        Assert.Equal(1, engine.Downloads);

        // Checking again does not download it a second time.
        await updates.CheckNowAsync();
        Assert.Equal(1, engine.Downloads);
    }

    [Fact]
    public void AVersionDownloadedBeforeARestart_IsReadyStraightAway()
    {
        using var updates = Create(new FakeEngine { PendingVersion = "0.2.0" });

        Assert.Equal(new UpdateStatus(UpdateState.ReadyToInstall, "0.2.0"), updates.Status);
    }

    [Fact]
    public async Task AFailedCheck_IsReported_AndTheNextOneCanStillSucceed()
    {
        var engine = new FakeEngine { Failure = new HttpRequestException("offline") };
        using var updates = Create(engine);

        Assert.Equal(UpdateState.Failed, (await updates.CheckNowAsync()).State);

        engine.Failure = null;
        Assert.Equal(UpdateState.UpToDate, (await updates.CheckNowAsync()).State);
    }

    [Fact]
    public async Task InstallNow_HandsOverThenShutsDown_OnlyWhenAVersionIsReady()
    {
        var engine = new FakeEngine { Available = "0.2.0" };
        using var updates = Create(engine);
        var shutdowns = 0;

        Assert.False(updates.InstallNow(() => shutdowns++));
        Assert.Equal(0, shutdowns);

        await updates.CheckNowAsync();

        Assert.True(updates.InstallNow(() => shutdowns++));
        Assert.Equal(1, engine.InstallsOnExit);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task BackgroundChecks_FollowTheSetting()
    {
        var off = new FakeEngine();
        using (var updates = Create(off, automatic: false))
        {
            updates.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
            await Task.Delay(150);
        }

        var on = new FakeEngine();
        using (var updates = Create(on, automatic: true))
        {
            updates.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
            for (var waited = 0; on.Checks == 0 && waited < 5000; waited += 20) await Task.Delay(20);
        }

        Assert.Equal(0, off.Checks);
        Assert.True(on.Checks > 0);
    }
}

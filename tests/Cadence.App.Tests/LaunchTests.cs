using System.IO;
using Cadence.App.Launch;

namespace Cadence.App.Tests;

/// <summary>
/// The single-instance guard used to build its lock name from <c>string.GetHashCode()</c>, which
/// .NET randomises per process, so it never matched and every double-click started another copy.
/// These pin the contract the fix relies on. The cross-process case itself is verified by
/// launching the real exe twice, since a single test process cannot reproduce hash randomisation.
/// </summary>
public class SingleInstanceTests
{
    // A fresh name per test, so neither a real running Cadence nor another test can interfere.
    private static string UniqueName() => $"Cadence.Test.{Guid.NewGuid():N}";

    [Fact]
    public void TheFirstClaimOwnsTheLock()
    {
        using var first = new SingleInstance(UniqueName());

        Assert.True(first.IsFirstInstance);
    }

    [Fact]
    public void ASecondClaimOfTheSameNameIsNotTheFirstInstance()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);

        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void AfterTheFirstQuits_TheNextLaunchOwnsTheLockAgain()
    {
        // Quit and relaunch is the ordinary case. A lock that outlived its owner would leave Cadence
        // refusing to start until the next sign-in.
        var name = UniqueName();

        var first = new SingleInstance(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using var relaunch = new SingleInstance(name);
        Assert.True(relaunch.IsFirstInstance);
    }

    [Fact]
    public void ASecondLaunchWakesTheRunningInstance()
    {
        var name = UniqueName();

        // Declared first so it is disposed last, after the listener has been unregistered.
        using var woken = new ManualResetEventSlim(false);
        using var running = new SingleInstance(name);
        running.ListenForActivation(woken.Set);

        using var secondLaunch = new SingleInstance(name);
        Assert.True(secondLaunch.SignalFirstInstance());

        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)), "the running instance was never told about the second launch");
    }

    [Fact]
    public void EveryLaunchWakesIt_NotOnlyTheFirst()
    {
        // The event auto-resets, so the listener has to re-arm after each wake. A one-shot wait would
        // bring the panel forward once and then ignore every later double-click.
        var name = UniqueName();
        var wakes = 0;

        using var third = new ManualResetEventSlim(false);
        using var running = new SingleInstance(name);
        running.ListenForActivation(() =>
        {
            if (Interlocked.Increment(ref wakes) == 3) third.Set();
        });

        for (var i = 0; i < 3; i++)
        {
            using var launch = new SingleInstance(name);
            launch.SignalFirstInstance();

            // Space the signals out: two sets landing before the waiter wakes coalesce into one.
            Thread.Sleep(150);
        }

        Assert.True(third.Wait(TimeSpan.FromSeconds(5)), $"only {wakes} of 3 launches woke the running instance");
    }
}

public class StartupIntegrationTests
{
    [Fact]
    public void CreateShortcut_WritesARealShellLink()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cadence-lnk-{Guid.NewGuid():N}");

        // Nested one level deeper than exists, which also proves the folder is created.
        var lnk = Path.Combine(dir, "Programs", "Cadence.lnk");

        try
        {
            StartupIntegration.CreateShortcut(lnk, Environment.ProcessPath!, "test shortcut");

            Assert.True(File.Exists(lnk));

            // Every shell link opens with a four-byte header size of 0x4C. Checking it proves this is
            // a real .lnk, not a text file that merely has the right name.
            var bytes = File.ReadAllBytes(lnk);
            Assert.True(bytes.Length > 76, $"only {bytes.Length} bytes");
            Assert.Equal(new byte[] { 0x4C, 0x00, 0x00, 0x00 }, bytes[..4]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateShortcut_ReplacesAnExistingOne()
    {
        // Running setup again after moving the exe must repair the shortcut, not fail on the old one.
        var dir = Path.Combine(Path.GetTempPath(), $"cadence-lnk-{Guid.NewGuid():N}");
        var lnk = Path.Combine(dir, "Cadence.lnk");

        try
        {
            StartupIntegration.CreateShortcut(lnk, Environment.ProcessPath!, "first");
            StartupIntegration.CreateShortcut(lnk, Environment.ProcessPath!, "second");

            Assert.True(File.Exists(lnk));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StartMenuShortcut_LivesInThePerUserProgramsFolder()
    {
        // Per-user, never the all-users Start menu: that one needs elevation, and Cadence never asks.
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        Assert.StartsWith(programs, StartupIntegration.StartMenuShortcutPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Cadence.lnk", StartupIntegration.StartMenuShortcutPath, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Install and uninstall decide whether the sign-in entry is theirs by reading it back, so the
/// parsing has to cope with the shapes Windows accepts, not only the one Cadence writes.
/// </summary>
public class RunCommandTests
{
    [Theory]
    [InlineData(@"""C:\Users\a\AppData\Local\CadenceApp\current\Cadence.exe""", @"C:\Users\a\AppData\Local\CadenceApp\current\Cadence.exe")]
    [InlineData(@"""C:\Program Files\Cadence\Cadence.exe"" --show", @"C:\Program Files\Cadence\Cadence.exe")]
    [InlineData(@"C:\Tools\Cadence.exe --show", @"C:\Tools\Cadence.exe")]
    [InlineData(@"C:\Program Files\Cadence\Cadence.exe", @"C:\Program Files\Cadence\Cadence.exe")]
    public void FindsTheExecutable(string command, string expected)
        => Assert.Equal(expected, StartupIntegration.ParseRunCommand(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    public void NothingUsable_IsNull(string? command)
        => Assert.Null(StartupIntegration.ParseRunCommand(command));

    [Fact]
    public void SamePath_IgnoresCaseAndDotSegments()
    {
        Assert.True(StartupIntegration.SamePath(
            @"C:\Users\A\AppData\Local\CadenceApp\current\Cadence.exe",
            @"c:\users\a\appdata\local\cadenceapp\current\..\current\CADENCE.exe"));

        Assert.False(StartupIntegration.SamePath(@"C:\a\Cadence.exe", @"C:\b\Cadence.exe"));
        Assert.False(StartupIntegration.SamePath(null, @"C:\a\Cadence.exe"));
    }
}

namespace Cadence.App.Updates;

/// <summary>
/// The parts of the updater <see cref="UpdateService"/> relies on, kept narrow so the service's
/// behaviour can be tested without an installed copy or a release feed.
/// </summary>
public interface IUpdateEngine
{
    /// <summary>False for a development build or a bare exe: only an installed copy can update.</summary>
    bool IsInstalled { get; }

    /// <summary>The installed version, or null when not installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>A version already downloaded and waiting to be installed, if any.</summary>
    string? PendingVersion { get; }

    /// <summary>Returns the newer version on the feed, or null when this one is the latest.</summary>
    Task<string?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the version the last <see cref="CheckAsync"/> found.</summary>
    Task DownloadAsync(CancellationToken ct);

    /// <summary>
    /// Arranges for the downloaded version to be installed as soon as this process exits, and for
    /// Cadence to start again afterwards. The caller then shuts down normally.
    /// </summary>
    void InstallOnExit();
}

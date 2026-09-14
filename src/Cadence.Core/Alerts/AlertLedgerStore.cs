using System.Text.Json;
using Cadence.Core.Credentials;

namespace Cadence.Core.Alerts;

/// <summary>
/// Keeps the <see cref="AlertLedger"/> in a small JSON file beside the history database.
/// </summary>
/// <remarks>
/// This is state, not configuration, so it lives in the local data folder rather than beside the
/// roaming settings. Losing it is harmless: the worst case is a warning given once more.
/// </remarks>
public sealed class AlertLedgerStore(string? path = null)
{
    /// <summary>Windows and notices not seen for this long are dropped on save.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    private readonly string _path = path ?? KnownPaths.AlertStateFile;

    public string Path => _path;

    /// <summary>Reads the ledger, or returns an empty one if there is none or it cannot be read.</summary>
    public AlertLedger Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AlertLedger();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AlertLedger>(json, SettingsStore.Json) ?? new AlertLedger();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AlertLedger();
        }
    }

    /// <summary>Writes the ledger atomically, dropping entries older than <see cref="Retention"/>.</summary>
    /// <returns>False when the file could not be written; the ledger then stays marked as changed.</returns>
    public bool Save(AlertLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        foreach (var key in ledger.Windows.Where(kv => now - kv.Value.LastSeen > Retention).Select(kv => kv.Key).ToList())
            ledger.Windows.Remove(key);

        foreach (var key in ledger.Notices.Where(kv => now - kv.Value > Retention).Select(kv => kv.Key).ToList())
            ledger.Notices.Remove(key);

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger, SettingsStore.Json));

            if (File.Exists(_path)) File.Replace(temp, _path, destinationBackupFileName: null);
            else File.Move(temp, _path);

            ledger.MarkSaved();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

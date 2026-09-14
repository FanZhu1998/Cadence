using System.Text.Json;
using System.Text.Json.Serialization;
using Cadence.Core.Model;

namespace Cadence.Core.Credentials;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// Writes go through a temp file and an atomic replace, so a crash mid-save cannot leave a
/// truncated config that loses every provider setting. A config written by a newer build is left
/// strictly alone rather than being silently rewritten in an older shape.
/// </remarks>
public sealed class SettingsStore(string? path = null)
{
    private readonly string _path = path ?? KnownPaths.ConfigFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Path => _path;

    /// <summary>True when the on-disk config came from a newer Cadence and must not be overwritten.</summary>
    public bool IsFromFutureVersion { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return AppSettings.Default;

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Json, ct).ConfigureAwait(false);
            if (settings is null) return AppSettings.Default;

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Downgrading the file would discard settings this build does not know about.
                IsFromFutureVersion = true;
                return settings;
            }

            return Migrate(settings);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt config must not stop the app from starting; defaults are always valid.
            return AppSettings.Default;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (IsFromFutureVersion) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            var temp = _path + ".tmp";

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            // Atomic swap, so an interrupted save never leaves a half-written config.
            if (File.Exists(_path)) File.Replace(temp, _path, destinationBackupFileName: null);
            else File.Move(temp, _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings an older config forward. Version 1 is the first, so this is currently identity, but
    /// the chain exists from the start because this model will change.
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
        => settings.SchemaVersion == AppSettings.CurrentSchemaVersion
            ? settings
            : settings with { SchemaVersion = AppSettings.CurrentSchemaVersion };
}

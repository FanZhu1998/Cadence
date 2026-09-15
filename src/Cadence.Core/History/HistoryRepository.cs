using Cadence.Core.Cost;
using Cadence.Core.Credentials;
using Cadence.Core.Forecast;
using Cadence.Core.Model;
using Microsoft.Data.Sqlite;

namespace Cadence.Core.History;

/// <summary>One stored usage observation, as it comes back out of the database.</summary>
public sealed record StoredSample(
    ProviderId Provider,
    string WindowId,
    DateTimeOffset Timestamp,
    double? UsedPercent,
    long? UsedUnits,
    DateTimeOffset? ResetsAt);

/// <summary>Aggregated token totals over a period.</summary>
public sealed record CostTotals(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    public static readonly CostTotals Empty = new(0, 0, 0, 0);
}

/// <summary>A day's totals, for the history chart.</summary>
public sealed record DailyCost(DateOnly Day, string Model, long TotalTokens);

/// <summary>
/// Append-only sample store plus the cost ledger, on SQLite with WAL.
/// </summary>
/// <remarks>
/// Epoch ids are deliberately <em>not</em> persisted. They are derived on read by
/// <see cref="EpochDetector"/>, so a fix to boundary detection immediately corrects all existing
/// history rather than leaving old rows grouped by superseded logic.
/// </remarks>
public sealed class HistoryRepository : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HistoryRepository(SqliteConnection connection) => _connection = connection;

    public static async Task<HistoryRepository> OpenAsync(string? path = null, CancellationToken ct = default)
    {
        var file = path ?? KnownPaths.HistoryDatabase;

        if (file != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = file == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var repository = new HistoryRepository(connection);
        await repository.MigrateAsync(ct).ConfigureAwait(false);
        return repository;
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        // WAL keeps the UI's reads from blocking the refresh loop's writes.
        await ExecuteAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecuteAsync("PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);

        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS usage_sample (
              provider   TEXT    NOT NULL,
              window_id  TEXT    NOT NULL,
              ts_utc     INTEGER NOT NULL,
              used_pct   REAL,
              used_units INTEGER,
              resets_at  INTEGER,
              PRIMARY KEY (provider, window_id, ts_utc)
            ) WITHOUT ROWID;
            """, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS ix_sample_window_time ON usage_sample(provider, window_id, ts_utc);",
            ct).ConfigureAwait(false);

        // entry_id is the provider's own dedup key: (message.id|requestId) for Claude,
        // response_id for Codex. The primary key is what makes rescanning a file idempotent.
        // Databases created while Cadence still estimated dollars also have a cost_usd column. It is
        // never read or written now, and its default keeps inserts that leave it out valid.
        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS cost_entry (
              provider              TEXT    NOT NULL,
              entry_id              TEXT    NOT NULL,
              ts_utc                INTEGER NOT NULL,
              model                 TEXT    NOT NULL,
              input_tokens          INTEGER NOT NULL DEFAULT 0,
              output_tokens         INTEGER NOT NULL DEFAULT 0,
              cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
              cache_write_5m_tokens INTEGER NOT NULL DEFAULT 0,
              cache_write_1h_tokens INTEGER NOT NULL DEFAULT 0,
              reasoning_tokens      INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (provider, entry_id)
            ) WITHOUT ROWID;
            """, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS ix_cost_time ON cost_entry(provider, ts_utc);", ct).ConfigureAwait(false);

        // Incremental scan bookmarks, so an 80 MB transcript is not reparsed every minute.
        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS scan_cursor (
              path        TEXT    NOT NULL PRIMARY KEY,
              size_bytes  INTEGER NOT NULL,
              mtime_utc   INTEGER NOT NULL,
              byte_offset INTEGER NOT NULL
            ) WITHOUT ROWID;
            """, ct).ConfigureAwait(false);

        await ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;",
            ct).ConfigureAwait(false);
    }

    // ---- usage samples ---------------------------------------------------------------------------

    /// <summary>Appends every known window in a snapshot. Re-recording the same instant is a no-op.</summary>
    public async Task AppendAsync(UsageSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.Windows.Count == 0) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = _connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO usage_sample (provider, window_id, ts_utc, used_pct, used_units, resets_at)
                VALUES ($provider, $window, $ts, $pct, $units, $reset)
                ON CONFLICT(provider, window_id, ts_utc) DO UPDATE SET
                  used_pct = excluded.used_pct,
                  used_units = excluded.used_units,
                  resets_at = excluded.resets_at;
                """;

            var provider = command.Parameters.Add("$provider", SqliteType.Text);
            var window = command.Parameters.Add("$window", SqliteType.Text);
            var ts = command.Parameters.Add("$ts", SqliteType.Integer);
            var pct = command.Parameters.Add("$pct", SqliteType.Real);
            var units = command.Parameters.Add("$units", SqliteType.Integer);
            var reset = command.Parameters.Add("$reset", SqliteType.Integer);

            provider.Value = snapshot.Provider.ToString();
            ts.Value = snapshot.FetchedAt.ToUnixTimeSeconds();

            foreach (var quotaWindow in snapshot.Windows)
            {
                window.Value = quotaWindow.Id;
                pct.Value = (object?)quotaWindow.UsedPercent ?? DBNull.Value;
                units.Value = (object?)quotaWindow.UsedUnits ?? DBNull.Value;
                reset.Value = quotaWindow.ResetsAt is { } r ? r.ToUnixTimeSeconds() : DBNull.Value;

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads samples for one window, oldest first.</summary>
    public async Task<IReadOnlyList<UsageSample>> ReadSamplesAsync(
        ProviderId provider, string windowId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT ts_utc, used_pct, used_units, resets_at
            FROM usage_sample
            WHERE provider = $provider AND window_id = $window AND ts_utc >= $since
            ORDER BY ts_utc;
            """;

        command.Parameters.AddWithValue("$provider", provider.ToString());
        command.Parameters.AddWithValue("$window", windowId);
        command.Parameters.AddWithValue("$since", since?.ToUnixTimeSeconds() ?? 0L);

        var samples = new List<UsageSample>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            samples.Add(new UsageSample(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return samples;
    }

    /// <summary>Builds the epoch-split history the forecaster consumes.</summary>
    public async Task<WindowHistory> ReadHistoryAsync(
        ProviderId provider, QuotaWindow window, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var samples = await ReadSamplesAsync(provider, window.Id, since, ct).ConfigureAwait(false);

        return new WindowHistory(
            window.Id, window.Kind, window.WindowLength,
            EpochDetector.Split(samples, window.Id, window.Kind, window.WindowLength));
    }

    public async Task<IReadOnlyList<string>> ListWindowIdsAsync(ProviderId provider, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT window_id FROM usage_sample WHERE provider = $provider ORDER BY window_id;";
        command.Parameters.AddWithValue("$provider", provider.ToString());

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));

        return ids;
    }

    // ---- cost ledger -----------------------------------------------------------------------------

    /// <summary>
    /// Upserts scanned cost entries. Idempotent by <c>(provider, entry_id)</c>, which is what makes
    /// rescanning a partially-read transcript safe.
    /// </summary>
    public async Task UpsertCostEntriesAsync(IReadOnlyList<CostEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = _connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO cost_entry (provider, entry_id, ts_utc, model, input_tokens, output_tokens,
                                        cache_read_tokens, cache_write_5m_tokens, cache_write_1h_tokens,
                                        reasoning_tokens)
                VALUES ($provider, $id, $ts, $model, $in, $out, $cread, $c5m, $c1h, $reason)
                ON CONFLICT(provider, entry_id) DO UPDATE SET
                  ts_utc = excluded.ts_utc, model = excluded.model,
                  input_tokens = excluded.input_tokens, output_tokens = excluded.output_tokens,
                  cache_read_tokens = excluded.cache_read_tokens,
                  cache_write_5m_tokens = excluded.cache_write_5m_tokens,
                  cache_write_1h_tokens = excluded.cache_write_1h_tokens,
                  reasoning_tokens = excluded.reasoning_tokens;
                """;

            var provider = command.Parameters.Add("$provider", SqliteType.Text);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var ts = command.Parameters.Add("$ts", SqliteType.Integer);
            var model = command.Parameters.Add("$model", SqliteType.Text);
            var input = command.Parameters.Add("$in", SqliteType.Integer);
            var output = command.Parameters.Add("$out", SqliteType.Integer);
            var cacheRead = command.Parameters.Add("$cread", SqliteType.Integer);
            var cache5m = command.Parameters.Add("$c5m", SqliteType.Integer);
            var cache1h = command.Parameters.Add("$c1h", SqliteType.Integer);
            var reasoning = command.Parameters.Add("$reason", SqliteType.Integer);

            foreach (var entry in entries)
            {
                provider.Value = entry.Provider.ToString();
                id.Value = entry.EntryId;
                ts.Value = entry.Timestamp.ToUnixTimeSeconds();
                model.Value = entry.Model;
                input.Value = entry.InputTokens;
                output.Value = entry.OutputTokens;
                cacheRead.Value = entry.CacheReadTokens;
                cache5m.Value = entry.CacheWrite5mTokens;
                cache1h.Value = entry.CacheWrite1hTokens;
                reasoning.Value = entry.ReasoningTokens;

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CostTotals> ReadCostTotalsAsync(
        DateTimeOffset since, ProviderId? provider = null, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(cache_read_tokens), 0),
                   COALESCE(SUM(cache_write_5m_tokens + cache_write_1h_tokens), 0)
            FROM cost_entry
            WHERE ts_utc >= $since AND ($provider IS NULL OR provider = $provider);
            """;

        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$provider", (object?)provider?.ToString() ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return CostTotals.Empty;

        return new CostTotals(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>Per-day, per-model token totals.</summary>
    public async Task<IReadOnlyList<DailyCost>> ReadDailyCostAsync(
        DateTimeOffset since, ProviderId? provider = null, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT date(ts_utc, 'unixepoch') AS day, model,
                   SUM(input_tokens + output_tokens + cache_read_tokens
                       + cache_write_5m_tokens + cache_write_1h_tokens)
            FROM cost_entry
            WHERE ts_utc >= $since AND ($provider IS NULL OR provider = $provider)
            GROUP BY day, model
            ORDER BY day, model;
            """;

        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$provider", (object?)provider?.ToString() ?? DBNull.Value);

        var rows = new List<DailyCost>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DailyCost(
                DateOnly.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(1), reader.GetInt64(2)));
        }

        return rows;
    }

    // ---- scan cursors ----------------------------------------------------------------------------

    public async Task<(long Size, DateTimeOffset Mtime, long Offset)?> ReadScanCursorAsync(
        string path, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT size_bytes, mtime_utc, byte_offset FROM scan_cursor WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return (reader.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), reader.GetInt64(2));
    }

    public async Task WriteScanCursorAsync(
        string path, long size, DateTimeOffset mtime, long offset, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO scan_cursor (path, size_bytes, mtime_utc, byte_offset)
                VALUES ($path, $size, $mtime, $offset)
                ON CONFLICT(path) DO UPDATE SET
                  size_bytes = excluded.size_bytes, mtime_utc = excluded.mtime_utc,
                  byte_offset = excluded.byte_offset;
                """;

            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$mtime", mtime.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$offset", offset);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- retention -------------------------------------------------------------------------------

    /// <summary>Trims both tables to the retention window. Returns rows removed.</summary>
    public async Task<int> PruneAsync(TimeSpan retention, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = (now - retention).ToUnixTimeSeconds();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = 0;

            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM usage_sample WHERE ts_utc < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                removed += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM cost_entry WHERE ts_utc < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                removed += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}

using Cadence.Core.History;
using Cadence.Core.Model;
using Microsoft.Extensions.Logging;

namespace Cadence.Core.Cost;

/// <summary>What one scan pass did, for the diagnostics pane.</summary>
public sealed record ScanReport(
    int FilesSeen,
    int FilesRead,
    int EntriesFound,
    long BytesRead,
    IReadOnlyList<string> UnpricedModels)
{
    public static readonly ScanReport Empty = new(0, 0, 0, 0, []);
}

/// <summary>
/// Walks the provider session transcripts and folds them into the cost ledger.
/// </summary>
/// <remarks>
/// Incremental by design: transcripts reach tens of megabytes and a tray app cannot reparse them
/// every minute. A per-file cursor of (size, mtime, byte offset) means a growing file is read only
/// from where the last pass stopped, and a file whose size shrank is treated as rewritten and read
/// from the start.
/// </remarks>
public sealed class CostScanner(
    HistoryRepository history,
    PricingTable pricing,
    ILogger<CostScanner> logger,
    IReadOnlyList<IJsonlScanner>? scanners = null)
{
    private readonly IReadOnlyList<IJsonlScanner> _scanners =
        scanners ?? [new ClaudeJsonlScanner(), new CodexJsonlScanner()];

    /// <summary>Entries buffered before a database round-trip.</summary>
    private const int BatchSize = 500;

    public async Task<ScanReport> ScanAsync(
        TimeSpan retention, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now - retention;
        int filesSeen = 0, filesRead = 0, entriesFound = 0;
        long bytesRead = 0;
        var unpriced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scanner in _scanners)
        {
            foreach (var directory in scanner.Directories())
            {
                if (!Directory.Exists(directory)) continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    filesSeen++;

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        if (!info.Exists) continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    // A file untouched since the retention window began has nothing to add.
                    if (info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;

                    var cursor = await history.ReadScanCursorAsync(file, ct).ConfigureAwait(false);
                    var startOffset = 0L;

                    if (cursor is { } saved)
                    {
                        // Unchanged since last time.
                        if (saved.Size == info.Length &&
                            saved.Mtime.ToUnixTimeSeconds() == new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())
                        {
                            continue;
                        }

                        // Grown: resume. Shrunk: it was rewritten, so start over.
                        startOffset = info.Length >= saved.Size ? saved.Offset : 0L;
                    }

                    var (entries, endOffset, read) =
                        await ReadFileAsync(scanner, file, startOffset, cutoff, ct).ConfigureAwait(false);

                    bytesRead += read;
                    if (read > 0) filesRead++;

                    if (entries.Count > 0)
                    {
                        foreach (var entry in entries)
                        {
                            if (pricing.IsUnpriced(entry.Model)) unpriced.Add(entry.Model);
                        }

                        var priced = entries
                            .Select(e => e with { CostUsd = pricing.CostOf(e) })
                            .ToList();

                        await history.UpsertCostEntriesAsync(priced, ct).ConfigureAwait(false);
                        entriesFound += priced.Count;
                    }

                    await history.WriteScanCursorAsync(
                        file, info.Length, new DateTimeOffset(info.LastWriteTimeUtc), endOffset, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        if (unpriced.Count > 0)
            logger.LogInformation("Cost scan saw {Count} unpriced models: {Models}", unpriced.Count, string.Join(", ", unpriced));

        return new ScanReport(filesSeen, filesRead, entriesFound, bytesRead, [.. unpriced]);
    }

    /// <summary>
    /// Reads one transcript from <paramref name="startOffset"/>, returning entries and the offset
    /// to resume from.
    /// </summary>
    /// <remarks>
    /// The resume offset is advanced only past lines that were read <em>complete</em>. A transcript
    /// being appended to right now ends mid-line, and recording an offset past that partial line
    /// would drop the exchange permanently once the writer finished it.
    /// </remarks>
    private static async Task<(List<CostEntry> Entries, long EndOffset, long BytesRead)> ReadFileAsync(
        IJsonlScanner scanner, string path, long startOffset, DateTimeOffset cutoff, CancellationToken ct)
    {
        var entries = new List<CostEntry>(BatchSize);
        var offset = startOffset;
        long bytesRead = 0;

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

            // A resumed read starts mid-file, so the model context from earlier turn_context lines
            // is gone. Rescanning whole files purely to recover it would defeat the incremental
            // design; entries then fall back to "unknown" until the next turn_context appears.
            scanner.BeginFile();

            // Whether the file's very last line is terminated decides if it can be trusted. A
            // transcript being appended to right now ends mid-object, and consuming that partial
            // line would drop the exchange permanently once the writer completed it.
            var lastLineIsComplete = await EndsWithNewlineAsync(stream, ct).ConfigureAwait(false);
            stream.Seek(startOffset > 0 && startOffset <= stream.Length ? startOffset : 0, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            // One line of lookahead, so the final line can be dropped without blocking on
            // EndOfStream (which performs a synchronous read).
            string? pending = null;
            long pendingBytes = 0;

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (pending is not null)
                {
                    offset += pendingBytes;
                    Consume(pending);
                }

                pending = line;
                // ReadLine strips the terminator; count it so the offset stays aligned.
                pendingBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                bytesRead += pendingBytes;
            }

            if (pending is not null && lastLineIsComplete)
            {
                offset += pendingBytes;
                Consume(pending);
            }

            void Consume(string line)
            {
                var entry = scanner.ParseLine(line);
                if (entry is null) return;
                if (entry.Timestamp < cutoff) return;

                entries.Add(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked or vanished mid-scan. Keep whatever parsed and retry next pass.
        }

        return (entries, offset, bytesRead);
    }

    /// <summary>True when the stream's final byte is a line terminator, i.e. no torn trailing line.</summary>
    private static async Task<bool> EndsWithNewlineAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length == 0) return false;

        stream.Seek(-1, SeekOrigin.End);

        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);

        return read == 1 && buffer[0] is (byte)'\n' or (byte)'\r';
    }
}

using System.Text.Json;
using Cadence.Core.Cost;
using Cadence.Core.Model;

namespace Cadence.Core.Tests;

/// <summary>
/// These encode the field conventions measured against real transcripts on 2026-09-13. They are
/// the tests most likely to catch a provider changing its reporting shape.
/// </summary>
public class CostScannerTests
{
    // ---- Claude ----------------------------------------------------------------------------------

    private static readonly string ClaudeAssistantLine =
        """
        {"type":"assistant","requestId":"req_A","timestamp":"2026-09-13T10:00:00.000Z",
         "message":{"id":"msg_A","model":"claude-opus-5","usage":{
            "input_tokens":2,"cache_creation_input_tokens":8807,"cache_read_input_tokens":28966,
            "output_tokens":211,"output_tokens_details":{"thinking_tokens":46},
            "cache_creation":{"ephemeral_1h_input_tokens":8807,"ephemeral_5m_input_tokens":0},
            "iterations":[{"input_tokens":2,"output_tokens":211,"cache_read_input_tokens":28966,
                           "cache_creation_input_tokens":8807}]}}}
        """.ReplaceLineEndings(string.Empty);

    [Fact]
    public void Claude_FieldsAreTreatedAsAdditive()
    {
        // input_tokens (2) excludes cache reads (28966) and cache creation (8807). Each is stored
        // as its own exclusive bucket, so summing them gives the true total.
        var entry = new ClaudeJsonlScanner().ParseLine(ClaudeAssistantLine);

        Assert.NotNull(entry);
        Assert.Equal(2, entry!.InputTokens);
        Assert.Equal(211, entry.OutputTokens);
        Assert.Equal(28966, entry.CacheReadTokens);
        Assert.Equal(8807, entry.CacheWrite1hTokens);
        Assert.Equal(0, entry.CacheWrite5mTokens);
        Assert.Equal(2 + 211 + 28966 + 8807, entry.TotalTokens);
    }

    [Fact]
    public void Claude_CacheCreationIsSplitByTtlBecauseThePricesDiffer()
    {
        var entry = new ClaudeJsonlScanner().ParseLine(ClaudeAssistantLine)!;

        Assert.Equal(8807, entry.CacheWrite1hTokens);
        Assert.Equal(0, entry.CacheWrite5mTokens);
    }

    [Fact]
    public void Claude_IterationsArrayIsNotAddedOnTopOfTheTopLevelFigures()
    {
        // `iterations` restates the same numbers. Adding them would double every entry.
        var entry = new ClaudeJsonlScanner().ParseLine(ClaudeAssistantLine)!;

        Assert.Equal(211, entry.OutputTokens);
    }

    [Fact]
    public void Claude_ThinkingTokensAreReportedButNotAddedToOutput()
    {
        // thinking_tokens is a subset of output_tokens and already billed as output.
        var entry = new ClaudeJsonlScanner().ParseLine(ClaudeAssistantLine)!;

        Assert.Equal(46, entry.ReasoningTokens);
        Assert.Equal(211, entry.OutputTokens);
    }

    [Fact]
    public void Claude_EntryIdCombinesMessageAndRequestIds()
    {
        // The dedup key. A real transcript had 1156 usage lines and 553 distinct pairs.
        var entry = new ClaudeJsonlScanner().ParseLine(ClaudeAssistantLine)!;

        Assert.Equal("msg_A|req_A", entry.EntryId);
    }

    [Fact]
    public void Claude_OlderTranscriptsWithOnlyATotalAttributeItToTheCheaperBucket()
    {
        const string Line =
            """{"type":"assistant","requestId":"r","timestamp":"2026-09-13T10:00:00Z","message":{"id":"m","model":"claude-sonnet-5","usage":{"input_tokens":10,"cache_creation_input_tokens":500,"output_tokens":20}}}""";

        var entry = new ClaudeJsonlScanner().ParseLine(Line)!;

        Assert.Equal(500, entry.CacheWrite5mTokens);
        Assert.Equal(0, entry.CacheWrite1hTokens);
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"content":"hi"}}""")]
    [InlineData("""{"type":"assistant","message":{"id":"m"}}""")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"assistant","message":{"id":"m","usage":{"inp""")]
    public void Claude_LinesWithoutUsageOrTornLinesAreSkipped(string line)
        => Assert.Null(new ClaudeJsonlScanner().ParseLine(line));

    // ---- Codex -----------------------------------------------------------------------------------

    private static readonly string CodexUsageLine =
        """
        {"type":"token_usage_record","timestamp":"2026-09-12T02:57:40.536Z","payload":{
          "response_id":"resp_A","turn_id":"turn_A",
          "usage":{"input_tokens":25886,"cached_input_tokens":18944,"cache_write_input_tokens":0,
                   "output_tokens":194,"reasoning_output_tokens":0,"total_tokens":26080},
          "turn_token_usage":{"input_tokens":250000,"cached_input_tokens":180000,"output_tokens":9000,"total_tokens":259000},
          "thread_token_usage":{"input_tokens":900000,"cached_input_tokens":700000,"output_tokens":40000,"total_tokens":940000}}}
        """.ReplaceLineEndings(string.Empty);

    [Fact]
    public void Codex_FieldsAreTreatedAsInclusiveSoCachedInputIsSubtracted()
    {
        // input_tokens (25886) *contains* cached_input_tokens (18944). Fresh input is the
        // difference. Getting this backwards roughly triples a cache-heavy session's cost.
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();

        var entry = scanner.ParseLine(CodexUsageLine);

        Assert.NotNull(entry);
        Assert.Equal(25886 - 18944, entry!.InputTokens);
        Assert.Equal(18944, entry.CacheReadTokens);
        Assert.Equal(194, entry.OutputTokens);

        // Reconciles with the provider's own total.
        Assert.Equal(26080, entry.InputTokens + entry.CacheReadTokens + entry.OutputTokens);
    }

    [Fact]
    public void Codex_CumulativeBlocksAreIgnored()
    {
        // turn_token_usage and thread_token_usage are running totals. Summing either grows
        // quadratically with conversation length.
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();

        var entry = scanner.ParseLine(CodexUsageLine)!;

        Assert.True(entry.TotalTokens < 30_000,
            $"picked up a cumulative block: {entry.TotalTokens} tokens for one response");
    }

    [Fact]
    public void Codex_ModelComesFromTheMostRecentTurnContext()
    {
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();

        Assert.Null(scanner.ParseLine("""{"type":"turn_context","payload":{"model":"gpt-6-astra"}}"""));
        Assert.Equal("gpt-6-astra", scanner.ParseLine(CodexUsageLine)!.Model);
    }

    [Fact]
    public void Codex_ModelIsUnknownBeforeAnyTurnContextIsSeen()
    {
        // A resumed incremental read starts mid-file, past the turn_context that named the model.
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();

        Assert.Equal("unknown", scanner.ParseLine(CodexUsageLine)!.Model);
    }

    [Fact]
    public void Codex_BeginFileClearsModelStateBetweenFiles()
    {
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();
        scanner.ParseLine("""{"type":"turn_context","payload":{"model":"gpt-6-astra"}}""");

        scanner.BeginFile();

        Assert.Equal("unknown", scanner.ParseLine(CodexUsageLine)!.Model);
    }

    [Fact]
    public void Codex_ReasoningTokensAreReportedButNotAddedToOutput()
    {
        var line = CodexUsageLine.Replace("\"reasoning_output_tokens\":0", "\"reasoning_output_tokens\":150", StringComparison.Ordinal);
        var scanner = new CodexJsonlScanner();
        scanner.BeginFile();

        var entry = scanner.ParseLine(line)!;

        Assert.Equal(150, entry.ReasoningTokens);
        Assert.Equal(194, entry.OutputTokens);
    }

    [Fact]
    public void Codex_EntryIdIsTheResponseId()
        => Assert.Equal("resp_A", new CodexJsonlScanner().ParseLine(CodexUsageLine)!.EntryId);
}

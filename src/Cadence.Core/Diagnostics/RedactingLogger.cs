using Microsoft.Extensions.Logging;

namespace Cadence.Core.Diagnostics;

/// <summary>
/// Wraps a logger factory so every message is scrubbed before it reaches any sink.
/// </summary>
/// <remarks>
/// Redacting at each call site would work right up until someone forgot, and the thing they forgot
/// would be a live OAuth token written to a file on disk. Putting it in the pipeline makes the safe
/// behaviour the automatic one. Provider error bodies are the realistic risk: an API can echo a
/// token back inside a message Cadence never composed.
/// </remarks>
public sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception,
                (s, e) => Redaction.Scrub(formatter(s, e)));
        }
    }
}

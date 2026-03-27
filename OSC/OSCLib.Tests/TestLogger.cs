using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OSCLib.Tests;

internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public int Count(LogLevel level, string contains)
        => _entries.Count(e => e.Level == level && e.Message.Contains(contains, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    internal readonly record struct LogEntry(LogLevel Level, string Message, Exception? Exception);
}

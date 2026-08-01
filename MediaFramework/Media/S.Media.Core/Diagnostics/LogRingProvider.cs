using Microsoft.Extensions.Logging;

namespace S.Media.Core.Diagnostics;

/// <summary>One captured log record, kept structured rather than pre-formatted.</summary>
/// <param name="Timestamp">When it was written (UTC).</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Logger category, e.g. <c>S.Media.Routing.AudioRouter</c>.</param>
/// <param name="Message">The formatted message text, without level/category/time decoration.</param>
/// <param name="EventId">The event id, when the caller supplied one.</param>
/// <param name="Exception">The exception, when one was logged.</param>
/// <remarks>
/// The fields stay separate on purpose. A diagnostics view renders time, level and category as their own
/// columns and filters on them, none of which is possible once they have been flattened into one string -
/// which is exactly why this does not reuse the file logger's line-formatting model.
/// </remarks>
public readonly record struct LogRingEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    EventId EventId,
    Exception? Exception);

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps the most recent records in a fixed-size ring, so a UI can
/// tail the log without a second logging system and without touching the file sink.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>reader</b>, not an archive: the ring holds a bounded window and the file log remains the
/// durable record. That division is deliberate - a diagnostics window wants the last few hundred lines
/// instantly, and anything older is a question for the file.
/// </para>
/// <para>
/// <b>Runtime level switching.</b> <see cref="MinimumLevel"/> is a volatile field read per record rather
/// than an options object, because the host's file provider bakes its level into each category logger at
/// creation and has no <c>IOptionsMonitor</c> to re-read - so a picker in the UI could not move it. Here
/// the level is sink-side, and since hosts typically configure the factory at <see cref="LogLevel.Trace"/>
/// the provider sees essentially everything and can widen or narrow without reconfiguring anything.
/// </para>
/// <para>Safe to write from any thread, including pump and audio threads: a write is a short lock around
/// an array slot assignment, with no allocation beyond the message string the logging pipeline already
/// produced.</para>
/// </remarks>
public sealed class LogRingProvider : ILoggerProvider
{
    /// <summary>Records retained when no capacity is given.</summary>
    public const int DefaultCapacity = 500;

    private readonly LogRingEntry[] _entries;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;
    private long _dropped;
    private volatile LogLevel _minimumLevel;

    public LogRingProvider(int capacity = DefaultCapacity, LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new LogRingEntry[capacity];
        _minimumLevel = minimumLevel;
    }

    /// <summary>Records at or above this level are kept. Changing it takes effect on the next record.</summary>
    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    /// <summary>Records the ring has overwritten since it was created - the "you are missing some" count
    /// a tail should surface rather than silently showing a gap.</summary>
    public long DroppedCount
    {
        get { lock (_gate) return _dropped; }
    }

    /// <summary>Raised after a record is captured, so a view can refresh without polling. Runs on the
    /// logging caller's thread - marshal before touching UI.</summary>
    public event Action<LogRingEntry>? EntryCaptured;

    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);

    /// <summary>The retained records, oldest first.</summary>
    public IReadOnlyList<LogRingEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogRingEntry[_count];
            // _next is the write cursor; once wrapped, the oldest record sits there.
            var start = _count == _entries.Length ? _next : 0;
            for (var i = 0; i < _count; i++)
                result[i] = _entries[(start + i) % _entries.Length];
            return result;
        }
    }

    /// <summary>Empties the ring (and its dropped count).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
            _dropped = 0;
        }
    }

    public void Dispose() => EntryCaptured = null;

    private void Capture(in LogRingEntry entry)
    {
        lock (_gate)
        {
            if (_count == _entries.Length)
                _dropped++;
            else
                _count++;
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
        }

        EntryCaptured?.Invoke(entry);
    }

    private sealed class RingLogger(LogRingProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            ArgumentNullException.ThrowIfNull(formatter);

            owner.Capture(new LogRingEntry(
                DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), eventId, exception));
        }
    }
}

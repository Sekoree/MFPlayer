using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using HaCue2.Machine;
using Microsoft.Extensions.Logging;

namespace HaCue2.Session;

/// <summary>A bounded, single-writer file sink that drains before application shutdown.</summary>
internal sealed class FileLogProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(8_192)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly StreamWriter? _writer;
    private readonly Task _pump;
    private int _disposed;

    public FileLogProvider(AppSettings settings)
    {
        MinimumLevel = Enum.TryParse<LogLevel>(settings.FileLogLevel, true, out var level)
            ? level : LogLevel.Information;

        try
        {
            var directory = settings.LogDirectory.Length > 0 ? settings.LogDirectory : StoragePaths.LogRoot;
            Directory.CreateDirectory(directory);
            Prune(directory, Days(settings.LogRetention));
            var path = Path.Combine(
                directory,
                $"hacue2-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _writer = new StreamWriter(new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true));
            _pump = Task.Run(PumpAsync);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"HaCue2 could not open its file log: {failure.Message}");
            _pump = Task.CompletedTask;
        }
    }

    internal LogLevel MinimumLevel { get; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, category => new FileLogger(this, category));

    internal void Write(string line)
    {
        if (Volatile.Read(ref _disposed) == 0 && _writer is not null)
            _lines.Writer.TryWrite(line);
    }

    private async Task PumpAsync()
    {
        if (_writer is null)
            return;

        await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            await _writer.WriteLineAsync(line).ConfigureAwait(false);

        await _writer.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lines.Writer.TryComplete();
        try
        {
            _pump.GetAwaiter().GetResult();
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
        }
        _writer?.Dispose();
    }

    private static int Days(string text)
    {
        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? Math.Clamp(days, 1, 3_650) : 14;
    }

    private static void Prune(string directory, int days)
    {
        var before = DateTime.UtcNow.AddDays(-days);
        foreach (var path in Directory.EnumerateFiles(directory, "hacue2-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < before)
                    File.Delete(path);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception).ReplaceLineEndings(" ↵ ");
            var suffix = exception is null ? "" : $" · {exception}".ReplaceLineEndings(" ↵ ");
            owner.Write($"{DateTimeOffset.Now:O} {Short(logLevel)} {category} [{eventId.Id}] {message}{suffix}");
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---",
        };
    }
}


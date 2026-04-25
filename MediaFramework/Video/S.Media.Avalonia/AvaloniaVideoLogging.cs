using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.Avalonia;

/// <summary>
/// Logging configuration for the S.Media.Avalonia library.
/// Call <see cref="Configure"/> once at application startup to enable logging.
/// </summary>
public static class AvaloniaVideoLogging
{
    private static readonly Lock Gate = new();
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    internal static ILogger GetLogger(string category) => _factory.CreateLogger(category);
    internal static ILogger<T> GetLogger<T>() => _factory.CreateLogger<T>();
}


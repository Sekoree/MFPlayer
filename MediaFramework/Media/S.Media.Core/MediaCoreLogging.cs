using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.Core;

/// <summary>
/// Logging configuration for the S.Media.Core library.
/// Call <see cref="Configure"/> once at application startup to enable logging.
/// </summary>
public static class MediaCoreLogging
{
    private static readonly Lock Gate = new();

    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Configures the logger factory for the entire S.Media.Core library.
    /// Pass <see langword="null"/> to reset to the no-op logger.
    /// </summary>
    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    internal static ILogger GetLogger(string category) => _factory.CreateLogger(category);
    internal static ILogger<T> GetLogger<T>() => _factory.CreateLogger<T>();
}


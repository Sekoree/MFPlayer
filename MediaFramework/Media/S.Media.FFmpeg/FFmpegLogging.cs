using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.FFmpeg;

/// <summary>
/// Logging configuration for the S.Media.FFmpeg library.
/// Call <see cref="Configure"/> once at application startup to enable logging.
/// </summary>
public static class FFmpegLogging
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


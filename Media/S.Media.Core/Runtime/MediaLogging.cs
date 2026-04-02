using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.Core.Runtime;

/// <summary>
/// Unified logging configuration for the MFPlayer framework.
/// <para>
/// Call <see cref="Configure"/> once at application startup to inject an
/// <see cref="ILoggerFactory"/> that is shared across all <c>S.Media.*</c> modules.
/// Each engine's <c>Initialize()</c> method propagates this factory to its
/// underlying native library (<c>PALibLogging</c>, <c>PMLibLogging</c>,
/// <c>NDILibLogging</c>), so a single call is sufficient to enable logging
/// across the entire stack.
/// </para>
/// <example>
/// <code>
/// using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
/// MediaLogging.Configure(loggerFactory);
///
/// // All subsequent engine.Initialize() calls will inherit this factory.
/// var audioEngine = new PortAudioEngine();
/// audioEngine.Initialize(config);
/// </code>
/// </example>
/// </summary>
public static class MediaLogging
{
    private static readonly Lock Gate = new();

    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Configures the shared <see cref="ILoggerFactory"/> used by all S.Media.* modules.
    /// Safe to call multiple times — the factory is replaced on every call.
    /// Passing <see langword="null"/> resets to <see cref="NullLoggerFactory.Instance"/>.
    /// </summary>
    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Returns the currently configured <see cref="ILoggerFactory"/>.
    /// Defaults to <see cref="NullLoggerFactory.Instance"/> if <see cref="Configure"/>
    /// has not been called.
    /// </summary>
    public static ILoggerFactory Factory
    {
        get
        {
            lock (Gate)
                return _factory;
        }
    }

    /// <summary>
    /// Creates a logger with the specified category name using the shared factory.
    /// </summary>
    public static ILogger GetLogger(string category) => Factory.CreateLogger(category);

    /// <summary>
    /// Creates a logger for type <typeparamref name="T"/> using the shared factory.
    /// </summary>
    public static ILogger GetLogger<T>() => Factory.CreateLogger<T>();
}


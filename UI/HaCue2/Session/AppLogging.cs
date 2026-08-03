using HaCue2.Machine;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaCue2.Session;

/// <summary>
/// The application's one logging pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Register item 27: the Diagnostics event panel is a level-filtered live tail of the
/// <c>Microsoft.Extensions.Logging</c> pipeline — <b>the same sink the file log uses</b>. One logging
/// system, two readers. A second event-collection system beside it would drift from the archive the
/// moment either changed, and the screen an operator reads during a fault would stop matching the file
/// they send afterwards.
/// </para>
/// <para>
/// Installed into <see cref="MediaDiagnostics.LoggerFactory"/> at startup, which is what makes the
/// FRAMEWORK's own logs appear: the session, the router and the patch bay all resolve their loggers
/// from there. Without that the tail would only ever show what the app itself wrote, which is the
/// least interesting half — a wedged output pump reports itself from inside the routing layer.
/// </para>
/// </remarks>
public sealed class AppLogging : IDisposable
{
    private AppLogging(ILoggerFactory factory, LogRingProvider ring)
    {
        Factory = factory;
        Ring = ring;
    }

    /// <summary>The in-memory tail the Diagnostics window reads.</summary>
    public LogRingProvider Ring { get; }

    public ILoggerFactory Factory { get; }

    /// <summary>The one installed pipeline, or null before <see cref="Install"/> has run.</summary>
    public static AppLogging? Current { get; private set; }

    /// <summary>
    /// Builds the pipeline and hands it to the framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once, before anything that logs. The ring's own minimum level is the FLOOR — it captures
    /// at Debug so the Diagnostics filter can be turned down after something has gone wrong, rather
    /// than only forward from the moment the operator changed it. A fault that is only reproducible
    /// once is the one this matters for.
    /// </para>
    /// <para>
    /// The builder's level is separately set to Debug for the same reason: a category filtered out
    /// upstream never reaches the ring at all, and no amount of turning the panel's filter down would
    /// bring it back.
    /// </para>
    /// </remarks>
    public static AppLogging Install(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Current is { } already)
            return already;

        var ring = new LogRingProvider(minimumLevel: LogLevel.Debug);

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(ring);
        });

        MediaDiagnostics.LoggerFactory = factory;

        var logging = new AppLogging(factory, ring);
        Current = logging;

        factory.CreateLogger("HaCue2").LogInformation(
            "HaCue2 started · log level {Level}", settings.FileLogLevel);

        return logging;
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this))
            Current = null;

        MediaDiagnostics.LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Factory.Dispose();
        Ring.Dispose();
    }
}

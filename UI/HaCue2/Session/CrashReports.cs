using HaCue2.Machine;
using Microsoft.Extensions.Logging;

namespace HaCue2.Session;

/// <summary>Writes a last-resort managed crash report beside the normal logs.</summary>
internal sealed class CrashReports : IDisposable
{
    private readonly string _directory;
    private readonly ILogger _log;
    private bool _disposed;

    private CrashReports(AppSettings settings, ILoggerFactory factory)
    {
        _directory = settings.LogDirectory.Length > 0 ? settings.LogDirectory : StoragePaths.LogRoot;
        _log = factory.CreateLogger("HaCue2.Crash");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    public static IDisposable? Install(AppSettings settings, ILoggerFactory factory) =>
        settings.CrashDumps ? new CrashReports(settings, factory) : null;

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) =>
        Write("unhandled", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Write("unobserved-task", args.Exception);

    private void Write(string kind, Exception failure)
    {
        _log.LogCritical(failure, "{Kind} failure reached the application boundary", kind);
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(
                Path.Combine(_directory, $"hacue2-crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt"),
                $"HaCue2 {kind} failure{Environment.NewLine}{DateTimeOffset.Now:O}{Environment.NewLine}{failure}");
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }
}


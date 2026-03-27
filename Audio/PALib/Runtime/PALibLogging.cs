using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PALib.Runtime;

public static class PALibLogging
{
    private static readonly Lock Gate = new();

    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static ILogger GetLogger(string category) => _factory.CreateLogger(category);

    public static void TraceCall(ILogger logger, string method, params (string Name, object? Value)[] args)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
            return;

        var formatted = string.Join(", ", args.Select(a => $"{a.Name}={a.Value}"));
        logger.LogTrace("{Method}({Args})", method, formatted);
    }

    public static string PtrMeta(nint ptr) => ptr == nint.Zero ? "0x0" : $"0x{ptr:X}";

    public static string BufferMeta(nint ptr, long elementCount, int elementSize, string direction)
        => $"{direction} ptr={PtrMeta(ptr)} elementCount={elementCount} elementSize={elementSize}";
}

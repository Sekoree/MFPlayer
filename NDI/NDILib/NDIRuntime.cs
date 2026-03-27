using System.Runtime.InteropServices;

namespace NDILib;

public static class NDIRuntime
{
    public static bool Initialize() => Native.NDIlib_initialize();

    public static void Destroy() => Native.NDIlib_destroy();

    public static string Version => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;

    public static bool IsSupportedCpu() => Native.NDIlib_is_supported_CPU();
}

public sealed class NDIRuntimeScope : IDisposable
{
    private bool _disposed;

    public NDIRuntimeScope()
    {
        if (!NDIRuntime.Initialize())
            throw new InvalidOperationException("NDI initialization failed (unsupported CPU or runtime not installed).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        NDIRuntime.Destroy();
        _disposed = true;
    }
}

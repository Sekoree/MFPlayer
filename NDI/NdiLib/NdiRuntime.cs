using System.Runtime.InteropServices;

namespace NdiLib;

public static class NdiRuntime
{
    public static bool Initialize() => Native.NDIlib_initialize();

    public static void Destroy() => Native.NDIlib_destroy();

    public static string Version => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;

    public static bool IsSupportedCpu() => Native.NDIlib_is_supported_CPU();
}

public sealed class NdiRuntimeScope : IDisposable
{
    private bool _disposed;

    public NdiRuntimeScope()
    {
        if (!NdiRuntime.Initialize())
            throw new InvalidOperationException("NDI initialization failed (unsupported CPU or runtime not installed).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        NdiRuntime.Destroy();
        _disposed = true;
    }
}


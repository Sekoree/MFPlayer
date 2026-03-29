using System.Runtime.InteropServices;

namespace NDILib;

/// <summary>
/// Manages the NDI runtime lifetime. Must be created before any other NDI object and disposed last.
/// </summary>
/// <remarks>
/// Static query methods (<see cref="Version"/>, <see cref="IsSupportedCpu"/>) are safe to call
/// without creating an instance.
/// </remarks>
public sealed class NDIRuntime : IDisposable
{
    private bool _disposed;

    private NDIRuntime() { }

    // ------------------------------------------------------------------
    // Static queries — safe without an active instance
    // ------------------------------------------------------------------

    /// <summary>The NDI SDK version string (e.g. "6.x.x.xxxxx").</summary>
    public static string Version
        => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;

    /// <summary>Returns <see langword="true"/> if the current CPU supports NDI (requires SSE4.2).</summary>
    public static bool IsSupportedCpu()
        => Native.NDIlib_is_supported_CPU();

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Initialises the NDI runtime and returns a lifetime scope.
    /// Dispose the returned instance to shut the runtime down.
    /// </summary>
    /// <param name="runtime">
    /// On success, the initialised runtime scope. <see langword="null"/> on failure.
    /// </param>
    /// <returns>
    /// <c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIRuntimeInitFailed"/></c>
    /// if the NDI runtime is not installed or the CPU does not meet requirements.
    /// </returns>
    public static int Create(out NDIRuntime? runtime)
    {
        runtime = null;
        if (!Native.NDIlib_initialize())
            return (int)NDIErrorCode.NDIRuntimeInitFailed;

        runtime = new NDIRuntime();
        return 0;
    }

    // ------------------------------------------------------------------
    // Lifetime
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        Native.NDIlib_destroy();
        _disposed = true;
    }
}

using NDILib;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS (rather than fails) when this machine has no NDI runtime.
/// </summary>
/// <remarks>
/// <para>
/// The NDI runtime is proprietary and separately installed, so a CI runner does not have one — both
/// legs failed these with an empty <c>Open</c> list, which is the engine doing exactly what it promises
/// (an NDI runtime this machine lacks is reported, never thrown). A test that asserts a sender OPENED
/// is asserting about the runtime as much as about the code, and cannot be answered without one.
/// </para>
/// <para>
/// This probes rather than taking the framework's env-var opt-in (<c>MFP_RUN_NDI_TESTS=1</c>): those
/// tests need a live source on the NETWORK, which no probe can conjure, while these need only a
/// loadable library — so on any box that has NDI installed they keep running by themselves, which is
/// where their value is.
/// </para>
/// </remarks>
public sealed class NdiRuntimeFactAttribute : FactAttribute
{
    private static readonly string? UnavailableReason = Probe();

    private static string? Probe()
    {
        try
        {
            // The runtime itself, NOT a sender: creating a sender to find out would announce a source on
            // the network — briefly, but on every discovery pass, on every machine that HAS NDI. Loading
            // the library and initialising it is the whole question, and NDIlib_version answers the first
            // half by throwing when there is nothing to load.
            if (NDIRuntime.Version.Length == 0)
                return "no NDI runtime on this machine: the library reported no version";

            if (NDIRuntime.Create(out var runtime) != 0 || runtime is null)
                return "no NDI runtime on this machine: initialisation failed";

            runtime.Dispose();
            return null;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return $"no NDI runtime on this machine: {failure.GetType().Name}: {failure.Message}";
        }
    }

    public NdiRuntimeFactAttribute()
    {
        if (UnavailableReason is not null)
            Skip = UnavailableReason;
    }
}

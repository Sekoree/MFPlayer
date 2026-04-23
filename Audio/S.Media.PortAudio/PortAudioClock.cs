using S.Media.Core;
using PALib;

namespace S.Media.PortAudio;

/// <summary>
/// <see cref="HardwareClock"/> backed by <c>Pa_GetStreamTime</c>.
/// A <see cref="HandleRef"/> box is captured by the provider lambda so the stream handle
/// can be injected after construction without replacing the delegate.
/// </summary>
public sealed class PortAudioClock : HardwareClock
{
    // Mutable box — captured by the provider lambda so we can set the handle after Open().
    // §3.25 / P2 — `Value` is read on every `Position` evaluation (potentially from any
    // thread observing the clock) and written by `SetStreamHandle`. `nint` is
    // pointer-sized and aligned, so reads are torn-free on 64-bit runtimes, but we
    // publish the write via `Interlocked.Exchange` to add an explicit release/acquire
    // fence so the delegate capture sees the new handle before the next tick fires.
    private sealed class HandleRef { public nint Value; }

    private readonly HandleRef _ref;

    private PortAudioClock(HandleRef box, double sampleRate)
        : base(() =>
               {
                   // Volatile read of a pointer-sized field — torn-free on every supported
                   // platform (the JIT emits an aligned mov).
                   nint h = Volatile.Read(ref box.Value);
                   return h != nint.Zero ? Native.Pa_GetStreamTime(h) : 0.0;
               },
               sampleRate)
    {
        _ref = box;
    }

    /// <summary>Creates a clock for the given sample rate (stream handle not yet known).</summary>
    public static PortAudioClock Create(double sampleRate)
    {
        var box = new HandleRef();
        return new PortAudioClock(box, sampleRate);
    }

    /// <summary>
    /// Called by <see cref="PortAudioEndpoint"/> once the PA stream is open.
    /// §3.25 / P2 — the handle update is published with a release barrier via
    /// <see cref="Interlocked.Exchange(ref nint,nint)"/>, ensuring the provider lambda
    /// sees the new handle before we recompute the tick interval.
    /// </summary>
    internal void SetStreamHandle(nint handle, int framesPerBuffer)
    {
        Interlocked.Exchange(ref _ref.Value, handle);
        UpdateTickInterval(framesPerBuffer);
    }

    /// <summary>
    /// Detaches the current stream handle so the clock stops consulting PortAudio
    /// and transitions into its <see cref="HardwareClock"/> fallback path. Called
    /// from <see cref="PortAudioEndpoint.Dispose"/> before the stream is closed.
    /// </summary>
    internal void ClearStreamHandle()
    {
        Interlocked.Exchange(ref _ref.Value, nint.Zero);
    }
}

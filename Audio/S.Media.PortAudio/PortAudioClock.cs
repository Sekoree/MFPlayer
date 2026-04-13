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
    private sealed class HandleRef { public nint Value; }

    private readonly HandleRef _ref;

    private PortAudioClock(HandleRef box, double sampleRate)
        : base(() => box.Value != nint.Zero ? Native.Pa_GetStreamTime(box.Value) : 0.0,
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

    /// <summary>Called by <see cref="PortAudioOutput"/> once the PA stream is open.</summary>
    internal void SetStreamHandle(nint handle, int framesPerBuffer)
    {
        _ref.Value = handle;
        UpdateTickInterval(framesPerBuffer);
    }
}

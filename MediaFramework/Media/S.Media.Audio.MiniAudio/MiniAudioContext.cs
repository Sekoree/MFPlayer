using S.Media.Core.Diagnostics;

namespace S.Media.Audio.MiniAudio;

internal sealed unsafe class MiniAudioContext : IDisposable
{
    private nint _handle;

    private MiniAudioContext(nint handle) => _handle = handle;

    /// <summary>True when this context could only start on JACK - see <see cref="Create"/>.</summary>
    public bool UsesJackBackend { get; private init; }

    public static MiniAudioContext Create()
    {
        MiniAudioException.ThrowIfError(
            MiniAudioNative.ContextCreate(out var handle, out var fallback), "ma_context_init");

        switch (fallback)
        {
            case MiniAudioFallbackBackend.Jack:
                // Not fatal, and deliberately loud. On JACK, miniaudio runs its data callback on the
                // SERVER's graph thread, so a GC pause in this process stalls the graph and the server
                // xruns - every client on the box glitches, and none of this app's own counters will
                // show why. Every other backend runs it on miniaudio's own thread, where a pause costs
                // only this app's audio and the ring absorbs it. Reaching here means no other backend
                // would start, so the honest answer is "this works, and here is the failure mode to
                // expect".
                MediaDiagnostics.LogWarning(
                    "MiniAudio: no backend but JACK could be initialised on this machine. JACK runs the " +
                    "audio callback on the server's graph thread, where a garbage collection in this " +
                    "process can cause a server-wide xrun (heard as dropouts in EVERY JACK client, not " +
                    "just this one). Prefer the PortAudio backend on this box - it uses blocking writes " +
                    "and has no such hazard.");
                break;

            case MiniAudioFallbackBackend.Null:
                // Distinct from the JACK case on purpose: this box is playing NOTHING. Devices still
                // enumerate and playback appears to run, so without this line the only symptom is
                // silence.
                MediaDiagnostics.LogWarning(
                    "MiniAudio: no working audio backend on this machine - running on miniaudio's " +
                    "silent null device. Playback will appear to run but nothing is audible.");
                break;
        }

        return new MiniAudioContext(handle) { UsesJackBackend = fallback == MiniAudioFallbackBackend.Jack };
    }

    public IReadOnlyList<AudioDeviceInfo> Enumerate(MiniAudioDeviceType deviceType)
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);

        MiniAudioException.ThrowIfError(
            MiniAudioNative.ContextDeviceCount(_handle, (int)deviceType, out var count),
            "ma_context_get_devices(count)");

        var devices = new AudioDeviceInfo[count];
        var idCapacity = Math.Max(1, MiniAudioNative.DeviceIdHexCapacity());
        for (var i = 0; i < devices.Length; i++)
        {
            var idBuffer = new byte[idCapacity];
            var nameBuffer = new byte[512];
            uint isDefault;
            uint maxChannels;
            uint defaultSampleRate;

            fixed (byte* idPtr = idBuffer)
            fixed (byte* namePtr = nameBuffer)
            {
                MiniAudioException.ThrowIfError(
                    MiniAudioNative.ContextDeviceGet(
                        _handle,
                        (int)deviceType,
                        (uint)i,
                        idPtr,
                        idBuffer.Length,
                        namePtr,
                        nameBuffer.Length,
                        out isDefault,
                        out maxChannels,
                        out defaultSampleRate),
                    "ma_context_get_devices(get)");
            }

            devices[i] = new AudioDeviceInfo(
                MiniAudioNative.FromUtf8NullTerminated(idBuffer),
                MiniAudioNative.FromUtf8NullTerminated(nameBuffer),
                checked((int)Math.Max(1, maxChannels)),
                defaultSampleRate == 0 ? 48000 : defaultSampleRate,
                isDefault != 0);
        }

        return devices;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
            MiniAudioNative.ContextDestroy(handle);
    }
}

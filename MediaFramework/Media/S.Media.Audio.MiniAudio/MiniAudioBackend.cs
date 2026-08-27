
namespace S.Media.Audio.MiniAudio;

/// <summary>
/// Backend-neutral miniaudio adapter: device discovery plus ready-to-use capture/playback devices.
/// Enumeration reuses one cached <c>ma_context</c> per backend instance (building and destroying a
/// fresh context per call was pure waste for direct callers); the cache is thread-safe, rebuilt once
/// per call if a call fails on a stale context (e.g. after an audio-server restart), and released by
/// <see cref="Dispose"/>. Registry-registered backends live for the process, so their single context
/// does too - that is the intended footprint.
/// </summary>
public sealed class MiniAudioBackend : IAudioBackend, IDisposable
{
    private readonly Lock _contextGate = new();
    private MiniAudioContext? _context;
    private bool _disposed;

    public string Name => "miniaudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() => Enumerate(MiniAudioDeviceType.Playback);

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => Enumerate(MiniAudioDeviceType.Capture);

    private IReadOnlyList<AudioDeviceInfo> Enumerate(MiniAudioDeviceType deviceType)
    {
        // All native context use stays under the gate: Enumerate and Dispose must never race the
        // ma_context handle (same lifecycle-gate convention as the outputs' device gates).
        lock (_contextGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _context ??= MiniAudioContext.Create();
            try
            {
                return _context.Enumerate(deviceType);
            }
            catch (MiniAudioException)
            {
                // The cached context can go stale (audio server restarted under us). Rebuild once and
                // retry; if that also fails, let the error surface.
                _context.Dispose();
                _context = null;
                _context = MiniAudioContext.Create();
                return _context.Enumerate(deviceType);
            }
        }
    }

    public void Dispose()
    {
        lock (_contextGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _context?.Dispose();
            _context = null;
        }
    }

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var output = new MiniAudioOutput(
            format,
            deviceId,
            FramesPerBuffer(format, opt),
            RingCapacityFrames(opt));
        // Before Start, per the property's contract; clamped so the pacing never waits for ring
        // room that cannot exist.
        if (opt.TargetQueueFrames > 0)
            output.TargetQueueSamples = Math.Min(opt.TargetQueueFrames, RingCapacityFrames(opt));
        return Started(output);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var input = new MiniAudioInput(
            format,
            deviceId,
            FramesPerBuffer(format, opt),
            RingCapacityFrames(opt));
        try
        {
            input.Start();
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static MiniAudioOutput Started(MiniAudioOutput output)
    {
        try
        {
            output.Start();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static int FramesPerBuffer(AudioFormat format, AudioBackendOptions opt)
    {
        if (opt.FramesPerBuffer > 0)
            return opt.FramesPerBuffer;
        if (opt.SuggestedLatencySeconds is { } latency && latency > 0)
            return Math.Max(16, (int)Math.Round(format.SampleRate * latency));
        return 0;
    }

    private static int RingCapacityFrames(AudioBackendOptions opt) =>
        opt.RingCapacityFrames > 0 ? opt.RingCapacityFrames : 16384;
}

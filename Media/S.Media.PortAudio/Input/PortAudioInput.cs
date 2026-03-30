using Microsoft.Extensions.Logging;
using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.PortAudio.Engine;

namespace S.Media.PortAudio.Input;

/// <summary>
/// Live audio capture source backed by PortAudio.
/// Implements <see cref="IAudioInput"/> for use as a mixer source
/// (e.g. microphone passthrough in <c>AVMixer</c>).
/// </summary>
/// <remarks>
/// <b>Initialization dependency:</b> PortAudio's native runtime must be initialized via
/// <c>Pa_Initialize</c> before calling <see cref="Start()"/>. When using the engine API,
/// ensure a <c>PortAudioEngine</c> has been successfully initialized first.
/// Standalone use without an engine will result in a
/// <see cref="MediaErrorCode.PortAudioStreamOpenFailed"/> error from <see cref="Start()"/>.
/// A <c>CreateInput</c> factory on <c>IAudioEngine</c> enforces correct initialization order.
/// <para>
/// <b>Device selection:</b> Call <see cref="SetInputDevice"/>, <see cref="SetInputDeviceByName"/>,
/// or <see cref="SetInputDeviceByIndex"/> to change capture device. Use <c>deviceIndex = -1</c>
/// to fall back to the system default input device.
/// </para>
/// <para>
/// <b>Seeking:</b> Returns <see cref="MediaErrorCode.MediaSourceNonSeekable"/>.
/// <see cref="DurationSeconds"/> is <see cref="double.NaN"/> — this is a live capture source.
/// </para>
/// </remarks>
public sealed unsafe class PortAudioInput : IAudioInput
{
    // Validation bounds (6.4)
    private const int MaxSampleRate = 384_000;
    private const int MaxChannelCount = 64;
    private const int MaxFramesPerBuffer = 32_768;

    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<AudioDeviceInfo>>? _deviceProvider;
    private readonly Func<AudioDeviceInfo?>? _defaultInputProvider;
    private readonly Action<IAudioInput>? _onDisposed;  // engine cleanup callback
    private volatile bool _disposed;           // (10.6) volatile
    private volatile nint _stream;             // (10.6) volatile
    private volatile bool _nativeStreaming;    // (10.6) volatile

    public PortAudioInput()
        : this(deviceProvider: null, defaultInputProvider: null, onDisposed: null) { }

    internal PortAudioInput(
        Func<IReadOnlyList<AudioDeviceInfo>>? deviceProvider,
        Func<AudioDeviceInfo?>? defaultInputProvider,
        Action<IAudioInput>? onDisposed,
        AudioDeviceInfo? initialDevice = null)
    {
        Id = Guid.NewGuid();
        Config = new AudioInputConfig();
        Device = initialDevice
            ?? defaultInputProvider?.Invoke()
            ?? new AudioDeviceInfo(new AudioDeviceId("default-input"), "Default Input",
                HostApi: "fallback", IsDefaultInput: true, IsFallback: true);
        _deviceProvider = deviceProvider;
        _defaultInputProvider = defaultInputProvider;
        _onDisposed = onDisposed;
    }

    public Guid Id { get; }
    public AudioSourceState State { get; private set; } = AudioSourceState.Stopped;
    public float Volume { get; set; } = 1.0f;
    public long? TotalSampleCount => null;
    public AudioInputConfig Config { get; private set; }
    public AudioDeviceInfo Device { get; private set; }
    public AudioStreamInfo StreamInfo => new() { SampleRate = Config.SampleRate, ChannelCount = Config.ChannelCount };
    public double PositionSeconds { get; private set; }
    public double DurationSeconds => double.NaN;
    public event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;

    public int Start() => Start(Config);

    public int Start(AudioInputConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.PortAudioInputStartFailed;
            if (config.SampleRate <= 0 || config.ChannelCount <= 0 || config.FramesPerBuffer <= 0)
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            if (config.SampleRate > MaxSampleRate || config.ChannelCount > MaxChannelCount || config.FramesPerBuffer > MaxFramesPerBuffer)
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            Config = config;
            if (State == AudioSourceState.Running && _nativeStreaming && _stream != nint.Zero)
                return MediaResult.Success;
            CloseNativeStreamIfOpen();
            var startResult = TryStartNativeStream();
            if (startResult != MediaResult.Success) return startResult;
            State = AudioSourceState.Running;
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed) return MediaResult.Success;
            CloseNativeStreamIfOpen();
            State = AudioSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    // ── Device selection (Issue 5.2) ──────────────────────────────────────────

    public int SetInputDevice(AudioDeviceId deviceId)
    {
        if (_deviceProvider is null) return (int)MediaErrorCode.PortAudioDeviceNotFound;
        var devices = _deviceProvider();
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].Id == deviceId) return ApplyDeviceChange(devices[i]);
        return (int)MediaErrorCode.PortAudioDeviceNotFound;
    }

    public int SetInputDeviceByName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return (int)MediaErrorCode.MediaInvalidArgument;
        if (_deviceProvider is null) return (int)MediaErrorCode.PortAudioDeviceNotFound;
        var devices = _deviceProvider();
        for (var i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i].Name, deviceName, StringComparison.OrdinalIgnoreCase))
                return ApplyDeviceChange(devices[i]);
        return (int)MediaErrorCode.PortAudioDeviceNotFound;
    }

    public int SetInputDeviceByIndex(int deviceIndex)
    {
        if (deviceIndex == -1)
        {
            var def = _defaultInputProvider?.Invoke();
            return def.HasValue ? ApplyDeviceChange(def.Value) : (int)MediaErrorCode.PortAudioDeviceNotFound;
        }
        if (_deviceProvider is null) return (int)MediaErrorCode.PortAudioDeviceNotFound;
        var devices = _deviceProvider();
        if (deviceIndex < 0 || deviceIndex >= devices.Count) return (int)MediaErrorCode.PortAudioDeviceNotFound;
        return ApplyDeviceChange(devices[deviceIndex]);
    }

    public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
    {
        framesRead = 0;
        if (requestedFrameCount <= 0) return MediaResult.Success;
        AudioInputConfig config;
        lock (_gate)
        {
            if (_disposed || State != AudioSourceState.Running)
                return (int)MediaErrorCode.PortAudioInputReadFailed;
            config = Config;
        }
        // (10.3) volatile snapshot after lock release
        var stream = _stream;
        if (!_nativeStreaming || stream == nint.Zero) return (int)MediaErrorCode.PortAudioInputReadFailed;
        var writableFrames = destination.Length / Math.Max(1, config.ChannelCount);
        framesRead = Math.Min(requestedFrameCount, writableFrames);
        if (framesRead <= 0) return MediaResult.Success;
        fixed (float* ptr = destination)
        {
            var read = Native.Pa_ReadStream(stream, (nint)ptr, (nuint)framesRead);
            if (read == PaError.paNoError)
            {
                var writtenSamples = framesRead * config.ChannelCount;
                var vol = Volume;
                if (vol != 1.0f && writtenSamples > 0)                 // (10.1) apply volume
                    for (var i = 0; i < writtenSamples; i++) destination[i] *= vol;
                if (writtenSamples < destination.Length) destination[writtenSamples..].Fill(0f);
                lock (_gate) { PositionSeconds += framesRead / (double)config.SampleRate; }
                return MediaResult.Success;
            }
            if (read == PaError.paInputOverflowed) return (int)MediaErrorCode.PortAudioOverflow;
            if (read == PaError.paTimedOut)        return (int)MediaErrorCode.MediaSourceReadTimeout;
            if (read == PaError.paUnanticipatedHostError) return (int)MediaErrorCode.PortAudioHostError;
            return (int)MediaErrorCode.PortAudioInputReadFailed;
        }
    }

    public int Seek(double positionSeconds) => (int)MediaErrorCode.MediaSourceNonSeekable;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseNativeStreamIfOpen();
            State = AudioSourceState.Stopped;
            AudioDeviceChanged = null;
        }
        _onDisposed?.Invoke(this);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private int ApplyDeviceChange(AudioDeviceInfo newDevice)
    {
        AudioDeviceInfo previous;
        int restartResult = MediaResult.Success;
        lock (_gate)
        {
            if (_disposed) return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
            previous = Device;
            Device = newDevice;
            if (_nativeStreaming) { CloseNativeStreamIfOpen(); restartResult = TryStartNativeStream(); }
        }
        if (restartResult != MediaResult.Success) return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
        if (previous != newDevice)
            AudioDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(previous, newDevice));
        return MediaResult.Success;
    }

    private int TryStartNativeStream()
    {
        if (_nativeStreaming) return MediaResult.Success;
        try
        {
            PaError open;
            // (5.2) Use Pa_OpenStream for specific (pa:N) devices, Pa_OpenDefaultStream otherwise.
            if (TryResolvePortAudioDeviceIndex(Device.Id, out var deviceIndex))
            {
                var deviceInfo = Native.Pa_GetDeviceInfo(deviceIndex);
                if (!deviceInfo.HasValue || deviceInfo.Value.maxInputChannels <= 0)
                    return (int)MediaErrorCode.PortAudioStreamOpenFailed;
                var effectiveCh = Math.Clamp(Config.ChannelCount, 1, Math.Max(1, deviceInfo.Value.maxInputChannels));
                var inputParams = new PaStreamParameters
                {
                    device = deviceIndex,
                    channelCount = effectiveCh,
                    sampleFormat = PaSampleFormat.paFloat32,
                    suggestedLatency = deviceInfo.Value.defaultHighInputLatency > 0
                        ? deviceInfo.Value.defaultHighInputLatency
                        : deviceInfo.Value.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = nint.Zero,
                };
                nint sh;
                open = Native.Pa_OpenStream(out sh, inputParameters: inputParams, outputParameters: null,
                    sampleRate: Config.SampleRate, framesPerBuffer: (nuint)Math.Max(1, Config.FramesPerBuffer),
                    streamFlags: PaStreamFlags.paNoFlag,
                    streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                    userData: nint.Zero);
                _stream = sh;
            }
            else
            {
                nint sh;
                open = Native.Pa_OpenDefaultStream(out sh,
                    numInputChannels: Config.ChannelCount, numOutputChannels: 0,
                    sampleFormat: PaSampleFormat.paFloat32, sampleRate: Config.SampleRate,
                    framesPerBuffer: (nuint)Math.Max(1, Config.FramesPerBuffer),
                    streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                    userData: nint.Zero);
                _stream = sh;
            }
            if (open != PaError.paNoError) {
                _stream = nint.Zero; _nativeStreaming = false;
                PortAudioEngine.Logger?.LogError("Pa_Open*Stream failed with {Code} for input device '{Device}'.", open, Device.Name);
                return (int)MediaErrorCode.PortAudioStreamOpenFailed; }
            var start = Native.Pa_StartStream(_stream);
            if (start != PaError.paNoError) {
                Native.Pa_CloseStream(_stream); _stream = nint.Zero; _nativeStreaming = false;
                PortAudioEngine.Logger?.LogError("Pa_StartStream (input) failed with {Code} for device '{Device}'.", start, Device.Name);
                return (int)MediaErrorCode.PortAudioStreamStartFailed; }
            _nativeStreaming = true;
            return MediaResult.Success;
        }
        catch (DllNotFoundException)        { _stream = nint.Zero; _nativeStreaming = false; return (int)MediaErrorCode.PortAudioStreamOpenFailed; }  // (10.4)
        catch (EntryPointNotFoundException) { _stream = nint.Zero; _nativeStreaming = false; return (int)MediaErrorCode.PortAudioStreamOpenFailed; }
        catch (TypeInitializationException) { _stream = nint.Zero; _nativeStreaming = false; return (int)MediaErrorCode.PortAudioStreamOpenFailed; }
    }

    private static bool TryResolvePortAudioDeviceIndex(AudioDeviceId id, out int deviceIndex)
    {
        deviceIndex = -1;
        const string prefix = "pa:";
        var value = id.Value;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(value[prefix.Length..], out deviceIndex) && deviceIndex >= 0;
    }

    private void CloseNativeStreamIfOpen()
    {
        if (_stream == nint.Zero) { _nativeStreaming = false; return; }
        try   { _ = Native.Pa_StopStream(_stream); _ = Native.Pa_CloseStream(_stream); }
        catch { /* Best-effort */ }
        finally { _stream = nint.Zero; _nativeStreaming = false; }
    }
}

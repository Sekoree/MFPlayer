using System.Buffers;
using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;

namespace S.Media.PortAudio.Output;

public sealed unsafe class PortAudioOutput : IAudioOutput
{
    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _deviceProvider;
    private readonly Func<AudioDeviceInfo?> _defaultOutputProvider;
    private readonly AudioEngineConfig _config;
    private nint _stream;
    private int _nativeSampleRate;
    private int _nativeChannelCount;
    private int _nativeFramesPerBuffer;
    private bool _nativeStreaming;
    private bool _disposed;

    public PortAudioOutput(
        AudioDeviceInfo device,
        Func<IReadOnlyList<AudioDeviceInfo>> deviceProvider,
        AudioEngineConfig config,
        Func<AudioDeviceInfo?>? defaultOutputProvider = null)
    {
        Device = device;
        _deviceProvider = deviceProvider;
        _config = config;
        _defaultOutputProvider = defaultOutputProvider ?? (() => null);
        _nativeSampleRate = Math.Max(1, config.SampleRate);
        _nativeChannelCount = Math.Max(1, config.OutputChannelCount);
        _nativeFramesPerBuffer = Math.Max(1, config.FramesPerBuffer);
    }

    public AudioOutputState State { get; private set; } = AudioOutputState.Stopped;

    public AudioDeviceInfo Device { get; private set; }

    public event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;

    public int Start(AudioOutputConfig config)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioStreamStartFailed;
            }

            if (State == AudioOutputState.Running && _nativeStreaming && _stream != nint.Zero)
            {
                return MediaResult.Success;
            }

            var startCode = TryStartNativeStream();
            if (startCode != MediaResult.Success)
            {
                State = AudioOutputState.Stopped;
                return startCode;
            }

            State = AudioOutputState.Running;
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            CloseNativeStreamIfOpen();
            State = AudioOutputState.Stopped;
            return MediaResult.Success;
        }
    }

    public int SetOutputDevice(AudioDeviceId deviceId)
    {
        var devices = _deviceProvider();
        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i].Id == deviceId)
            {
                return ApplyDeviceChange(devices[i]);
            }
        }

        return (int)MediaErrorCode.PortAudioDeviceNotFound;
    }

    public int SetOutputDeviceByName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var devices = _deviceProvider();
        for (var i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i].Name, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return ApplyDeviceChange(devices[i]);
            }
        }

        return (int)MediaErrorCode.PortAudioDeviceNotFound;
    }

    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        if (deviceIndex == -1)
        {
            var defaultOutput = _defaultOutputProvider();
            if (!defaultOutput.HasValue)
            {
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            }

            return ApplyDeviceChange(defaultOutput.Value);
        }

        var devices = _deviceProvider();
        if (deviceIndex < 0 || deviceIndex >= devices.Count)
        {
            return (int)MediaErrorCode.PortAudioDeviceNotFound;
        }

        return ApplyDeviceChange(devices[deviceIndex]);
    }

    public int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex)
    {
        return PushFrame(in frame, sourceChannelByOutputIndex, frame.SourceChannelCount);
    }

    public int PushFrame(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount)
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.PortAudioPushFailed;
        }

        var validation = AudioRouteMapValidator.ValidatePushFrameMap(frame, sourceChannelByOutputIndex, sourceChannelCount);
        if (validation != MediaResult.Success)
        {
            return validation;
        }

        if (State != AudioOutputState.Running)
        {
            return (int)MediaErrorCode.PortAudioPushFailed;
        }

        if (!_nativeStreaming || _stream == nint.Zero)
        {
            return (int)MediaErrorCode.PortAudioStreamStartFailed;
        }

        return TryWriteNativeFrame(frame, sourceChannelByOutputIndex, sourceChannelCount);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseNativeStreamIfOpen();
            State = AudioOutputState.Stopped;
            AudioDeviceChanged = null;
        }
    }

    private int ApplyDeviceChange(AudioDeviceInfo newDevice)
    {
        AudioDeviceInfo previous;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
            }

            previous = Device;
            Device = newDevice;
        }

        if (previous != newDevice)
        {
            AudioDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(previous, newDevice));
        }

        return MediaResult.Success;
    }

    private int TryStartNativeStream()
    {
        if (_nativeStreaming)
        {
            return MediaResult.Success;
        }

        try
        {
            var open = TryOpenSelectedDeviceStream();
            if (open != PaError.paNoError)
            {
                open = Native.Pa_OpenDefaultStream(
                    out _stream,
                    numInputChannels: 0,
                    numOutputChannels: _nativeChannelCount,
                    sampleFormat: PaSampleFormat.paFloat32,
                    sampleRate: _nativeSampleRate,
                    framesPerBuffer: (nuint)_nativeFramesPerBuffer,
                    streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                    userData: nint.Zero);
            }

            if (open != PaError.paNoError)
            {
                _stream = nint.Zero;
                _nativeStreaming = false;
                return (int)MediaErrorCode.PortAudioStreamOpenFailed;
            }

            var start = Native.Pa_StartStream(_stream);
            if (start != PaError.paNoError)
            {
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
                _nativeStreaming = false;
                return (int)MediaErrorCode.PortAudioStreamStartFailed;
            }

            _nativeStreaming = true;
            return MediaResult.Success;
        }
        catch (DllNotFoundException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioStreamOpenFailed;
        }
        catch (EntryPointNotFoundException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioStreamOpenFailed;
        }
        catch (TypeInitializationException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioStreamOpenFailed;
        }
    }

    private PaError TryOpenSelectedDeviceStream()
    {
        _stream = nint.Zero;

        if (!TryResolvePortAudioDeviceIndex(Device.Id, out var deviceIndex))
        {
            return PaError.paInvalidDevice;
        }

        var deviceInfo = Native.Pa_GetDeviceInfo(deviceIndex);
        if (!deviceInfo.HasValue || deviceInfo.Value.maxOutputChannels <= 0)
        {
            return PaError.paInvalidDevice;
        }

        _nativeChannelCount = Math.Clamp(_nativeChannelCount, 1, Math.Max(1, deviceInfo.Value.maxOutputChannels));

        var outputParams = new PaStreamParameters
        {
            device = deviceIndex,
            channelCount = _nativeChannelCount,
            sampleFormat = PaSampleFormat.paFloat32,
            suggestedLatency = deviceInfo.Value.defaultHighOutputLatency > 0
                ? deviceInfo.Value.defaultHighOutputLatency
                : deviceInfo.Value.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = nint.Zero,
        };

        return Native.Pa_OpenStream(
            out _stream,
            inputParameters: null,
            outputParameters: outputParams,
            sampleRate: _nativeSampleRate,
            framesPerBuffer: (nuint)_nativeFramesPerBuffer,
            streamFlags: PaStreamFlags.paNoFlag,
            streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
            userData: nint.Zero);
    }

    private static bool TryResolvePortAudioDeviceIndex(AudioDeviceId id, out int deviceIndex)
    {
        deviceIndex = -1;
        const string prefix = "pa:";
        var value = id.Value;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(value[prefix.Length..], out deviceIndex) && deviceIndex >= 0;
    }

    private int TryWriteNativeFrame(in AudioFrame frame, ReadOnlySpan<int> routeMap, int sourceChannelCount)
    {
        var requiredSamples = frame.FrameCount * _nativeChannelCount;
        if (requiredSamples <= 0)
        {
            return MediaResult.Success;
        }

        var source = frame.Samples.Span;
        var rented = ArrayPool<float>.Shared.Rent(requiredSamples);

        try
        {
            for (var frameIndex = 0; frameIndex < frame.FrameCount; frameIndex++)
            {
                for (var outputChannel = 0; outputChannel < _nativeChannelCount; outputChannel++)
                {
                    var outputOffset = (frameIndex * _nativeChannelCount) + outputChannel;
                    var sourceChannel = outputChannel < routeMap.Length ? routeMap[outputChannel] : -1;
                    if (sourceChannel < 0)
                    {
                        rented[outputOffset] = 0f;
                        continue;
                    }

                    if (sourceChannel >= sourceChannelCount)
                    {
                        return (int)MediaErrorCode.AudioRouteMapInvalid;
                    }

                    var sourceOffset = (frameIndex * sourceChannelCount) + sourceChannel;
                    rented[outputOffset] = sourceOffset < source.Length ? source[sourceOffset] : 0f;
                }
            }

            fixed (float* ptr = rented)
            {
                var framesRemaining = frame.FrameCount;
                var frameOffset = 0;

                while (framesRemaining > 0)
                {
                    if (_disposed || State != AudioOutputState.Running || _stream == nint.Zero)
                    {
                        return (int)MediaErrorCode.PortAudioPushFailed;
                    }

                    var writableFrames = Math.Min(framesRemaining, Math.Max(1, _nativeFramesPerBuffer));
                    var sampleOffset = frameOffset * _nativeChannelCount;
                    var writePtr = ptr + sampleOffset;
                    var write = Native.Pa_WriteStream(_stream, (nint)writePtr, (nuint)writableFrames);
                    if (write == PaError.paNoError)
                    {
                        frameOffset += writableFrames;
                        framesRemaining -= writableFrames;
                        continue;
                    }

                    if (write == PaError.paTimedOut || write == PaError.paOutputUnderflowed)
                    {
                        // Keep blocking semantics: retry transient backpressure until the chunk is accepted.
                        Native.Pa_Sleep(1);
                        continue;
                    }

                    if (write == PaError.paUnanticipatedHostError)
                    {
                        return (int)MediaErrorCode.PortAudioHostError;
                    }

                    return (int)MediaErrorCode.PortAudioPushFailed;
                }

                return MediaResult.Success;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented, clearArray: false);
        }
    }

    private void CloseNativeStreamIfOpen()
    {
        if (_stream == nint.Zero)
        {
            _nativeStreaming = false;
            return;
        }

        try
        {
            _ = Native.Pa_StopStream(_stream);
            _ = Native.Pa_CloseStream(_stream);
        }
        catch
        {
            // Best-effort close for deterministic teardown in fallback-friendly scaffolding.
        }
        finally
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
    }
}


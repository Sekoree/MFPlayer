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
    private nint _stream;
    private int _nativeSampleRate = 48_000;
    private int _nativeChannelCount = 2;
    private bool _nativeStreaming;
    private bool _disposed;

    public PortAudioOutput(AudioDeviceInfo device, Func<IReadOnlyList<AudioDeviceInfo>> deviceProvider)
    {
        Device = device;
        _deviceProvider = deviceProvider;
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

            State = AudioOutputState.Running;
            TryStartNativeStream();
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
            return MediaResult.Success;
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

    private void TryStartNativeStream()
    {
        if (_nativeStreaming)
        {
            return;
        }

        try
        {
            var open = Native.Pa_OpenDefaultStream(
                out _stream,
                numInputChannels: 0,
                numOutputChannels: _nativeChannelCount,
                sampleFormat: PaSampleFormat.paFloat32,
                sampleRate: _nativeSampleRate,
                framesPerBuffer: 256,
                streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                userData: nint.Zero);

            if (open != PaError.paNoError)
            {
                _stream = nint.Zero;
                return;
            }

            var start = Native.Pa_StartStream(_stream);
            if (start != PaError.paNoError)
            {
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
                return;
            }

            _nativeStreaming = true;
        }
        catch (DllNotFoundException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
        catch (EntryPointNotFoundException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
        catch (TypeInitializationException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
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
                var write = Native.Pa_WriteStream(_stream, (nint)ptr, (nuint)frame.FrameCount);
                if (write == PaError.paNoError)
                {
                    return MediaResult.Success;
                }

                if (write == PaError.paOutputUnderflowed)
                {
                    return (int)MediaErrorCode.PortAudioUnderflow;
                }

                if (write == PaError.paTimedOut)
                {
                    return (int)MediaErrorCode.MediaSourceReadTimeout;
                }

                if (write == PaError.paUnanticipatedHostError)
                {
                    return (int)MediaErrorCode.PortAudioHostError;
                }

                return (int)MediaErrorCode.PortAudioPushFailed;
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


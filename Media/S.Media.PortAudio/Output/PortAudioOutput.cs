using System.Buffers;
using Microsoft.Extensions.Logging;
using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Engine;

namespace S.Media.PortAudio.Output;

/// <summary>
/// Hardware audio output backed by a PortAudio stream.
/// Implements <see cref="IAudioOutput"/> with device-selection APIs and automatic resampling.
/// </summary>
/// <remarks>
/// <b>Configuration snapshot (Issue 4.1 / 6.7):</b> Each <see cref="PortAudioOutput"/> captures
/// a snapshot of <see cref="AudioEngineConfig"/> at creation time via
/// <see cref="PortAudioEngine.CreateOutput"/>. Re-initializing the engine with different settings
/// does not update the config of existing outputs.  Recreate outputs after re-initialization if
/// config changes (sample rate, buffer size, latency mode) are needed.
/// </remarks>
public sealed unsafe class PortAudioOutput : IAudioOutput
{
    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _deviceProvider;
    private readonly Func<AudioDeviceInfo?> _defaultOutputProvider;
    private readonly AudioEngineConfig _config;
    private readonly Action<IAudioOutput>? _onDisposed;  // (7.5) cleanup callback for the engine
    private volatile nint _stream;                       // (10.6) volatile: read lock-free in PushFrame hot-path
    private readonly int _configChannelCount;   // immutable: config-requested channel count
    private int _nativeSampleRate;
    private int _nativeChannelCount;            // (8.2) effective channel count actually opened
    private int _nativeFramesPerBuffer;
    private volatile bool _nativeStreaming;     // (10.6) volatile: read lock-free in PushFrame hot-path
    private volatile bool _disposed;            // (10.6) volatile: read lock-free in PushFrame hot-path
    // C.1 — volatile so a PushFrame thread reading _resampler sees the null written by Start() under _gate.
    private volatile AudioResampler? _resampler;
    private AudioOutputConfig _outputConfig = new();

    public PortAudioOutput(
        AudioDeviceInfo device,
        Func<IReadOnlyList<AudioDeviceInfo>> deviceProvider,
        AudioEngineConfig config,
        Func<AudioDeviceInfo?>? defaultOutputProvider = null,
        Action<IAudioOutput>? onDisposed = null)   // (7.5)
    {
        Id = Guid.NewGuid();
        Device = device;
        _deviceProvider = deviceProvider;
        _config = config;
        _defaultOutputProvider = defaultOutputProvider ?? (() => null);
        _onDisposed = onDisposed;
        _configChannelCount = Math.Max(1, config.OutputChannelCount);
        _nativeSampleRate = Math.Max(1, config.SampleRate);
        _nativeChannelCount = _configChannelCount;
        _nativeFramesPerBuffer = Math.Max(1, config.FramesPerBuffer);
    }

    public Guid Id { get; }

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

            _outputConfig = config ?? new AudioOutputConfig();
            _resampler?.Dispose();
            _resampler = null;

            if (State == AudioOutputState.Running && _nativeStreaming && _stream != nint.Zero)
            {
                return MediaResult.Success;
            }

            // Reset effective channel count to config value before each open attempt (8.2)
            _nativeChannelCount = _configChannelCount;

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
        // (8.4) Fast-reject before expensive validation: check state before route-map validation.
        if (_disposed)
        {
            return (int)MediaErrorCode.PortAudioPushFailed;
        }

        if (State != AudioOutputState.Running)
        {
            return (int)MediaErrorCode.PortAudioPushFailed;
        }

        if (!_nativeStreaming || _stream == nint.Zero)
        {
            return (int)MediaErrorCode.PortAudioStreamStartFailed;
        }

        var validation = AudioRouteMapValidator.ValidatePushFrameMap(frame, sourceChannelByOutputIndex, sourceChannelCount);
        if (validation != MediaResult.Success)
        {
            return validation;
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
            _resampler?.Dispose();
            _resampler = null;
            CloseNativeStreamIfOpen();
            State = AudioOutputState.Stopped;
            AudioDeviceChanged = null;
        }

        // (7.5) Notify the engine so it can remove this output from its tracked list.
        // Called outside the lock to avoid deadlock with the engine's own lock.
        _onDisposed?.Invoke(this);
    }

    // (8.1) Restart the native stream on the new device when already running.
    private int ApplyDeviceChange(AudioDeviceInfo newDevice)
    {
        AudioDeviceInfo previous;
        int restartResult = MediaResult.Success;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
            }

            previous = Device;
            Device = newDevice;
            _resampler?.Dispose();
            _resampler = null;

            if (_nativeStreaming)
            {
                // Close the old stream and reopen on the new device under the same lock,
                // consistent with how Start() manages the native stream.
                CloseNativeStreamIfOpen();

                // Reset effective channel count to config value before reopening (8.2)
                _nativeChannelCount = _configChannelCount;

                restartResult = TryStartNativeStream();
                // B.3 — roll back Device on failure so it stays consistent with the (closed) stream.
                if (restartResult != MediaResult.Success)
                    Device = previous;
            }
        }

        if (restartResult != MediaResult.Success)
        {
            return (int)MediaErrorCode.PortAudioDeviceSwitchFailed;
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
                // (10.6) Use a local for the out-parameter; direct volatile-field ref would suppress
                // volatile semantics (CS0420). Assign back with a volatile write after the call.
                nint streamHandle;
                open = Native.Pa_OpenDefaultStream(
                    out streamHandle,
                    numInputChannels: 0,
                    numOutputChannels: _nativeChannelCount,
                    sampleFormat: PaSampleFormat.paFloat32,
                    sampleRate: _nativeSampleRate,
                    framesPerBuffer: (nuint)_nativeFramesPerBuffer,
                    streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                    userData: nint.Zero);
                _stream = streamHandle;
            }

            if (open != PaError.paNoError)
            {
                _stream = nint.Zero;
                _nativeStreaming = false;
                PortAudioEngine.Logger?.LogError("Pa_OpenStream failed with {Code} for device '{Device}'.", open, Device.Name);
                return (int)MediaErrorCode.PortAudioStreamOpenFailed;
            }

            var start = Native.Pa_StartStream(_stream);
            if (start != PaError.paNoError)
            {
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
                _nativeStreaming = false;
                PortAudioEngine.Logger?.LogError("Pa_StartStream failed with {Code} for device '{Device}'.", start, Device.Name);
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

        // (8.2) Clamp the effective channel count to device capability.
        // Only update _nativeChannelCount after a successful open so a failed attempt does not
        // permanently reduce the requested count (which would corrupt the fallback path).
        var effectiveChannelCount = Math.Clamp(
            _nativeChannelCount, 1, Math.Max(1, deviceInfo.Value.maxOutputChannels));

        // (8.3) Use the configured latency mode.
        var suggestedLatency = _config.LatencyMode switch
        {
            AudioLatencyMode.Low    => deviceInfo.Value.defaultLowOutputLatency,
            AudioLatencyMode.Custom => _config.CustomLatencySeconds,
            _                       => deviceInfo.Value.defaultHighOutputLatency > 0
                                           ? deviceInfo.Value.defaultHighOutputLatency
                                           : deviceInfo.Value.defaultLowOutputLatency,
        };

        var outputParams = new PaStreamParameters
        {
            device = deviceIndex,
            channelCount = effectiveChannelCount,
            sampleFormat = PaSampleFormat.paFloat32,
            suggestedLatency = suggestedLatency,
            hostApiSpecificStreamInfo = nint.Zero,
        };

        // (10.6) Use a local for the out-parameter to avoid CS0420 on volatile field.
        nint openedStream;
        var result = Native.Pa_OpenStream(
            out openedStream,
            inputParameters: null,
            outputParameters: outputParams,
            sampleRate: _nativeSampleRate,
            framesPerBuffer: (nuint)_nativeFramesPerBuffer,
            streamFlags: PaStreamFlags.paNoFlag,
            streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
            userData: nint.Zero);
        _stream = openedStream;

        // (8.2) Commit the effective channel count only after a successful open.
        if (result == PaError.paNoError)
        {
            _nativeChannelCount = effectiveChannelCount;
        }

        return result;
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
        // E.1 — guard: only interleaved frames are supported; planar would be silently misinterpreted.
        if (frame.Layout != AudioFrameLayout.Interleaved)
            return (int)MediaErrorCode.MediaInvalidArgument;
        var source = frame.Samples.Span;
        var effectiveFrameCount = frame.FrameCount;
        var effectiveSourceChannelCount = sourceChannelCount;
        float[]? resampledRented = null;

        try
        {
            // --- Sample-rate conversion when source rate differs from native output rate ---
            if (frame.SampleRate > 0 && frame.SampleRate != _nativeSampleRate)
            {
                var resampler = EnsureResampler(frame.SampleRate, sourceChannelCount);
                if (resampler is null)
                {
                    return (int)MediaErrorCode.AudioSampleRateMismatch;
                }

                var estimatedFrames = resampler.EstimateOutputFrameCount(frame.FrameCount);
                var resampledSampleCount = estimatedFrames * sourceChannelCount;
                resampledRented = ArrayPool<float>.Shared.Rent(resampledSampleCount);

                var resampledFrames = resampler.Resample(source, frame.FrameCount, resampledRented.AsSpan(0, resampledSampleCount));
                if (resampledFrames < 0)
                {
                    return (int)MediaErrorCode.AudioChannelCountMismatch;
                }

                source = resampledRented.AsSpan(0, resampledFrames * sourceChannelCount);
                effectiveFrameCount = resampledFrames;
            }

            var requiredSamples = effectiveFrameCount * _nativeChannelCount;
            if (requiredSamples <= 0)
            {
                return MediaResult.Success;
            }

            var rented = ArrayPool<float>.Shared.Rent(requiredSamples);

            try
            {
                for (var frameIndex = 0; frameIndex < effectiveFrameCount; frameIndex++)
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

                        if (sourceChannel >= effectiveSourceChannelCount)
                        {
                            return (int)MediaErrorCode.AudioRouteMapInvalid;
                        }

                        var sourceOffset = (frameIndex * effectiveSourceChannelCount) + sourceChannel;
                        rented[outputOffset] = sourceOffset < source.Length ? source[sourceOffset] : 0f;
                    }
                }

                fixed (float* ptr = rented)
                {
                    var framesRemaining = effectiveFrameCount;
                    var frameOffset = 0;

                    // (10.6) Snapshot the stream handle once before the write loop.
                    // _stream is volatile so this read has acquire semantics on ARM64.
                    var stream = _stream;

                    // (6.1) Compute the deadline for the entire write of this frame batch.
                    var deadline = _config.WriteTimeoutMs > 0
                        ? Environment.TickCount64 + _config.WriteTimeoutMs
                        : long.MaxValue;

                    while (framesRemaining > 0)
                    {
                        if (_disposed || State != AudioOutputState.Running || stream == nint.Zero)
                        {
                            return (int)MediaErrorCode.PortAudioPushFailed;
                        }

                        // (6.1) Enforce write deadline to prevent permanent stall on a blocked device.
                        if (Environment.TickCount64 > deadline)
                        {
                            PortAudioEngine.Logger?.LogWarning(
                                "PortAudioOutput write timeout after {Ms} ms on device '{Device}'. Device may be unplugged or stalled.",
                                _config.WriteTimeoutMs, Device.Name);
                            return (int)MediaErrorCode.PortAudioPushFailed;
                        }

                        var writableFrames = Math.Min(framesRemaining, Math.Max(1, _nativeFramesPerBuffer));
                        var sampleOffset = frameOffset * _nativeChannelCount;
                        var writePtr = ptr + sampleOffset;
                        var write = Native.Pa_WriteStream(stream, (nint)writePtr, (nuint)writableFrames);
                        if (write == PaError.paNoError)
                        {
                            frameOffset += writableFrames;
                            framesRemaining -= writableFrames;
                            continue;
                        }

                        if (write == PaError.paTimedOut || write == PaError.paOutputUnderflowed)
                        {
                            // C.2 — hot-spin first, then yield; avoids the ~15 ms Windows timer
                            // granularity penalty of Thread.Sleep(1) on the first underflow.
                            Thread.SpinWait(50);
                            Thread.Sleep(0);
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
        finally
        {
            if (resampledRented is not null)
            {
                ArrayPool<float>.Shared.Return(resampledRented, clearArray: false);
            }
        }
    }

    private AudioResampler? EnsureResampler(int sourceSampleRate, int sourceChannelCount)
    {
        if (_resampler is not null
            && _resampler.SourceSampleRate == sourceSampleRate
            && _resampler.SourceChannelCount == sourceChannelCount
            && _resampler.TargetSampleRate == _nativeSampleRate
            && _resampler.TargetChannelCount == sourceChannelCount)
        {
            return _resampler;
        }

        _resampler?.Dispose();
        _resampler = null;

        var code = AudioResampler.Create(
            sourceSampleRate,
            sourceChannelCount,
            _nativeSampleRate,
            sourceChannelCount,
            out var resampler,
            _outputConfig.ResamplerMode,
            _outputConfig.ChannelMismatchPolicy);
        if (code == MediaResult.Success)
        {
            _resampler = resampler;
            return _resampler;
        }
        return null;
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
            // A.4 — AbortStream terminates immediately without draining the ring buffer.
            // This prevents stalls on hot-unplug or misbehaving drivers in all teardown paths.
            _ = Native.Pa_AbortStream(_stream);
            _ = Native.Pa_CloseStream(_stream);
        }
        catch
        {
            // Best-effort close for deterministic teardown.
        }
        finally
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
    }
}

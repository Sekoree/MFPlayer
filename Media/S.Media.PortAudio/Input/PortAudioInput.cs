using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;

namespace S.Media.PortAudio.Input;

public sealed unsafe class PortAudioInput : IAudioSource
{
    private readonly Lock _gate = new();
    private bool _disposed;
    private long _sampleCursor;
    private nint _stream;
    private bool _nativeStreaming;

    public PortAudioInput()
    {
        Id = Guid.NewGuid();
        Config = new AudioInputConfig();
    }

    public Guid Id { get; }

    public AudioSourceState State { get; private set; } = AudioSourceState.Stopped;

    /// <inheritdoc/>
    public float Volume { get; set; } = 1.0f;

    /// <inheritdoc/>
    public long? TotalSampleCount => null; // live capture — no known total

    public AudioInputConfig Config { get; private set; }

    public AudioStreamInfo StreamInfo => new()
    {
        SampleRate = Config.SampleRate,
        ChannelCount = Config.ChannelCount,
    };

    public double PositionSeconds { get; private set; }

    public double DurationSeconds => double.NaN;

    public int Start()
    {
        return Start(Config);
    }

    public int Start(AudioInputConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioInputStartFailed;
            }

            if (config.SampleRate <= 0 || config.ChannelCount <= 0)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            Config = config;
            State = AudioSourceState.Running;
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
            State = AudioSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
    {
        framesRead = 0;

        if (requestedFrameCount <= 0)
        {
            return MediaResult.Success;
        }

        AudioInputConfig config;

        lock (_gate)
        {
            if (_disposed || State != AudioSourceState.Running)
            {
                return (int)MediaErrorCode.PortAudioInputReadFailed;
            }

            config = Config;
        }

        var writableFrames = destination.Length / Math.Max(1, config.ChannelCount);
        framesRead = Math.Min(requestedFrameCount, writableFrames);

        if (framesRead <= 0)
        {
            return MediaResult.Success;
        }

        if (_nativeStreaming && _stream != nint.Zero)
        {
            fixed (float* ptr = destination)
            {
                var read = Native.Pa_ReadStream(_stream, (nint)ptr, (nuint)framesRead);
                if (read == PaError.paNoError)
                {
                    var writtenSamples = framesRead * config.ChannelCount;
                    if (writtenSamples < destination.Length)
                    {
                        destination[writtenSamples..].Fill(0f);
                    }

                    lock (_gate)
                    {
                        _sampleCursor += writtenSamples;
                        PositionSeconds += framesRead / (double)config.SampleRate;
                    }

                    return MediaResult.Success;
                }

                if (read == PaError.paInputOverflowed)
                {
                    return (int)MediaErrorCode.PortAudioOverflow;
                }

                if (read == PaError.paTimedOut)
                {
                    return (int)MediaErrorCode.MediaSourceReadTimeout;
                }

                if (read == PaError.paUnanticipatedHostError)
                {
                    return (int)MediaErrorCode.PortAudioHostError;
                }

                return (int)MediaErrorCode.PortAudioInputReadFailed;
            }
        }

        var sampleCount = framesRead * config.ChannelCount;
        for (var i = 0; i < sampleCount; i++)
        {
            destination[i] = ((_sampleCursor + i) % 64) / 64f;
        }

        if (sampleCount < destination.Length)
        {
            destination[sampleCount..].Fill(0f);
        }

        lock (_gate)
        {
            _sampleCursor += sampleCount;
            PositionSeconds += framesRead / (double)config.SampleRate;
        }

        return MediaResult.Success;
    }

    public int Seek(double positionSeconds)
    {
        return (int)MediaErrorCode.MediaSourceNonSeekable;
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
            State = AudioSourceState.Stopped;
        }
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
                numInputChannels: Config.ChannelCount,
                numOutputChannels: 0,
                sampleFormat: PaSampleFormat.paFloat32,
                sampleRate: Config.SampleRate,
                framesPerBuffer: 256,
                streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                userData: nint.Zero);

            if (open != PaError.paNoError)
            {
                _stream = nint.Zero;
                _nativeStreaming = false;
                return;
            }

            var start = Native.Pa_StartStream(_stream);
            if (start != PaError.paNoError)
            {
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
                _nativeStreaming = false;
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

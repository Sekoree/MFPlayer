using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;

namespace S.Media.PortAudio.Input;

public sealed unsafe class PortAudioInput : IAudioSource
{
    // Validation bounds (6.4)
    private const int MaxSampleRate = 384_000;
    private const int MaxChannelCount = 64;
    private const int MaxFramesPerBuffer = 32_768;

    private readonly Lock _gate = new();
    private bool _disposed;
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

            // Lower-bound validation
            if (config.SampleRate <= 0 || config.ChannelCount <= 0 || config.FramesPerBuffer <= 0)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            // Upper-bound validation (6.4)
            if (config.SampleRate > MaxSampleRate ||
                config.ChannelCount > MaxChannelCount ||
                config.FramesPerBuffer > MaxFramesPerBuffer)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            Config = config;

            // If already running with an active native stream, nothing to do.
            if (State == AudioSourceState.Running && _nativeStreaming && _stream != nint.Zero)
            {
                return MediaResult.Success;
            }

            // Close any stale stream from a previous failed start attempt.
            CloseNativeStreamIfOpen();

            // (6.5) Surface the native open/start result to the caller.
            var startResult = TryStartNativeStream();
            if (startResult != MediaResult.Success)
            {
                return startResult;
            }

            State = AudioSourceState.Running;
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

        // (6.3) No synthetic fallback — if native is not available, return an error rather
        // than generating sawtooth test samples that would silently corrupt downstream audio.
        if (!_nativeStreaming || _stream == nint.Zero)
        {
            return (int)MediaErrorCode.PortAudioInputReadFailed;
        }

        var writableFrames = destination.Length / Math.Max(1, config.ChannelCount);
        framesRead = Math.Min(requestedFrameCount, writableFrames);

        if (framesRead <= 0)
        {
            return MediaResult.Success;
        }

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

    // (6.5) Returns an int error code so Start() can surface native failures to the caller.
    private int TryStartNativeStream()
    {
        if (_nativeStreaming)
        {
            return MediaResult.Success;
        }

        try
        {
            var open = Native.Pa_OpenDefaultStream(
                out _stream,
                numInputChannels: Config.ChannelCount,
                numOutputChannels: 0,
                sampleFormat: PaSampleFormat.paFloat32,
                sampleRate: Config.SampleRate,
                framesPerBuffer: (nuint)Math.Max(1, Config.FramesPerBuffer), // (6.2) use config
                streamCallback: (delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>)0,
                userData: nint.Zero);

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
            return (int)MediaErrorCode.PortAudioInitializeFailed;
        }
        catch (EntryPointNotFoundException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioInitializeFailed;
        }
        catch (TypeInitializationException)
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
            return (int)MediaErrorCode.PortAudioInitializeFailed;
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
            // Best-effort close for deterministic teardown.
        }
        finally
        {
            _stream = nint.Zero;
            _nativeStreaming = false;
        }
    }
}

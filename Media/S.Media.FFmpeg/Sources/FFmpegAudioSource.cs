using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Decoders.Internal;
using S.Media.FFmpeg.Media;

namespace S.Media.FFmpeg.Sources;

public sealed class FFmpegAudioSource : IAudioSource
{
    private readonly Lock _gate = new();
    private readonly FFSharedDemuxSession? _sharedDemuxSession;
    private int _readInProgress;
    private bool _disposed;
    private double _positionSeconds;

    public FFmpegAudioSource(double durationSeconds = double.NaN, bool isSeekable = true)
        : this(new AudioStreamInfo { Duration = CreateDuration(durationSeconds) }, durationSeconds, isSeekable, options: null)
    {
    }

    public FFmpegAudioSource(FFmpegMediaItem mediaItem)
        : this(
            ResolveAudioStreamInfo(mediaItem),
            durationSeconds: ResolveDurationSeconds(ResolveAudioStreamInfo(mediaItem).Duration),
            isSeekable: mediaItem.AudioSource?.IsSeekable ?? mediaItem.VideoSource?.IsSeekable ?? true,
            options: null,
            sharedDemuxSession: mediaItem.SharedDemuxSession)
    {
    }

    public FFmpegAudioSource(AudioStreamInfo streamInfo, double durationSeconds = double.NaN, bool isSeekable = true, FFmpegAudioSourceOptions? options = null)
        : this(streamInfo, durationSeconds, isSeekable, options, sharedDemuxSession: null)
    {
    }

    internal FFmpegAudioSource(
        AudioStreamInfo streamInfo,
        double durationSeconds,
        bool isSeekable,
        FFmpegAudioSourceOptions? options,
        FFSharedDemuxSession? sharedDemuxSession)
    {
        StreamInfo = streamInfo;
        DurationSeconds = durationSeconds;
        IsSeekable = isSeekable;
        Options = options ?? new FFmpegAudioSourceOptions();
        _sharedDemuxSession = sharedDemuxSession;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    public AudioSourceState State { get; private set; } = AudioSourceState.Stopped;

    public AudioStreamInfo StreamInfo { get; }

    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Max(0f, value);
    }
    private float _volume = 1.0f;

    /// <inheritdoc/>
    public long? TotalSampleCount =>
        StreamInfo.Duration.HasValue && StreamInfo.SampleRate.GetValueOrDefault(0) > 0
            ? (long)(StreamInfo.Duration.Value.TotalSeconds * StreamInfo.SampleRate!.Value)
            : null;

    public FFmpegAudioSourceOptions Options { get; }

    public bool IsSeekable { get; }

    /// <summary>
    /// Approximate playback position in seconds.
    /// <para>
    /// <b>Accuracy note (P3.16):</b> When a valid presentation timestamp (PTS) is available
    /// from the FFmpeg packet, this value is set directly from the PTS, providing frame-accurate
    /// positioning. When no PTS is available (e.g. raw PCM streams), it falls back to
    /// frame-count accumulation (<c>framesRead / sampleRate</c>).
    /// After a seek, the position is reset to the seek target.
    /// </para>
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                return _positionSeconds;
            }
        }
    }

    public double DurationSeconds { get; }

    public int Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.MediaObjectDisposed;
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
                return (int)MediaErrorCode.MediaObjectDisposed;
            }

            State = AudioSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
    {
        framesRead = 0;

        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0)
        {
            return (int)MediaErrorCode.FFmpegConcurrentReadViolation;
        }

        try
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaObjectDisposed;
            }

            if (requestedFrameCount <= 0)
            {
                return MediaResult.Success;
            }

            if (_sharedDemuxSession is not null)
            {
                var channelCount = StreamInfo.ChannelCount.GetValueOrDefault(2);
                var readCode = _sharedDemuxSession.ReadAudioSamples(destination, requestedFrameCount, channelCount, out framesRead, out var chunkPts);
                if (readCode == MediaResult.Success && framesRead > 0)
                {
                    lock (_gate)
                    {
                        // P3.16: Use PTS-based tracking when a valid PTS is available;
                        // fall back to frame-count accumulation otherwise.
                        if (chunkPts > TimeSpan.Zero)
                        {
                            _positionSeconds = chunkPts.TotalSeconds;
                        }
                        else
                        {
                            _positionSeconds += (double)framesRead / Math.Max(1, StreamInfo.SampleRate.GetValueOrDefault(48_000));
                        }
                    }
                }
                else if (framesRead == 0 && readCode == MediaResult.Success)
                {
                    // No more samples — signal end of stream
                    lock (_gate) { if (State == AudioSourceState.Running) State = AudioSourceState.EndOfStream; }
                }

                return readCode;
            }

            return MediaResult.Success;
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (!IsSeekable)
        {
            return (int)MediaErrorCode.MediaSourceNonSeekable;
        }

        if (_sharedDemuxSession is not null)
        {
            var seekCode = _sharedDemuxSession.Seek(positionSeconds);
            if (seekCode != MediaResult.Success)
            {
                return seekCode;
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaObjectDisposed;
            }

            _positionSeconds = positionSeconds;
            // P4-4: a seek restarts the stream — move back to Running if we were at EndOfStream.
            if (State == AudioSourceState.EndOfStream)
                State = AudioSourceState.Running;
            return MediaResult.Success;
        }
    }

    /// <summary>
    /// Updates the position tracking state without triggering a session seek.
    /// Called by <see cref="FFmpegMediaItem.Seek"/> after the shared session has already been seeked
    /// to avoid the double-seek that would occur if <see cref="Seek"/> were called directly.
    /// </summary>
    internal void NotifySeek(double positionSeconds)
    {
        lock (_gate)
        {
            _positionSeconds = positionSeconds;
            if (State == AudioSourceState.EndOfStream)
                State = AudioSourceState.Running;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            State = AudioSourceState.Stopped;
        }
    }

    private static TimeSpan? CreateDuration(double seconds)
    {
        return double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    public int TryGetEffectiveChannelMap(out FFmpegAudioChannelMap map)
    {
        var channelCount = StreamInfo.ChannelCount.GetValueOrDefault(2);
        // OutputChannelCountOverride limits the number of output channels produced.
        var outputChannelCount = Options.OutputChannelCountOverride is > 0
            ? Options.OutputChannelCountOverride.Value
            : channelCount;

        if (Options.MappingPolicy == FFmpegAudioChannelMappingPolicy.ApplyExplicitRouteMap)
        {
            if (Options.ExplicitChannelMap is null)
            {
                map = default;
                return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
            }

            map = Options.ExplicitChannelMap.Value;
            return map.Validate(out _);
        }

        if (Options.MappingPolicy == FFmpegAudioChannelMappingPolicy.DownmixToMono)
        {
            map = new FFmpegAudioChannelMap(channelCount, 1, [0]);
            return MediaResult.Success;
        }

        if (Options.MappingPolicy == FFmpegAudioChannelMappingPolicy.DownmixToStereo)
        {
            map = new FFmpegAudioChannelMap(channelCount, 2, [0, Math.Min(1, channelCount - 1)]);
            return MediaResult.Success;
        }

        // PreserveSourceLayout — apply override if set.
        if (outputChannelCount != channelCount)
        {
            var clampedOutput = Math.Min(outputChannelCount, channelCount);
            var indices = new int[clampedOutput];
            for (var i = 0; i < clampedOutput; i++) indices[i] = i;
            map = new FFmpegAudioChannelMap(channelCount, clampedOutput, indices);
            return MediaResult.Success;
        }

        map = FFmpegAudioChannelMap.Identity(channelCount);
        return MediaResult.Success;
    }

    private static AudioStreamInfo ResolveAudioStreamInfo(FFmpegMediaItem mediaItem)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);

        if (mediaItem.AudioStreams.Count == 0)
        {
            throw new DecodingException(MediaErrorCode.FFmpegInvalidConfig, "FFmpegMediaItem does not contain an audio stream.");
        }

        return mediaItem.AudioStreams[0];
    }

    private static double ResolveDurationSeconds(TimeSpan? duration)
    {
        if (!duration.HasValue)
        {
            return double.NaN;
        }

        return duration.Value.TotalSeconds >= 0 ? duration.Value.TotalSeconds : double.NaN;
    }
}

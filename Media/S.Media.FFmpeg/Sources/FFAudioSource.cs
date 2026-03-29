using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Decoders.Internal;
using S.Media.FFmpeg.Media;

namespace S.Media.FFmpeg.Sources;

public sealed class FFAudioSource : IAudioSource
{
    private readonly Lock _gate = new();
    private readonly FFSharedDemuxSession? _sharedDemuxSession;
    private int _readInProgress;
    private bool _disposed;
    private double _positionSeconds;

    public FFAudioSource(double durationSeconds = double.NaN, bool isSeekable = true)
        : this(new AudioStreamInfo { Duration = CreateDuration(durationSeconds) }, durationSeconds, isSeekable, options: null)
    {
    }

    public FFAudioSource(FFMediaItem mediaItem)
        : this(
            ResolveAudioStreamInfo(mediaItem),
            durationSeconds: ResolveDurationSeconds(ResolveAudioStreamInfo(mediaItem).Duration),
            isSeekable: mediaItem.AudioSource?.IsSeekable ?? mediaItem.VideoSource?.IsSeekable ?? true,
            options: null,
            sharedDemuxSession: mediaItem.SharedDemuxSession)
    {
    }

    public FFAudioSource(AudioStreamInfo streamInfo, double durationSeconds = double.NaN, bool isSeekable = true, FFAudioSourceOptions? options = null)
        : this(streamInfo, durationSeconds, isSeekable, options, sharedDemuxSession: null)
    {
    }

    internal FFAudioSource(
        AudioStreamInfo streamInfo,
        double durationSeconds,
        bool isSeekable,
        FFAudioSourceOptions? options,
        FFSharedDemuxSession? sharedDemuxSession)
    {
        StreamInfo = streamInfo;
        DurationSeconds = durationSeconds;
        IsSeekable = isSeekable;
        Options = options ?? new FFAudioSourceOptions();
        _sharedDemuxSession = sharedDemuxSession;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    public AudioSourceState State { get; private set; } = AudioSourceState.Stopped;

    public AudioStreamInfo StreamInfo { get; }

    /// <inheritdoc/>
    public float Volume { get; set; } = 1.0f;

    /// <inheritdoc/>
    public long? TotalSampleCount =>
        StreamInfo.Duration.HasValue && StreamInfo.SampleRate.GetValueOrDefault(0) > 0
            ? (long)(StreamInfo.Duration.Value.TotalSeconds * StreamInfo.SampleRate!.Value)
            : null;

    public FFAudioSourceOptions Options { get; }

    public bool IsSeekable { get; }

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
            return _disposed ? (int)MediaErrorCode.MediaInvalidArgument : (State = AudioSourceState.Running) switch { _ => MediaResult.Success };
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
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
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            if (requestedFrameCount <= 0)
            {
                return MediaResult.Success;
            }

            if (_sharedDemuxSession is not null)
            {
                var channelCount = StreamInfo.ChannelCount.GetValueOrDefault(2);
                var readCode = _sharedDemuxSession.ReadAudioSamples(destination, requestedFrameCount, channelCount, out framesRead);
                if (readCode == MediaResult.Success && framesRead > 0)
                {
                    lock (_gate)
                    {
                        _positionSeconds += (double)framesRead / Math.Max(1, StreamInfo.SampleRate.GetValueOrDefault(48_000));
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
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _positionSeconds = positionSeconds;
            return MediaResult.Success;
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

    public int TryGetEffectiveChannelMap(out FFAudioChannelMap map)
    {
        var channelCount = StreamInfo.ChannelCount.GetValueOrDefault(2);

        if (Options.MappingPolicy == FFAudioChannelMappingPolicy.ApplyExplicitRouteMap)
        {
            if (Options.ExplicitChannelMap is null)
            {
                map = default;
                return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
            }

            map = Options.ExplicitChannelMap.Value;
            return map.Validate(out _);
        }

        if (Options.MappingPolicy == FFAudioChannelMappingPolicy.DownmixToMono)
        {
            map = new FFAudioChannelMap(channelCount, 1, [0]);
            return MediaResult.Success;
        }

        if (Options.MappingPolicy == FFAudioChannelMappingPolicy.DownmixToStereo)
        {
            map = new FFAudioChannelMap(channelCount, 2, [0, Math.Min(1, channelCount - 1)]);
            return MediaResult.Success;
        }

        map = FFAudioChannelMap.Identity(channelCount);
        return MediaResult.Success;
    }

    private static AudioStreamInfo ResolveAudioStreamInfo(FFMediaItem mediaItem)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);

        if (mediaItem.AudioStreams.Count == 0)
        {
            throw new DecodingException(MediaErrorCode.FFmpegInvalidConfig, "FFMediaItem does not contain an audio stream.");
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

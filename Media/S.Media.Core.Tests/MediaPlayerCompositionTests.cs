using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class MediaPlayerCompositionTests
{
    [Fact]
    public void Play_DelegatesToMixerStart()
    {
        var mixer = new FakeMixer();
        var player = new MediaPlayer(mixer);
        var media = new FakeMediaItem();

        var result = player.Play(media);

        Assert.Equal(1, mixer.StartCalls);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Play_BoundMedia_AttachesSources_ThenStarts()
    {
        var mixer = new FakeMixer();
        var player = new MediaPlayer(mixer);
        var audio = new FakeAudioSource();
        var video = new FakeVideoSource();
        var media = new FakeBoundMediaItem(audio, video, video);

        var result = player.Play(media);

        Assert.Equal(0, result);
        Assert.Equal(1, mixer.AddAudioSourceCalls);
        Assert.Equal(1, mixer.AddVideoSourceCalls);
        Assert.Equal(1, mixer.SetActiveVideoSourceCalls);
        Assert.Equal(1, mixer.StartCalls);
    }

    [Fact]
    public void Play_BoundMedia_RollsBack_WhenVideoAttachFails()
    {
        var mixer = new FakeMixer { AddVideoSourceReturnCode = 222 };
        var player = new MediaPlayer(mixer);
        var audio = new FakeAudioSource();
        var video = new FakeVideoSource();
        var media = new FakeBoundMediaItem(audio, video, null);

        var result = player.Play(media);

        Assert.Equal(222, result);
        Assert.Equal(1, mixer.AddAudioSourceCalls);
        Assert.Equal(1, mixer.AddVideoSourceCalls);
        Assert.Equal(1, mixer.RemoveAudioSourceCalls);
        Assert.Equal(0, mixer.StartCalls);
    }

    [Fact]
    public void Play_FFMediaItem_AttachesRealBinding_ThenStarts()
    {
        var mixer = new FakeMixer();
        var player = new MediaPlayer(mixer);
        var media = new FFMediaItem(new FFAudioSource(), new FFVideoSource());

        var result = player.Play(media);

        Assert.Equal(0, result);
        Assert.Equal(1, mixer.AddAudioSourceCalls);
        Assert.Equal(1, mixer.AddVideoSourceCalls);
        Assert.Equal(1, mixer.SetActiveVideoSourceCalls);
        Assert.Equal(1, mixer.StartCalls);
    }

    private sealed class FakeMediaItem : IMediaItem
    {
        public IReadOnlyList<AudioStreamInfo> AudioStreams => [];

        public IReadOnlyList<VideoStreamInfo> VideoStreams => [];

        public MediaMetadataSnapshot? Metadata => null;

        public bool HasMetadata => false;
    }

    private sealed class FakeBoundMediaItem : IMediaItem, IMediaPlaybackSourceBinding
    {
        public FakeBoundMediaItem(IAudioSource audioSource, IVideoSource videoSource, IVideoSource? initialActiveVideoSource)
        {
            PlaybackAudioSources = [audioSource];
            PlaybackVideoSources = [videoSource];
            InitialActiveVideoSource = initialActiveVideoSource;
        }

        public IReadOnlyList<AudioStreamInfo> AudioStreams => [];

        public IReadOnlyList<VideoStreamInfo> VideoStreams => [];

        public MediaMetadataSnapshot? Metadata => null;

        public bool HasMetadata => false;

        public IReadOnlyList<IAudioSource> PlaybackAudioSources { get; }

        public IReadOnlyList<IVideoSource> PlaybackVideoSources { get; }

        public IVideoSource? InitialActiveVideoSource { get; }
    }

    private sealed class FakeAudioSource : IAudioSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();
        public AudioSourceState State => AudioSourceState.Stopped;
        public int Start() => 0;
        public int Stop() => 0;
        public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
        {
            framesRead = 0;
            return 0;
        }
        public int Seek(double positionSeconds) => 0;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public void Dispose() { }
    }

    private sealed class FakeVideoSource : IVideoSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();
        public VideoSourceState State => VideoSourceState.Stopped;
        public int Start() => 0;
        public int Stop() => 0;
        public int ReadFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(
                width: 2,
                height: 2,
                pixelFormat: VideoPixelFormat.Rgba32,
                pixelFormatData: new Rgba32PixelFormatData(),
                presentationTime: TimeSpan.Zero,
                isKeyFrame: false,
                plane0: new byte[16],
                plane0Stride: 8);
            return 0;
        }
        public int Seek(double positionSeconds) => 0;
        public int SeekToFrame(long frameIndex) => 0;
        public int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount)
        {
            currentFrameIndex = 0;
            totalFrameCount = 0;
            return 0;
        }
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public long CurrentFrameIndex => 0;
        public long? CurrentDecodeFrameIndex => null;
        public long? TotalFrameCount => null;
        public bool IsSeekable => true;
        public void Dispose() { }
    }

    private sealed class FakeMixer : IAudioVideoMixer
    {
        public int StartCalls { get; private set; }
        public int AddAudioSourceCalls { get; private set; }
        public int RemoveAudioSourceCalls { get; private set; }
        public int AddVideoSourceCalls { get; private set; }
        public int RemoveVideoSourceCalls { get; private set; }
        public int SetActiveVideoSourceCalls { get; private set; }

        public int AddAudioSourceReturnCode { get; set; }
        public int AddVideoSourceReturnCode { get; set; }
        public int SetActiveVideoSourceReturnCode { get; set; }

        public AudioVideoMixerState State => AudioVideoMixerState.Stopped;

        public IMediaClock Clock { get; } = new CoreMediaClock();

        public ClockType ClockType => ClockType.Hybrid;

        public double PositionSeconds => 0;

        public bool IsRunning => false;

        public IAudioMixer AudioMixer => throw new NotSupportedException();

        public IVideoMixer VideoMixer => throw new NotSupportedException();

        public IReadOnlyList<IAudioSource> AudioSources => [];

        public IReadOnlyList<IVideoSource> VideoSources => [];

        public MixerSourceDetachOptions AudioSourceDetachOptions => new();

        public MixerSourceDetachOptions VideoSourceDetachOptions => new();

        public event EventHandler<AudioSourceErrorEventArgs>? AudioSourceError
        {
            add { }
            remove { }
        }

        public event EventHandler<VideoSourceErrorEventArgs>? VideoSourceError
        {
            add { }
            remove { }
        }

        public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged
        {
            add { }
            remove { }
        }

        public int Start()
        {
            StartCalls++;
            return 0;
        }

        public int Pause() => 0;

        public int Resume() => 0;

        public int Stop() => 0;

        public int Seek(double positionSeconds) => 0;

        public int AddAudioSource(IAudioSource source)
        {
            AddAudioSourceCalls++;
            return AddAudioSourceReturnCode;
        }

        public int RemoveAudioSource(IAudioSource source)
        {
            RemoveAudioSourceCalls++;
            return 0;
        }

        public int AddVideoSource(IVideoSource source)
        {
            AddVideoSourceCalls++;
            return AddVideoSourceReturnCode;
        }

        public int RemoveVideoSource(IVideoSource source)
        {
            RemoveVideoSourceCalls++;
            return 0;
        }

        public int ConfigureAudioSourceDetachOptions(MixerSourceDetachOptions options) => 0;

        public int ConfigureVideoSourceDetachOptions(MixerSourceDetachOptions options) => 0;

        public int SetClockType(ClockType clockType) => MixerClockTypeRules.Validate(MixerKind.AudioVideo, clockType);

        public int SetActiveVideoSource(IVideoSource source)
        {
            SetActiveVideoSourceCalls++;
            return SetActiveVideoSourceReturnCode;
        }
    }
}


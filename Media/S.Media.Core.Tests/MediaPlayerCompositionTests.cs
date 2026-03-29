using S.Media.Core.Audio;
using S.Media.Core.Errors;
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
    /// <summary>
    /// §4.2 fix: a plain <see cref="IMediaItem"/> that does not implement
    /// <see cref="IMediaPlaybackSourceBinding"/> must be rejected — starting the pump
    /// threads with no sources attached produces silent, empty playback with no error.
    /// </summary>
    [Fact]
    public void Play_PlainMediaItem_ReturnsInvalidArgument_AndDoesNotStartMixer()
    {
        var player = new MediaPlayer();
        var media = new FakeMediaItem();

        var result = player.Play(media);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
        Assert.Equal(AVMixerState.Stopped, player.State);
    }

    [Fact]
    public void Play_BoundMedia_AttachesSources_ThenStartsPlayback()
    {
        var player = new MediaPlayer();
        var audio = new FakeAudioSource();
        var video = new FakeVideoSource();
        var media = new FakeBoundMediaItem(audio, video, video);

        var result = player.Play(media);

        Assert.Equal(0, result);
        Assert.Single(player.AudioSources);
        Assert.Equal(audio.Id, player.AudioSources[0].Id);
        Assert.Single(player.VideoSources);
        Assert.Equal(video.Id, player.VideoSources[0].Id);
        Assert.Equal(AVMixerState.Running, player.State);
    }

    [Fact]
    public void Play_BoundMedia_RollsBack_WhenVideoAttachFails()
    {
        var player = new MediaPlayer();
        var audio = new FakeAudioSource();
        var video = new FakeVideoSource();

        // Pre-register the video source to trigger a SourceIdCollision on Play
        player.AddVideoSource(video);

        var media = new FakeBoundMediaItem(audio, video, null);

        var result = player.Play(media);

        Assert.Equal((int)MediaErrorCode.MixerSourceIdCollision, result);
        // Audio source should have been rolled back
        Assert.DoesNotContain(player.AudioSources, s => s.Id == audio.Id);
    }

    [Fact]
    public void Play_FFMediaItem_AttachesRealBinding_ThenStartsPlayback()
    {
        var player = new MediaPlayer();
        var media = new FFMediaItem(new FFAudioSource(), new FFVideoSource());

        var result = player.Play(media);

        Assert.Equal(0, result);
        Assert.Single(player.AudioSources);
        Assert.Single(player.VideoSources);
        Assert.Equal(AVMixerState.Running, player.State);
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
        public Guid Id { get; } = Guid.NewGuid();
        public AudioSourceState State => AudioSourceState.Stopped;
        public AudioStreamInfo StreamInfo => default;
        public float Volume { get; set; } = 1.0f;
        public long? TotalSampleCount => null;
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
        public Guid Id { get; } = Guid.NewGuid();
        public VideoSourceState State => VideoSourceState.Stopped;
        public VideoStreamInfo StreamInfo => default;
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
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public long CurrentFrameIndex => 0;
        public long? CurrentDecodeFrameIndex => null;
        public long? TotalFrameCount => null;
        public bool IsSeekable => true;
        public void Dispose() { }
    }
}

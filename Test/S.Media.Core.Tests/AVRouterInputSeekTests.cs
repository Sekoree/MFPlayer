using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AVRouterInputSeekTests
{
    [Fact]
    public void TrySeekInput_SeekableAudioInput_ForwardsSeek()
    {
        using var router = new AVRouter();
        using var channel = new TestAudioChannel(canSeek: true);
        var inputId = router.RegisterAudioInput(channel);

        var ok = router.TrySeekInput(inputId, TimeSpan.FromSeconds(7));

        Assert.True(ok);
        Assert.Equal(TimeSpan.FromSeconds(7), channel.LastSeekPosition);
        Assert.Equal(1, channel.SeekCallCount);
    }

    [Fact]
    public void TrySeekInput_NonSeekableAudioInput_ReturnsFalse()
    {
        using var router = new AVRouter();
        using var channel = new TestAudioChannel(canSeek: false);
        var inputId = router.RegisterAudioInput(channel);

        var ok = router.TrySeekInput(inputId, TimeSpan.FromSeconds(7));

        Assert.False(ok);
        Assert.Equal(0, channel.SeekCallCount);
    }

    [Fact]
    public void TryRewindInput_SeekableAudioInput_SeeksToZero()
    {
        using var router = new AVRouter();
        using var channel = new TestAudioChannel(canSeek: true);
        var inputId = router.RegisterAudioInput(channel);

        var ok = router.TryRewindInput(inputId);

        Assert.True(ok);
        Assert.Equal(TimeSpan.Zero, channel.LastSeekPosition);
        Assert.Equal(1, channel.SeekCallCount);
    }

    [Fact]
    public void TrySeekInput_UnknownInput_Throws()
    {
        using var router = new AVRouter();
        var unknown = new InputId(Guid.NewGuid());

        Assert.Throws<MediaRoutingException>(() => router.TrySeekInput(unknown, TimeSpan.FromSeconds(1)));
    }

    private sealed class TestAudioChannel : IAudioChannel, ISeekableInput
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat { get; } = new(48000, 2);
        public bool IsOpen => true;
        public bool CanSeek { get; }
        public int BufferDepth => 128;
        public int BufferAvailable => 0;
        public TimeSpan Position => TimeSpan.Zero;
        public float Volume { get; set; } = 1.0f;
        public int SeekCallCount { get; private set; }
        public TimeSpan LastSeekPosition { get; private set; } = TimeSpan.Zero;

        public event EventHandler? EndOfStream
        {
            add { }
            remove { }
        }

        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { }
            remove { }
        }

        public TestAudioChannel(bool canSeek) => CanSeek = canSeek;

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest.Clear();
            return 0;
        }

        public void Seek(TimeSpan position)
        {
            SeekCallCount++;
            LastSeekPosition = position;
        }

        public void Dispose()
        {
        }
    }
}


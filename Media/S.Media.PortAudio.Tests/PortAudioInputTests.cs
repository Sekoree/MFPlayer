using S.Media.Core.Errors;
using S.Media.PortAudio.Input;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioInputTests
{
    [Fact]
    public void LiveInput_HasNanDuration_AndIsNonSeekable()
    {
        using var input = new PortAudioInput();

        Assert.True(double.IsNaN(input.DurationSeconds));
        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, input.Seek(1.0));
    }

    [Fact]
    public void ReadSamples_ZeroFillsRemainingDestination_WhenNotEnoughWritableFrames()
    {
        using var input = new PortAudioInput();
        Assert.Equal(MediaResult.Success, input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2 }));

        var destination = new float[10];
        var code = input.ReadSamples(destination, requestedFrameCount: 4, out var framesRead);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(4, framesRead);
        Assert.NotEqual(0f, destination[1]);
        Assert.NotEqual(0f, destination[7]);
        Assert.Equal(0f, destination[8]);
        Assert.Equal(0f, destination[9]);
    }

    [Fact]
    public void ReadSamples_ReturnsInputReadFailed_WhenNotRunning()
    {
        using var input = new PortAudioInput();

        var buffer = new float[8];
        var code = input.ReadSamples(buffer, 4, out _);

        Assert.Equal((int)MediaErrorCode.PortAudioInputReadFailed, code);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        using var input = new PortAudioInput();

        Assert.Equal(MediaResult.Success, input.Stop());
        Assert.Equal(MediaResult.Success, input.Start());
        Assert.Equal(MediaResult.Success, input.Stop());
        Assert.Equal(MediaResult.Success, input.Stop());
    }
}


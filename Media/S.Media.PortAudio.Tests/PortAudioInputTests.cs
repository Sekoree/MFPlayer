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
        var startCode = input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2 });

        // If native PortAudio hardware is unavailable in this environment, Start() now correctly
        // returns an error instead of silently falling back to synthetic sawtooth data (fix 6.3/6.5).
        if (startCode != MediaResult.Success)
        {
            Assert.True(
                startCode is (int)MediaErrorCode.PortAudioStreamOpenFailed
                    or (int)MediaErrorCode.PortAudioStreamStartFailed
                    or (int)MediaErrorCode.PortAudioInitializeFailed,
                $"Unexpected start error code: {startCode}");
            return;
        }

        var destination = new float[10];
        // 4 frames × 2 ch = 8 samples written; last 2 samples must be zero-filled.
        var code = input.ReadSamples(destination, requestedFrameCount: 4, out var framesRead);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(4, framesRead);
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

        var startCode = input.Start();
        // Start may succeed (hardware available) or fail (no hardware in CI).
        Assert.True(
            startCode is MediaResult.Success
                or (int)MediaErrorCode.PortAudioStreamOpenFailed
                or (int)MediaErrorCode.PortAudioStreamStartFailed
                or (int)MediaErrorCode.PortAudioInitializeFailed,
            $"Unexpected start code: {startCode}");

        Assert.Equal(MediaResult.Success, input.Stop());
        Assert.Equal(MediaResult.Success, input.Stop());
    }

    [Fact]
    public void Start_ReturnsInvalidConfig_ForZeroSampleRate()
    {
        using var input = new PortAudioInput();

        var code = input.Start(new AudioInputConfig { SampleRate = 0, ChannelCount = 2 });

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig, code);
    }

    [Fact]
    public void Start_ReturnsInvalidConfig_WhenExceedingUpperBounds()
    {
        using var input = new PortAudioInput();

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 999_999, ChannelCount = 2 }));

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 200 }));

        Assert.Equal((int)MediaErrorCode.PortAudioInvalidConfig,
            input.Start(new AudioInputConfig { SampleRate = 48_000, ChannelCount = 2, FramesPerBuffer = 100_000 }));
    }
}

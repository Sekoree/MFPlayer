using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests;

public class AudioDeviceInfoTests
{
    private static AudioDeviceInfo MakeDevice(int maxIn, int maxOut) =>
        new(Index: 0, Name: "Test Device", HostApiIndex: 0,
            MaxInputChannels: maxIn, MaxOutputChannels: maxOut,
            DefaultSampleRate: 48000, DefaultLowOutputLatency: 0.01,
            DefaultHighOutputLatency: 0.1);

    [Fact]
    public void ClampOutputChannels_BelowMax_ReturnsRequested()
    {
        var device = MakeDevice(0, 8);
        Assert.Equal(2, device.ClampOutputChannels(2));
    }

    [Fact]
    public void ClampOutputChannels_AtMax_ReturnsMax()
    {
        var device = MakeDevice(0, 4);
        Assert.Equal(4, device.ClampOutputChannels(4));
    }

    [Fact]
    public void ClampOutputChannels_AboveMax_ClampsToMax()
    {
        var device = MakeDevice(0, 4);
        Assert.Equal(4, device.ClampOutputChannels(8));
    }

    [Fact]
    public void ClampInputChannels_BelowMax_ReturnsRequested()
    {
        var device = MakeDevice(8, 0);
        Assert.Equal(1, device.ClampInputChannels(1));
    }

    [Fact]
    public void ClampInputChannels_AboveMax_ClampsToMax()
    {
        var device = MakeDevice(2, 0);
        Assert.Equal(2, device.ClampInputChannels(100));
    }

    [Fact]
    public void ClampOutputChannels_JackLike256Max_AllowsAnyUpTo256()
    {
        // Simulate JACK device reporting 256 max ports
        var device = MakeDevice(0, 256);
        Assert.Equal(64, device.ClampOutputChannels(64));
        Assert.Equal(256, device.ClampOutputChannels(300));
    }
}


using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>Tests for <see cref="AudioFormat.NegotiateFor(AudioFormat, AudioDeviceInfo, int)"/>.</summary>
public sealed class AudioFormatNegotiateForTests
{
    private static AudioDeviceInfo Device(int maxOutCh) =>
        new(Index: 0, Name: "test", HostApiIndex: 0,
            MaxInputChannels: 0, MaxOutputChannels: maxOutCh,
            DefaultSampleRate: 48000, DefaultLowOutputLatency: 0.01, DefaultHighOutputLatency: 0.1);

    [Fact]
    public void Negotiate_StereoSourceStereoDevice_Passthrough()
    {
        var src = new AudioFormat(48000, 2);
        var (hw, map) = AudioFormat.NegotiateFor(src, Device(2));

        Assert.Equal(48000, hw.SampleRate);
        Assert.Equal(2,     hw.Channels);
        Assert.Equal(2,     map.Routes.Count);
        Assert.Contains(map.Routes, r => r.SrcChannel == 0 && r.DstChannel == 0);
        Assert.Contains(map.Routes, r => r.SrcChannel == 1 && r.DstChannel == 1);
    }

    [Fact]
    public void Negotiate_MonoSourceStereoDevice_FansOut()
    {
        var src = new AudioFormat(44100, 1);
        var (hw, map) = AudioFormat.NegotiateFor(src, Device(2));

        Assert.Equal(2, hw.Channels);
        // Mono->stereo: two routes from src 0.
        Assert.Equal(2, map.Routes.Count);
        Assert.All(map.Routes, r => Assert.Equal(0, r.SrcChannel));
    }

    [Fact]
    public void Negotiate_MultiChannelSource_ClampedToCap()
    {
        var src = new AudioFormat(48000, 6);
        var (hw, _) = AudioFormat.NegotiateFor(src, Device(8), maxChannels: 2);

        Assert.Equal(2, hw.Channels);
    }

    [Fact]
    public void Negotiate_CapLargerThanDevice_UsesDevice()
    {
        var src = new AudioFormat(48000, 4);
        var (hw, _) = AudioFormat.NegotiateFor(src, Device(2), maxChannels: 8);

        Assert.Equal(2, hw.Channels);
    }

    [Fact]
    public void Negotiate_InvalidMaxChannels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioFormat.NegotiateFor(new AudioFormat(48000, 2), Device(2), maxChannels: 0));
    }

    [Fact]
    public void Negotiate_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AudioFormat.NegotiateFor(new AudioFormat(48000, 2), null!));
    }
}

/// <summary>Tests for <see cref="ChannelRouteMap.AutoStereoDownmix"/>.</summary>
public sealed class ChannelRouteMapAutoStereoDownmixTests
{
    [Fact]
    public void Mono_ToStereo_FansOut()
    {
        var m = ChannelRouteMap.AutoStereoDownmix(1, 2);
        Assert.Equal(2, m.Routes.Count);
        Assert.All(m.Routes, r => Assert.Equal(0, r.SrcChannel));
    }

    [Fact]
    public void Stereo_ToMono_Averages()
    {
        var m = ChannelRouteMap.AutoStereoDownmix(2, 1);
        Assert.Equal(2, m.Routes.Count);
        Assert.All(m.Routes, r => Assert.Equal(0, r.DstChannel));
        Assert.All(m.Routes, r => Assert.Equal(0.5f, r.Gain));
    }

    [Fact]
    public void FiveOne_ToStereo_UsesBs775Coefficients()
    {
        // 5.1: FL FR FC LFE BL BR → stereo
        var m = ChannelRouteMap.AutoStereoDownmix(6, 2);

        // FL→L and FR→R are unit-gain.
        Assert.Contains(m.Routes, r => r.SrcChannel == 0 && r.DstChannel == 0 && r.Gain == 1.0f);
        Assert.Contains(m.Routes, r => r.SrcChannel == 1 && r.DstChannel == 1 && r.Gain == 1.0f);

        // FC fans to both at ~-3 dB.
        const float c = 0.7071067811865476f;
        Assert.Contains(m.Routes, r => r.SrcChannel == 2 && r.DstChannel == 0 && r.Gain == c);
        Assert.Contains(m.Routes, r => r.SrcChannel == 2 && r.DstChannel == 1 && r.Gain == c);

        // LFE (ch 3) is dropped.
        Assert.DoesNotContain(m.Routes, r => r.SrcChannel == 3);

        // BL→L, BR→R at ~-3 dB.
        Assert.Contains(m.Routes, r => r.SrcChannel == 4 && r.DstChannel == 0 && r.Gain == c);
        Assert.Contains(m.Routes, r => r.SrcChannel == 5 && r.DstChannel == 1 && r.Gain == c);
    }

    [Fact]
    public void Stereo_ToStereo_Passthrough()
    {
        var m = ChannelRouteMap.AutoStereoDownmix(2, 2);
        Assert.Equal(2, m.Routes.Count);
        Assert.Contains(m.Routes, r => r.SrcChannel == 0 && r.DstChannel == 0 && r.Gain == 1f);
        Assert.Contains(m.Routes, r => r.SrcChannel == 1 && r.DstChannel == 1 && r.Gain == 1f);
    }

    [Fact]
    public void ZeroChannels_ReturnsSilence()
    {
        var m = ChannelRouteMap.AutoStereoDownmix(0, 2);
        Assert.Empty(m.Routes);
    }
}


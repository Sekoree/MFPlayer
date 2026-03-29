using S.Media.Core.Audio;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class ConfigSimplificationTests
{
    // ── AudioEngineConfig empty-string normalization ──

    [Fact]
    public void AudioEngineConfig_PreferredHostApi_NormalizesEmptyToNull()
    {
        var config = new AudioEngineConfig { PreferredHostApi = "" };
        Assert.Null(config.PreferredHostApi);
    }

    [Fact]
    public void AudioEngineConfig_PreferredHostApi_NormalizesWhitespaceToNull()
    {
        var config = new AudioEngineConfig { PreferredHostApi = "   " };
        Assert.Null(config.PreferredHostApi);
    }

    [Fact]
    public void AudioEngineConfig_PreferredHostApi_PreservesNonEmpty()
    {
        var config = new AudioEngineConfig { PreferredHostApi = "ALSA" };
        Assert.Equal("ALSA", config.PreferredHostApi);
    }

    [Fact]
    public void AudioEngineConfig_PreferredHostApi_DefaultsToNull()
    {
        var config = new AudioEngineConfig();
        Assert.Null(config.PreferredHostApi);
    }

    // ── AVMixerConfig.ForStereo ──

    [Fact]
    public void ForStereo_ReturnsStereoConfig()
    {
        var config = AVMixerConfig.ForStereo();
        Assert.Equal(2, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 1 }, config.RouteMap);
        Assert.Equal(8, config.VideoOutputQueueCapacity);
        Assert.Equal(VideoDispatchPolicy.DirectThread, config.PresentationHostPolicy);
    }

    [Fact]
    public void ForStereo_DefaultTimestampPolicy_IsRebaseOnDiscontinuity()
    {
        var config = AVMixerConfig.ForStereo();
        Assert.Equal(VideoTimestampMode.RebaseOnDiscontinuity, config.TimestampMode);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.DiscontinuityThreshold);
    }

    // ── AVMixerConfig.ForSourceToStereo ──

    [Fact]
    public void ForSourceToStereo_Mono_DuplicatesToBothSlots()
    {
        var config = AVMixerConfig.ForSourceToStereo(1);
        Assert.Equal(1, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 0 }, config.RouteMap);
    }

    [Fact]
    public void ForSourceToStereo_Stereo_UsesStandardMap()
    {
        var config = AVMixerConfig.ForSourceToStereo(2);
        Assert.Equal(2, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 1 }, config.RouteMap);
    }

    [Fact]
    public void ForSourceToStereo_MultiChannel_MapsFrontPair()
    {
        var config = AVMixerConfig.ForSourceToStereo(6);
        Assert.Equal(6, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 1 }, config.RouteMap);
    }

    [Fact]
    public void ForSourceToStereo_ZeroOrNegative_ClampsToMono()
    {
        var config = AVMixerConfig.ForSourceToStereo(0);
        Assert.Equal(1, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 0 }, config.RouteMap);

        var config2 = AVMixerConfig.ForSourceToStereo(-5);
        Assert.Equal(1, config2.SourceChannelCount);
    }

    [Fact]
    public void ForSourceToStereo_DefaultStaleThreshold_IsSet()
    {
        var config = AVMixerConfig.ForSourceToStereo(2);
        Assert.Equal(TimeSpan.FromMilliseconds(200), config.OutputStaleFrameThreshold);
    }

    // ── AVMixerConfig.ForPassthrough ──

    [Fact]
    public void ForPassthrough_Stereo_MapsOneToOne()
    {
        var config = AVMixerConfig.ForPassthrough(2);
        Assert.Equal(2, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 1 }, config.RouteMap);
    }

    [Fact]
    public void ForPassthrough_EightChannels_MapsAllOneToOne()
    {
        var config = AVMixerConfig.ForPassthrough(8);
        Assert.Equal(8, config.SourceChannelCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, config.RouteMap);
    }

    [Fact]
    public void ForPassthrough_Mono_SingleElement()
    {
        var config = AVMixerConfig.ForPassthrough(1);
        Assert.Equal(1, config.SourceChannelCount);
        Assert.Equal(new[] { 0 }, config.RouteMap);
    }

    [Fact]
    public void ForPassthrough_ZeroOrNegative_ClampsToMono()
    {
        var config = AVMixerConfig.ForPassthrough(0);
        Assert.Equal(1, config.SourceChannelCount);
        Assert.Equal(new[] { 0 }, config.RouteMap);
    }

    [Fact]
    public void GetVideoOutputQueueCapacity_UsesPerOutputOverride_WhenPresent()
    {
        var config = new AVMixerConfig
        {
            VideoOutputQueueCapacity = 4,
        };

        var outputId = Guid.NewGuid();
        config.VideoOutputQueueCapacityOverrides[outputId] = 8;

        Assert.Equal(8, config.GetVideoOutputQueueCapacity(outputId));
        Assert.Equal(4, config.GetVideoOutputQueueCapacity(Guid.NewGuid()));
    }
}


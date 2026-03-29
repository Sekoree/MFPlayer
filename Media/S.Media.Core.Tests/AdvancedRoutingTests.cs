using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AdvancedRoutingTests
{
    // ─── Interface discovery ────────────────────────────────────────────

    [Fact]
    public void AudioVideoMixer_ImplementsIMixerRouting()
    {
        var mixer = new AVMixer();

        Assert.IsAssignableFrom<IMixerRouting>(mixer);
    }

    [Fact]
    public void MediaPlayer_AlsoImplementsIMixerRouting()
    {
        IMediaPlayer player = new MediaPlayer();

        // MediaPlayer inherits from AVMixer, which implements IMixerRouting.
        Assert.IsAssignableFrom<IMixerRouting>(player);
    }

    // ─── Audio routing rules ────────────────────────────────────────────

    [Fact]
    public void AddAudioRoutingRule_AddsRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0, 1.0f);

        var result = mixer.AddAudioRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
        Assert.Single(mixer.AudioRoutingRules);
        Assert.Equal(rule, mixer.AudioRoutingRules[0]);
    }

    [Fact]
    public void RemoveAudioRoutingRule_RemovesRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0, 1.0f);
        mixer.AddAudioRoutingRule(rule);

        var result = mixer.RemoveAudioRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
        Assert.Empty(mixer.AudioRoutingRules);
    }

    [Fact]
    public void RemoveAudioRoutingRule_NonExistentRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0, 1.0f);

        var result = mixer.RemoveAudioRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void ClearAudioRoutingRules_ClearsAll_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        mixer.AddAudioRoutingRule(new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0));
        mixer.AddAudioRoutingRule(new AudioRoutingRule(Guid.NewGuid(), 1, Guid.NewGuid(), 1));

        var result = mixer.ClearAudioRoutingRules();

        Assert.Equal(MediaResult.Success, result);
        Assert.Empty(mixer.AudioRoutingRules);
    }

    [Fact]
    public void AudioRoutingRules_ReturnsSnapshot_NotLiveReference()
    {
        var mixer = new AVMixer();
        var rule1 = new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0);
        mixer.AddAudioRoutingRule(rule1);

        var snapshot = mixer.AudioRoutingRules;
        mixer.AddAudioRoutingRule(new AudioRoutingRule(Guid.NewGuid(), 1, Guid.NewGuid(), 1));

        Assert.Single(snapshot); // snapshot should not have changed
        Assert.Equal(2, mixer.AudioRoutingRules.Count); // live query shows 2
    }

    [Fact]
    public void AudioRoutingRule_DefaultGain_IsUnity()
    {
        var rule = new AudioRoutingRule(Guid.NewGuid(), 0, Guid.NewGuid(), 0);

        Assert.Equal(1.0f, rule.Gain);
    }

    // ─── Video routing rules ────────────────────────────────────────────

    [Fact]
    public void AddVideoRoutingRule_AddsRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid());

        var result = mixer.AddVideoRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
        Assert.Single(mixer.VideoRoutingRules);
        Assert.Equal(rule, mixer.VideoRoutingRules[0]);
    }

    [Fact]
    public void RemoveVideoRoutingRule_RemovesRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid());
        mixer.AddVideoRoutingRule(rule);

        var result = mixer.RemoveVideoRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
        Assert.Empty(mixer.VideoRoutingRules);
    }

    [Fact]
    public void RemoveVideoRoutingRule_NonExistentRule_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        var rule = new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid());

        var result = mixer.RemoveVideoRoutingRule(rule);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void ClearVideoRoutingRules_ClearsAll_ReturnsSuccess()
    {
        var mixer = new AVMixer();
        mixer.AddVideoRoutingRule(new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid()));
        mixer.AddVideoRoutingRule(new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid()));

        var result = mixer.ClearVideoRoutingRules();

        Assert.Equal(MediaResult.Success, result);
        Assert.Empty(mixer.VideoRoutingRules);
    }

    [Fact]
    public void VideoRoutingRules_ReturnsSnapshot_NotLiveReference()
    {
        var mixer = new AVMixer();
        var rule1 = new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid());
        mixer.AddVideoRoutingRule(rule1);

        var snapshot = mixer.VideoRoutingRules;
        mixer.AddVideoRoutingRule(new VideoRoutingRule(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Single(snapshot);
        Assert.Equal(2, mixer.VideoRoutingRules.Count);
    }

    // ─── Multiple audio rules for the same output ───────────────────────

    [Fact]
    public void MultipleAudioRules_CanTargetSameOutput()
    {
        var mixer = new AVMixer();
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var outputId = Guid.NewGuid();

        mixer.AddAudioRoutingRule(new AudioRoutingRule(sourceA, 0, outputId, 0, 0.8f));
        mixer.AddAudioRoutingRule(new AudioRoutingRule(sourceB, 0, outputId, 1, 0.5f));

        Assert.Equal(2, mixer.AudioRoutingRules.Count);
        Assert.All(mixer.AudioRoutingRules, r => Assert.Equal(outputId, r.OutputId));
    }

    [Fact]
    public void MultipleVideoRules_CanTargetDifferentOutputs()
    {
        var mixer = new AVMixer();
        var sourceId = Guid.NewGuid();
        var out1 = Guid.NewGuid();
        var out2 = Guid.NewGuid();

        mixer.AddVideoRoutingRule(new VideoRoutingRule(sourceId, out1));
        mixer.AddVideoRoutingRule(new VideoRoutingRule(sourceId, out2));

        Assert.Equal(2, mixer.VideoRoutingRules.Count);
    }

    // ─── Record struct equality ─────────────────────────────────────────

    [Fact]
    public void AudioRoutingRule_EqualityByValue()
    {
        var sourceId = Guid.NewGuid();
        var outputId = Guid.NewGuid();

        var a = new AudioRoutingRule(sourceId, 0, outputId, 1, 0.75f);
        var b = new AudioRoutingRule(sourceId, 0, outputId, 1, 0.75f);

        Assert.Equal(a, b);
    }

    [Fact]
    public void VideoRoutingRule_EqualityByValue()
    {
        var sourceId = Guid.NewGuid();
        var outputId = Guid.NewGuid();

        var a = new VideoRoutingRule(sourceId, outputId);
        var b = new VideoRoutingRule(sourceId, outputId);

        Assert.Equal(a, b);
    }
}

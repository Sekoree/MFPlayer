namespace S.Media.Core.Mixing;

/// <summary>
/// Implemented by mixers that support per-source, per-channel routing rules.
/// Both <see cref="AVMixer"/> and <see cref="S.Media.Core.Playback.MediaPlayer"/>
/// (which inherits from <see cref="AVMixer"/>) implement this interface.
/// </summary>
public interface IMixerRouting
{
    /// <summary>Current set of active audio routing rules.</summary>
    IReadOnlyList<AudioRoutingRule> AudioRoutingRules { get; }

    /// <summary>Current set of active video routing rules.</summary>
    IReadOnlyList<VideoRoutingRule> VideoRoutingRules { get; }

    int AddAudioRoutingRule(AudioRoutingRule rule);
    int RemoveAudioRoutingRule(AudioRoutingRule rule);
    int ClearAudioRoutingRules();

    int AddVideoRoutingRule(VideoRoutingRule rule);
    int RemoveVideoRoutingRule(VideoRoutingRule rule);
    int ClearVideoRoutingRules();
}

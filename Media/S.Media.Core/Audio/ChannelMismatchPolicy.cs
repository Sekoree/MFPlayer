namespace S.Media.Core.Audio;

/// <summary>
/// Determines how <see cref="AudioResampler"/> handles a channel-count mismatch
/// between source and target when the route map does not fully cover the difference.
/// </summary>
public enum ChannelMismatchPolicy
{
    /// <summary>Extra source channels are discarded; missing channels are zero-filled.</summary>
    Drop = 0,

    /// <summary>All source channels are mixed down to stereo (L = even averaged, R = odd averaged).</summary>
    MixToStereo = 1,

    /// <summary>All source channels are summed and divided by count into a single mono channel.</summary>
    MixToMono = 2,

    /// <summary>Returns an error when source channels exceed target channels.</summary>
    Fail = 3,
}


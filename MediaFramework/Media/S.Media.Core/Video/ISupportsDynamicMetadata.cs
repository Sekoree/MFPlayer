using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Optional capability on a video endpoint: allows the router or host application
/// to push dynamic stream metadata hints (for example, an upcoming format/fps change)
/// before frames arrive.
/// </summary>
public interface ISupportsDynamicMetadata
{
    /// <summary>
    /// Announces an expected source video format transition. This is advisory and
    /// may be ignored/overridden by the actual incoming frame data.
    /// </summary>
    void AnnounceUpcomingVideoFormat(VideoFormat format);

    /// <summary>
    /// Applies an advisory source frame-rate hint as a rational fraction.
    /// Values are expected to be positive and non-zero.
    /// </summary>
    void ApplyVideoFpsHint(int numerator, int denominator);
}

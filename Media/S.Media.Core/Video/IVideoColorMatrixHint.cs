namespace S.Media.Core.Video;

/// <summary>
/// Optional hint interface for sources that can suggest YUV color matrix selection.
/// </summary>
public interface IVideoColorMatrixHint
{
    YuvColorMatrix SuggestedYuvColorMatrix { get; }

    /// <summary>
    /// Optional YUV range hint for shader normalization policy.
    /// </summary>
    YuvColorRange SuggestedYuvColorRange => YuvColorRange.Auto;
}


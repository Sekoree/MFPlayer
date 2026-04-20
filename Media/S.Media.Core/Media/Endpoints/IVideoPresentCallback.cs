namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Implemented by the graph. Set on <see cref="IPullVideoEndpoint"/> at registration time.
/// The endpoint calls this from its render loop.
/// </summary>
public interface IVideoPresentCallback
{
    /// <summary>
    /// Attempts to get the next video frame for presentation.
    /// Returns <see langword="true"/> if a frame is available; <see langword="false"/> otherwise.
    /// Uses out parameter to avoid nullable struct boxing.
    /// </summary>
    bool TryPresentNext(TimeSpan clockPosition, out VideoFrame frame);
}


namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Optional capability on video endpoints: the endpoint pulls frames
/// from a render loop rather than having them pushed.
/// SDL3/Avalonia outputs implement this.
/// </summary>
public interface IPullVideoEndpoint : IVideoEndpoint
{
    /// <summary>
    /// The graph sets this when the endpoint is registered.
    /// The endpoint calls it from its render loop to pull a frame.
    /// </summary>
    IVideoPresentCallback? PresentCallback { get; set; }
}


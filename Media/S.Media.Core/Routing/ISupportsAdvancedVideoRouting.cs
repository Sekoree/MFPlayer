namespace S.Media.Core.Routing;

public interface ISupportsAdvancedVideoRouting
{
    int AddRoute(VideoRoute route);

    int RemoveRoute(VideoRoute route);

    int UpdateRoute(VideoRoute route);

    IReadOnlyList<VideoRoute> Routes { get; }
}


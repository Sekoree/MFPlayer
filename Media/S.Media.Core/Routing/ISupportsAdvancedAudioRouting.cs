namespace S.Media.Core.Routing;

public interface ISupportsAdvancedAudioRouting
{
    int AddRoute(AudioRoute route);

    int RemoveRoute(AudioRoute route);

    int UpdateRoute(AudioRoute route);

    IReadOnlyList<AudioRoute> Routes { get; }
}


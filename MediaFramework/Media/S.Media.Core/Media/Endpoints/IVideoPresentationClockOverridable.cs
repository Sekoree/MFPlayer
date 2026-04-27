namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Pull video outputs whose presentation loop can follow a shared master
/// <see cref="S.Media.Core.IMediaClock"/> (typically <see cref="S.Media.Core.Routing.IAVRouter.Clock"/>)
/// for PTS gating, instead of the endpoint's own <see cref="S.Media.Core.Clock.VideoPtsClock"/> alone.
/// </summary>
public interface IVideoPresentationClockOverridable
{
    /// <summary>
    /// Drives the pull callback's <c>TryPresentNext(clockPosition, …)</c> with this clock's
    /// <see cref="S.Media.Core.IMediaClock.Position"/>. Pass <see langword="null"/> to use the
    /// endpoint's default video PTS clock only.
    /// </summary>
    void OverridePresentationClock(IMediaClock? clock);
}

namespace S.Media.Core.Video;

/// <summary>
/// Capability marker for an <see cref="IVideoOutput"/> whose <see cref="IVideoOutput.Submit"/> only
/// transfers ownership into an in-memory hand-off and returns promptly. Routers and composition fan-out
/// use it to avoid adding a second worker queue in front of an output that already owns its scheduling.
/// </summary>
public interface INonBlockingVideoOutput : IVideoOutput
{
}

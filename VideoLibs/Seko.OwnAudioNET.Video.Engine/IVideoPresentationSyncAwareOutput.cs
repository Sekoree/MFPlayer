namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Optional capability for <see cref="IVideoOutput"/> implementations that can honor
/// transport-level presentation sync preferences.
/// </summary>
public interface IVideoPresentationSyncAwareOutput
{
    /// <summary>
    /// Best-effort presentation policy requested by the active transport.
    /// </summary>
    VideoTransportPresentationSyncMode PresentationSyncMode { get; set; }
}


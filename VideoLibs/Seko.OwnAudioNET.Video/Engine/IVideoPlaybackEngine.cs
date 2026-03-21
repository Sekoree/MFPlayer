using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Standalone playback engine for one or more <see cref="IVideoSource"/> instances.
/// Owns playback clock publication, source attachment, and timeline operations without requiring an audio engine.
/// </summary>
internal interface IVideoPlaybackEngine : IDisposable
{
    /// <summary>Playback configuration used by the engine.</summary>
    VideoEngineConfig Config { get; }

    /// <summary>Clock published to attached video sources.</summary>
    IVideoClock Clock { get; }

    /// <summary>Current timeline position in seconds.</summary>
    double Position { get; }

    /// <summary><see langword="true"/> when the transport is advancing.</summary>
    bool IsRunning { get; }

    /// <summary>Number of currently registered video sources.</summary>
    int SourceCount { get; }

    /// <summary>Raised when a registered source reports an error.</summary>
    event EventHandler<VideoErrorEventArgs>? SourceError;

    /// <summary>Adds a source to the engine and attaches it to the shared clock.</summary>
    bool AddVideoSource(IVideoSource source);

    /// <summary>OwnAudio-style alias for <see cref="AddVideoSource"/>.</summary>
    bool AddSource(IVideoSource source);

    /// <summary>Removes a source from the engine.</summary>
    bool RemoveVideoSource(IVideoSource source);

    /// <summary>Removes a source by ID.</summary>
    bool RemoveVideoSource(Guid sourceId);

    /// <summary>OwnAudio-style alias for <see cref="RemoveVideoSource(IVideoSource)"/>.</summary>
    bool RemoveSource(IVideoSource source);

    /// <summary>OwnAudio-style alias for <see cref="RemoveVideoSource(Guid)"/>.</summary>
    bool RemoveSource(Guid sourceId);

    /// <summary>Returns a snapshot of all registered sources.</summary>
    IVideoSource[] GetVideoSources();

    /// <summary>OwnAudio-style alias for <see cref="GetVideoSources"/>.</summary>
    IVideoSource[] GetSources();

    /// <summary>Removes all registered sources.</summary>
    void ClearVideoSources();

    /// <summary>OwnAudio-style alias for <see cref="ClearVideoSources"/>.</summary>
    void ClearSources();


    /// <summary>Starts or resumes playback transport.</summary>
    void Start();

    /// <summary>Pauses playback transport.</summary>
    void Pause();

    /// <summary>Stops playback transport and rewinds all sources.</summary>
    void Stop();

    /// <summary>Seeks the shared timeline.</summary>
    void Seek(double positionInSeconds);

    /// <summary>
    /// Seeks the shared timeline. When <paramref name="safeSeek"/> is enabled, playback pauses
    /// for the seek operation and resumes afterwards if it had been running.
    /// </summary>
    void Seek(double positionInSeconds, bool safeSeek);
}




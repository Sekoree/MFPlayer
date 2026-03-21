using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// OwnAudio-style video mixer facade for synchronized FFmpeg-backed video source playback.
/// </summary>
public interface IVideoMixer : IDisposable
{
    /// <summary>Playback configuration used by the underlying shared transport.</summary>
    VideoEngineConfig Config { get; }

    /// <summary>Clock published to registered sources.</summary>
    IVideoClock Clock { get; }

    /// <summary>Current shared timeline position in seconds.</summary>
    double Position { get; }

    /// <summary>Optional external timeline clock used for position reporting/sync diagnostics.</summary>
    IExternalClock? ExternalClock { get; }

    /// <summary><see langword="true"/> when the mixer transport is advancing.</summary>
    bool IsRunning { get; }

    /// <summary>Number of registered FFmpeg-backed video sources.</summary>
    int SourceCount { get; }

    /// <summary>Currently active source rendered through the attached engine, or <see langword="null"/>.</summary>
    VideoStreamSource? ActiveSource { get; }


    /// <summary>Raised when a registered source reports an error.</summary>
    event EventHandler<VideoErrorEventArgs>? SourceError;

    /// <summary>Raised whenever the active source selection changes.</summary>
    event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveSourceChanged;

    /// <summary>Adds an FFmpeg-backed source to the shared transport.</summary>
    bool AddSource(VideoStreamSource source);

    /// <summary>Removes a source from the mixer.</summary>
    bool RemoveSource(VideoStreamSource source);

    /// <summary>Returns a snapshot of all registered sources.</summary>
    VideoStreamSource[] GetSources();

    /// <summary>Removes all sources.</summary>
    void ClearSources();

    /// <summary>Selects the source currently rendered by the attached engine.</summary>
    bool SetActiveSource(VideoStreamSource source);

    /// <summary>Starts or resumes playback transport.</summary>
    void Start();

    /// <summary>Pauses playback transport.</summary>
    void Pause();

    /// <summary>Stops playback transport and rewinds all sources.</summary>
    void Stop();

    /// <summary>Seeks the shared timeline.</summary>
    void Seek(double positionInSeconds);

    /// <summary>Seeks the shared timeline with optional pause/resume safety.</summary>
    void Seek(double positionInSeconds, bool safeSeek);
}


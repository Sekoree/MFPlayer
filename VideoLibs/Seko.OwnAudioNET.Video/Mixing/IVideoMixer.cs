using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// OwnAudio-style video mixer facade for synchronized FFmpeg-backed video source playback
/// and explicit output routing.
/// </summary>
public interface IVideoMixer : IDisposable
{
    /// <summary>Playback configuration used by the underlying shared transport.</summary>
    VideoTransportEngineConfig Config { get; }

    /// <summary>Clock published to registered sources.</summary>
    IVideoClock Clock { get; }

    /// <summary>Current shared timeline position in seconds.</summary>
    double Position { get; }

    /// <summary><see langword="true"/> when the mixer transport is advancing.</summary>
    bool IsRunning { get; }

    /// <summary>Number of registered FFmpeg-backed video sources.</summary>
    int SourceCount { get; }

    /// <summary>Number of registered outputs.</summary>
    int OutputCount { get; }

    /// <summary>Raised when a registered source reports an error.</summary>
    event EventHandler<VideoErrorEventArgs>? SourceError;

    /// <summary>Raised whenever an output changes from one source binding to another.</summary>
    event EventHandler<VideoOutputSourceChangedEventArgs>? OutputSourceChanged;

    /// <summary>Adds an FFmpeg-backed source to the shared transport.</summary>
    bool AddSource(FFVideoSource source);

    /// <summary>Removes a source from the mixer and detaches any outputs bound to it.</summary>
    bool RemoveSource(FFVideoSource source);

    /// <summary>Returns a snapshot of all registered sources.</summary>
    FFVideoSource[] GetSources();

    /// <summary>Removes all sources.</summary>
    void ClearSources();

    /// <summary>Adds an output to the mixer.</summary>
    bool AddOutput(IVideoOutput output);

    /// <summary>Removes an output from the mixer.</summary>
    bool RemoveOutput(IVideoOutput output);

    /// <summary>Returns a snapshot of all registered outputs.</summary>
    IVideoOutput[] GetOutputs();

    /// <summary>Removes all outputs.</summary>
    void ClearOutputs();

    /// <summary>Binds an output to a specific source.</summary>
    bool BindOutputToSource(IVideoOutput output, FFVideoSource source);

    /// <summary>Detaches the currently bound source from an output.</summary>
    bool UnbindOutput(IVideoOutput output);

    /// <summary>Returns all outputs currently bound to a source.</summary>
    IVideoOutput[] GetOutputsForSource(FFVideoSource source);

    /// <summary>Returns the source currently bound to an output, or <see langword="null"/>.</summary>
    FFVideoSource? GetSourceForOutput(IVideoOutput output);

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


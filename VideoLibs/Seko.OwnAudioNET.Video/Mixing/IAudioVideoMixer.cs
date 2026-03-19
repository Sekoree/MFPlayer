using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Coordinates audio and video mixers so both domains share one synchronized playback timeline.
/// </summary>
public interface IAudioVideoMixer : IDisposable
{
    /// <summary>The owned or attached audio mixer.</summary>
    AudioMixer AudioMixer { get; }

    /// <summary>The owned or attached video mixer.</summary>
    IVideoMixer VideoMixer { get; }

    /// <summary>The master clock driven by the audio mixer.</summary>
    MasterClock MasterClock { get; }

    /// <summary>The current synchronized A/V timeline position in seconds.</summary>
    double Position { get; }

    /// <summary><see langword="true"/> when either side of the combined mixer is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Number of audio sources currently registered.</summary>
    int AudioSourceCount { get; }

    /// <summary>Number of video sources currently registered.</summary>
    int VideoSourceCount { get; }

    /// <summary>Number of video outputs currently registered.</summary>
    int VideoOutputCount { get; }

    /// <summary>Raised when a registered audio source reports an error.</summary>
    event EventHandler<AudioErrorEventArgs>? AudioSourceError;

    /// <summary>Raised when a registered video source reports an error.</summary>
    event EventHandler<VideoErrorEventArgs>? VideoSourceError;

    /// <summary>Raised whenever a video output changes its bound source.</summary>
    event EventHandler<VideoOutputSourceChangedEventArgs>? VideoOutputSourceChanged;

    /// <summary>Adds an audio source to the combined mixer.</summary>
    bool AddAudioSource(IAudioSource source);

    /// <summary>Removes an audio source from the combined mixer.</summary>
    bool RemoveAudioSource(IAudioSource source);

    /// <summary>Returns a snapshot of all registered audio sources.</summary>
    IAudioSource[] GetAudioSources();

    /// <summary>Removes all registered audio sources.</summary>
    void ClearAudioSources();

    /// <summary>Adds a video source to the combined mixer.</summary>
    bool AddVideoSource(FFVideoSource source);

    /// <summary>Removes a video source from the combined mixer.</summary>
    bool RemoveVideoSource(FFVideoSource source);

    /// <summary>Returns a snapshot of all registered video sources.</summary>
    FFVideoSource[] GetVideoSources();

    /// <summary>Removes all registered video sources.</summary>
    void ClearVideoSources();

    /// <summary>Adds a video output sink.</summary>
    bool AddVideoOutput(IVideoOutput output);

    /// <summary>Removes a video output sink.</summary>
    bool RemoveVideoOutput(IVideoOutput output);

    /// <summary>Returns a snapshot of all registered video outputs.</summary>
    IVideoOutput[] GetVideoOutputs();

    /// <summary>Removes all registered video outputs.</summary>
    void ClearVideoOutputs();

    /// <summary>Binds a video output to a specific video source.</summary>
    bool BindVideoOutputToSource(IVideoOutput output, FFVideoSource source);

    /// <summary>Detaches the current source from a video output.</summary>
    bool UnbindVideoOutput(IVideoOutput output);

    /// <summary>Returns all outputs currently bound to a given video source.</summary>
    IVideoOutput[] GetVideoOutputsForSource(FFVideoSource source);

    /// <summary>Returns the currently bound video source for an output, or <see langword="null"/>.</summary>
    FFVideoSource? GetVideoSourceForOutput(IVideoOutput output);

    /// <summary>Starts synchronized audio/video playback.</summary>
    void Start();

    /// <summary>Pauses synchronized audio/video playback.</summary>
    void Pause();

    /// <summary>Stops synchronized audio/video playback.</summary>
    void Stop();

    /// <summary>Seeks the shared synchronized A/V timeline.</summary>
    void Seek(double positionInSeconds);
}


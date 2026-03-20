using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;
using Seko.OwnAudioNET.Video.Clocks;

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

    /// <summary>Optional external timeline clock used to drive sync position reporting.</summary>
    IExternalClock? ExternalClock { get; }

    /// <summary><see langword="true"/> when either side of the combined mixer is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Number of audio sources currently registered.</summary>
    int AudioSourceCount { get; }

    /// <summary>Number of video sources currently registered.</summary>
    int VideoSourceCount { get; }


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
    bool AddVideoSource(VideoStreamSource source);

    /// <summary>Removes a video source from the combined mixer.</summary>
    bool RemoveVideoSource(VideoStreamSource source);

    /// <summary>Returns a snapshot of all registered video sources.</summary>
    VideoStreamSource[] GetVideoSources();

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
    bool BindVideoOutputToSource(IVideoOutput output, VideoStreamSource source);

    /// <summary>Detaches the current source from a video output.</summary>
    bool UnbindVideoOutput(IVideoOutput output);

    /// <summary>Returns all outputs currently bound to a given video source.</summary>
    IVideoOutput[] GetVideoOutputsForSource(VideoStreamSource source);

    /// <summary>Returns the currently bound video source for an output, or <see langword="null"/>.</summary>
    VideoStreamSource? GetVideoSourceForOutput(IVideoOutput output);

    /// <summary>Starts synchronized audio/video playback.</summary>
    void Start();

    /// <summary>Pauses synchronized audio/video playback.</summary>
    void Pause();

    /// <summary>Stops synchronized audio/video playback.</summary>
    void Stop();

    /// <summary>Seeks the shared synchronized A/V timeline using <see cref="AudioVideoSeekMode.Auto"/>.</summary>
    void Seek(double positionInSeconds);

    /// <summary>
    /// Seeks the shared synchronized A/V timeline using the requested seek policy.
    /// </summary>
    void Seek(double positionInSeconds, AudioVideoSeekMode seekMode);
}

/// <summary>How video seek operations should be executed inside <see cref="IAudioVideoMixer.Seek(double,AudioVideoSeekMode)"/>.</summary>
public enum AudioVideoSeekMode
{
    /// <summary>Use fast seek for forward jumps and safe seek for backward jumps while running.</summary>
    Auto = 0,

    /// <summary>Always use fast seek (no pause/resume protection in transport).</summary>
    Fast = 1,

    /// <summary>Always use safe seek (pause/resume around timeline seek while running).</summary>
    Safe = 2
}


using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Video-only mixer facade that mirrors OwnAudio-style source/output lifecycle operations.
/// Uses a dedicated <see cref="IVideoTransportEngine"/> transport internally.
/// </summary>
public interface IVideoMixer : IDisposable
{
    VideoTransportEngineConfig Config { get; }

    IVideoClock Clock { get; }

    double Position { get; }

    bool IsRunning { get; }

    int SourceCount { get; }

    int OutputCount { get; }

    event EventHandler<VideoErrorEventArgs>? SourceError;

    bool AddSource(IVideoSource source);

    bool RemoveSource(IVideoSource source);

    bool RemoveSource(Guid sourceId);

    IVideoSource[] GetSources();

    void ClearSources();

    bool AddOutput(IVideoOutput output);

    bool RemoveOutput(IVideoOutput output);

    bool RemoveOutput(Guid outputId);

    IVideoOutput[] GetOutputs();

    void ClearOutputs();

    void Start();

    void Pause();

    void Stop();

    void Seek(double positionInSeconds);

    void Seek(double positionInSeconds, bool safeSeek);
}


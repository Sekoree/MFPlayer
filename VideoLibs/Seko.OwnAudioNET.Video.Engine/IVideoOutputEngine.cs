using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Output-sink engine that routes pushed frames to a selected <see cref="IVideoOutput"/>.
/// </summary>
public interface IVideoOutputEngine : IDisposable
{
    VideoEngineConfig Config { get; }

    int OutputCount { get; }

    Guid? CurrentOutputId { get; }

    IVideoOutput? CurrentOutput { get; }

    bool AddOutput(IVideoOutput output);

    bool RemoveOutput(IVideoOutput output);

    bool RemoveOutput(Guid outputId);

    IVideoOutput[] GetOutputs();

    void ClearOutputs();

    bool SetCurrentOutput(IVideoOutput output);

    bool SetCurrentOutput(Guid outputId);

    void ClearCurrentOutput();

    bool PushFrame(VideoFrame frame, double masterTimestamp);
}



using Seko.OwnAudioNET.Video.Events;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Core video engine contract used by direct decode and mixer-driven flows.
/// </summary>
public interface IVideoEngine : IDisposable
{
    VideoEngineConfig Config { get; }

    int OutputCount { get; }

    Guid? CurrentOutputId { get; }

    IVideoOutput? CurrentOutput { get; }

    event EventHandler<VideoErrorEventArgs>? Error;

    bool AddOutput(IVideoOutput output);

    bool RemoveOutput(IVideoOutput output);

    bool RemoveOutput(Guid outputId);

    IVideoOutput[] GetOutputs();

    void ClearOutputs();

    bool PushFrame(VideoFrame frame, double masterTimestamp);
}

/// <summary>
/// Optional capability implemented by engines that can switch their active output at runtime.
/// </summary>
public interface ISupportsOutputSwitching
{
    event EventHandler<VideoOutputChangedEventArgs>? VideoOutputChanged;

    bool SetVideoOutput(IVideoOutput output, VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch);

    bool SetVideoOutput(Guid outputId, VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch);

    bool ClearVideoOutput(VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch);
}

public enum VideoOutputSwitchMode
{
    PauseAndSwitch = 0,
    HotSwap = 1
}

public sealed class VideoOutputChangedEventArgs : EventArgs
{
    public VideoOutputChangedEventArgs(IVideoOutput? oldOutput, IVideoOutput? newOutput)
    {
        OldOutput = oldOutput;
        NewOutput = newOutput;
    }

    public IVideoOutput? OldOutput { get; }

    public IVideoOutput? NewOutput { get; }
}


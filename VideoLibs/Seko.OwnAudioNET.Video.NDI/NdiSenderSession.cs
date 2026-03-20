using NdiLib;

namespace Seko.OwnAudioNET.Video.NDI;

internal sealed class NdiSenderSession : IDisposable
{
    private readonly NdiSender _sender;
    private readonly Lock _sendLock = new();
    private bool _disposed;

    public NdiSenderSession(NdiEngineConfig config)
    {
        _sender = new NdiSender(config.SenderName, config.Groups, config.ClockVideo, config.ClockAudio);
    }

    public void SendVideo(in NdiVideoFrameV2 frame)
    {
        lock (_sendLock)
            _sender.SendVideo(frame);
    }

    public void SendAudio(in NdiAudioFrameV3 frame)
    {
        lock (_sendLock)
            _sender.SendAudio(frame);
    }

    public int GetConnectionCount(uint timeoutMs)
    {
        lock (_sendLock)
            return _sender.GetConnectionCount(timeoutMs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _sender.Dispose();
        _disposed = true;
    }
}


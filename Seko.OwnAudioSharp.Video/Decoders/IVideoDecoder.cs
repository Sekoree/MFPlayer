namespace Seko.OwnAudioSharp.Video.Decoders;

public interface IVideoDecoder : IDisposable
{
    VideoStreamInfo StreamInfo { get; }
    bool IsEndOfStream { get; }
    bool IsHardwareDecoding { get; }

    bool TryDecodeNextFrame(out VideoFrame frame, out string? error);
    bool TrySeek(TimeSpan position, out string error);
}
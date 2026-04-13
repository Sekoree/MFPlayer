using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Public aggregate wrapper around <see cref="FFmpegDecoder"/> that exposes
/// audio/video channel collections and coordinated lifecycle methods.
/// </summary>
public sealed class FFmpegAVChannel : IDisposable
{
    private readonly FFmpegDecoder _decoder;

    public IReadOnlyList<IAudioChannel> AudioChannels => _decoder.AudioChannels;
    public IReadOnlyList<IVideoChannel> VideoChannels => _decoder.VideoChannels;

    private FFmpegAVChannel(FFmpegDecoder decoder)
    {
        _decoder = decoder;
    }

    public static FFmpegAVChannel Open(string path, FFmpegDecoderOptions? options = null)
        => new(FFmpegDecoder.Open(path, options));

    public static FFmpegAVChannel Open(Stream stream, FFmpegDecoderOptions? options = null, bool leaveOpen = false)
        => new(FFmpegDecoder.Open(stream, options, leaveOpen));

    public void Start() => _decoder.Start();

    public void Seek(TimeSpan position) => _decoder.Seek(position);

    public FFmpegDecoder.DiagnosticsSnapshot GetDiagnosticsSnapshot() => _decoder.GetDiagnosticsSnapshot();

    public void Dispose() => _decoder.Dispose();
}


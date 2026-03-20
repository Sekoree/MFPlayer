using Seko.OwnAudioNET.Video.Decoders;

namespace Seko.OwnAudioNET.Video.Sources;

/// <summary>Tuning options for <see cref="VideoStreamSource"/>.</summary>
public sealed class VideoStreamSourceOptions
{
    /// <summary>
    /// When <see langword="true"/>, keeps the latest successfully presented frame visible after
    /// EOS or when a seek position cannot immediately provide a new frame. Default: <see langword="true"/>.
    /// </summary>
    public bool HoldLastFrameOnEndOfStream { get; init; } = true;


    /// <summary>Decoder options forwarded to <see cref="FFVideoDecoder"/> when the source owns the decoder.</summary>
    public FFVideoDecoderOptions DecoderOptions { get; init; } = new();
}


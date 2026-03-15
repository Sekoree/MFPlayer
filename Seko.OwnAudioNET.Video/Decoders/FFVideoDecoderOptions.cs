namespace Seko.OwnAudioNET.Video.Decoders;

/// <summary>Tuning options forwarded to <see cref="FFVideoDecoder"/> at construction time.</summary>
public sealed class FFVideoDecoderOptions
{
    /// <summary>
    /// Optional explicit stream index to decode. When <see langword="null"/>, FFmpeg selects the best video stream.
    /// </summary>
    public int? PreferredStreamIndex { get; init; }

    /// <summary>
    /// Attempt to open a hardware-accelerated decoder. Falls back to software decoding
    /// transparently if no compatible device is found. Default: <see langword="true"/>.
    /// </summary>
    public bool EnableHardwareDecoding { get; init; } = true;

    /// <summary>
    /// Optional hint for the preferred hardware device API (e.g. <c>"vaapi"</c>, <c>"cuda"</c>,
    /// <c>"d3d11va"</c>). When <see langword="null"/> or empty the decoder probes for the best
    /// available device automatically. Default: <see langword="null"/>.
    /// </summary>
    public string? PreferredHardwareDevice { get; init; }

    /// <summary>
    /// Number of threads used for software decoding and packet/frame processing.
    /// <c>0</c> lets FFmpeg choose automatically. Default: <c>0</c>.
    /// </summary>
    public int ThreadCount { get; init; } = 0;
}

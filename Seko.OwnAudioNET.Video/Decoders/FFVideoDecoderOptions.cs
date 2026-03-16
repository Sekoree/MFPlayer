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

    /// <summary>
    /// Preferred decoder output formats in priority order. The decoder picks the first supported
    /// entry, optionally preferring source-native output when that format is also listed.
    /// Default: <see cref="VideoPixelFormat.Rgba32"/>.
    /// </summary>
    public IReadOnlyList<VideoPixelFormat> PreferredOutputPixelFormats { get; init; } =
        [VideoPixelFormat.Rgba32];

    /// <summary>
    /// When <see langword="true"/>, and the decoded source format maps to a listed preferred output
    /// format, the decoder keeps that source format to avoid a conversion pass.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool PreferSourcePixelFormatWhenSupported { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, the decoder chooses the preferred output format with the lowest
    /// estimated conversion cost for the current source pixel format. This helps keep expensive
    /// conversions (for example to RGBA) off the hot path when a YUV output is available.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool PreferLowestConversionCost { get; init; } = true;
}

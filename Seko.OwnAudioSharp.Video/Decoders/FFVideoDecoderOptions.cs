namespace Seko.OwnAudioSharp.Video.Decoders;

public sealed class FFVideoDecoderOptions
{
    // Best-effort hardware decode. If setup fails, decoder falls back to software.
    public bool EnableHardwareDecoding { get; init; } = true;

    // Optional hint (for example: "vaapi", "cuda", "d3d11va"). Empty means auto-probe.
    public string? PreferredHardwareDevice { get; init; }

    // Decoder threading for software fallback and packet/frame processing.
    public int ThreadCount { get; init; } = 0;
}


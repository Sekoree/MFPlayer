namespace S.Media.Core.Media;

/// <summary>A single decoded video frame carried through the pipeline.</summary>
public readonly record struct VideoFrame(
    int                  Width,
    int                  Height,
    PixelFormat          PixelFormat,
    ReadOnlyMemory<byte> Data,
    TimeSpan             Pts);


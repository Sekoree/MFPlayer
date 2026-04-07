namespace S.Media.Core.Media;

/// <summary>
/// A single decoded video frame carried through the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="MemoryOwner"/> is non-null, the frame's pixel data was rented from
/// <c>ArrayPool&lt;byte&gt;</c> by the decoder. The consumer <b>must</b> call
/// <c>MemoryOwner.Dispose()</c> once they have finished reading <see cref="Data"/>, so
/// the rental can be returned to the pool.
/// </para>
/// <para>
/// If <see cref="MemoryOwner"/> is null, the data is heap-allocated and GC-managed;
/// no explicit disposal is required.
/// </para>
/// </remarks>
public readonly record struct VideoFrame(
    int                  Width,
    int                  Height,
    PixelFormat          PixelFormat,
    ReadOnlyMemory<byte> Data,
    TimeSpan             Pts,
    IDisposable?         MemoryOwner = null);


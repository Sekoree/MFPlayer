namespace S.Media.Core.Media;

/// <summary>
/// A single decoded video frame carried through the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="MemoryOwner"/> is non-null, the frame's pixel data was rented from
/// <c>ArrayPool&lt;byte&gt;</c> (or similar).  Ownership semantics:
/// </para>
/// <list type="bullet">
/// <item>
/// The <b>producer</b> (decoder / capture) and the <b>router</b> own the frame while it
/// is in flight through the routing graph.  The router disposes the owner after fan-out
/// to all endpoints has completed.
/// </item>
/// <item>
/// <b>Endpoints must NOT dispose <see cref="MemoryOwner"/></b> of a frame passed to
/// <c>ReceiveFrame(in VideoFrame)</c>.  The same frame may be fanned out to multiple
/// endpoints, or re-presented by a pull endpoint.  Endpoints that need to retain the
/// pixel data past the call <b>must copy</b> it into a buffer they own themselves.
/// </item>
/// </list>
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

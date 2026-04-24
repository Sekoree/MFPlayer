using System.Runtime.CompilerServices;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Explicit ref-counted handle over a <see cref="VideoFrame"/>. Introduced by
/// Implementation-Checklist §3.11 / review tag B15+B16+R18+CH7 to replace the
/// fragile "router disposes <see cref="VideoFrame.MemoryOwner"/> after
/// <c>ReceiveFrame</c>" contract with a Retain/Release API that endpoints can
/// opt into per-fan-out leg.
///
/// <para><b>Ownership model:</b></para>
/// <list type="number">
///   <item>The producer creates a <see cref="VideoFrame"/> whose
///         <see cref="VideoFrame.MemoryOwner"/> is a
///         <see cref="RefCountedVideoBuffer"/>. The constructor starts the
///         refcount at 1 — that ref is the producer's (enqueued in the
///         subscription). Each additional subscriber adds one via
///         <see cref="RefCountedVideoBuffer.Retain"/>.</item>
///   <item>The router wraps the dequeued <see cref="VideoFrame"/> in a
///         <see cref="VideoFrameHandle"/> and passes it to
///         <see cref="Media.Endpoints.IVideoEndpoint.ReceiveFrame(in VideoFrameHandle)"/>.
///         After the call returns the router calls <see cref="Release"/> once
///         — that drops the router's implicit ref, which is the one every
///         endpoint has always been forbidden from disposing.</item>
///   <item>An endpoint that needs to keep the pixel data alive past the
///         <c>ReceiveFrame</c> call (e.g. async GPU upload, frame-clone fast
///         path — §3.38) calls <see cref="Retain"/> inside the call and
///         exactly one matching <see cref="Release"/> later on its own
///         schedule. Endpoints that copy the bytes into their own buffer
///         before returning do nothing — the router's release path will
///         return the rental to the pool.</item>
/// </list>
///
/// <para>
/// Legacy endpoints that still implement only
/// <c>ReceiveFrame(in VideoFrame)</c> are driven through the default interface
/// implementation, which unwraps <see cref="Frame"/> and forwards. Their
/// "don't touch MemoryOwner" contract is unchanged.
/// </para>
///
/// <para>
/// <b>Non-ref-counted frames.</b> If the producing frame does not use a
/// <see cref="RefCountedVideoBuffer"/> (heap-managed buffers, legacy test
/// fixtures), <see cref="Retain"/> throws
/// <see cref="InvalidOperationException"/> — the pipeline would have no way
/// to keep the rental alive. <see cref="Release"/> still falls back to
/// disposing the raw <see cref="VideoFrame.MemoryOwner"/> so the
/// router's single implicit release keeps working.
/// </para>
/// </summary>
public readonly struct VideoFrameHandle : IEquatable<VideoFrameHandle>
{
    /// <summary>The underlying frame descriptor. Always valid for the
    /// duration of the current ref-count window owned by the reader.</summary>
    public VideoFrame Frame { get; }

    /// <summary>
    /// Ref-counted backing buffer, or <see langword="null"/> if the frame was
    /// produced with a non-ref-counted owner (legacy path).
    /// </summary>
    internal RefCountedVideoBuffer? RefBuffer { get; }

    /// <summary>Creates a handle that wraps <paramref name="frame"/>.
    /// Does <b>not</b> take an additional refcount — the caller is assumed
    /// to already hold one (the producer's initial ref, or a previous
    /// <see cref="Retain"/>).</summary>
    public VideoFrameHandle(in VideoFrame frame)
    {
        Frame = frame;
        RefBuffer = frame.MemoryOwner as RefCountedVideoBuffer;
    }

    // ── Forwarding accessors (pure ergonomics) ──────────────────────────

    public int                  Width       => Frame.Width;
    public int                  Height      => Frame.Height;
    public PixelFormat          PixelFormat => Frame.PixelFormat;
    public ReadOnlyMemory<byte> Data        => Frame.Data;
    public TimeSpan             Pts         => Frame.Pts;

    /// <summary>
    /// <see langword="true"/> when the handle carries a real frame. Zero-sized /
    /// <c>default</c> handles return <see langword="false"/>.
    /// </summary>
    public bool IsValid => Frame.Width > 0 && Frame.Height > 0;

    /// <summary>
    /// <see langword="true"/> when the frame is backed by a
    /// <see cref="RefCountedVideoBuffer"/> and <see cref="Retain"/> would
    /// therefore succeed. Endpoints that support a zero-copy fast path
    /// (clone sinks — §3.38, async GPU upload) gate on this before calling
    /// <see cref="Retain"/>. Non-ref-counted frames MUST be copied during
    /// the <c>ReceiveFrame</c> call.
    /// </summary>
    public bool IsRefCounted => RefBuffer is not null;

    /// <summary>
    /// Increments the underlying refcount so the pool rental survives past
    /// the current <c>ReceiveFrame</c> call. The caller MUST call
    /// <see cref="Release"/> exactly once per <see cref="Retain"/>.
    ///
    /// <para>
    /// Returns the same handle for fluent chaining, e.g.
    /// <c>_pending.Enqueue(handle.Retain());</c>.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the frame is not ref-counted (<see cref="RefBuffer"/>
    /// is <see langword="null"/>). Non-ref-counted frames cannot be retained
    /// past the call — endpoints must copy the bytes instead.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VideoFrameHandle Retain()
    {
        if (RefBuffer is null)
            throw new InvalidOperationException(
                "VideoFrameHandle.Retain() requires a RefCountedVideoBuffer-backed frame. " +
                "Endpoints that receive non-ref-counted frames must copy the pixel data " +
                "before returning from ReceiveFrame.");
        RefBuffer.Retain();
        return this;
    }

    /// <summary>
    /// Decrements the refcount (ref-counted frames) or disposes the legacy
    /// <see cref="VideoFrame.MemoryOwner"/> (non-ref-counted frames). Safe to
    /// call exactly once per owned reference — over-release throws via
    /// <see cref="RefCountedVideoBuffer.Release"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        if (RefBuffer is not null)
            RefBuffer.Release();
        else
            Frame.MemoryOwner?.Dispose();
    }

    /// <summary>
    /// Reference-equality test on the backing buffer. Two handles compare equal
    /// when they describe the same pool rental (used by endpoints' texture-reuse
    /// gates — §3.33).
    /// </summary>
    public bool Equals(VideoFrameHandle other)
    {
        if (RefBuffer is not null || other.RefBuffer is not null)
            return ReferenceEquals(RefBuffer, other.RefBuffer);
        return ReferenceEquals(Frame.MemoryOwner, other.Frame.MemoryOwner);
    }

    public override bool Equals(object? obj) => obj is VideoFrameHandle h && Equals(h);

    public override int GetHashCode()
    {
        object? key = (object?)RefBuffer ?? Frame.MemoryOwner;
        return key is null ? 0 : RuntimeHelpers.GetHashCode(key);
    }

    public static bool operator ==(VideoFrameHandle a, VideoFrameHandle b) => a.Equals(b);
    public static bool operator !=(VideoFrameHandle a, VideoFrameHandle b) => !a.Equals(b);
}


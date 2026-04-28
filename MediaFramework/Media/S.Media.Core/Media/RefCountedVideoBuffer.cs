using System.Collections.Concurrent;

namespace S.Media.Core.Media;

/// <summary>
/// Reference-counted wrapper around a pooled buffer (<see cref="IDisposable"/>). Allows a single
/// decoded <see cref="VideoFrame"/> to be shared by multiple subscribers (a pull endpoint and
/// one or more push endpoints, for example) without per-consumer copies. The underlying rental
/// is returned to its pool the moment the refcount reaches zero.
///
/// <para>
/// <b>Lifecycle:</b> the constructor (or <see cref="Rent"/>) starts the refcount at 1 — that
/// first ref is "consumed" by the producer (e.g. stored in a subscription queue). Each
/// additional subscriber calls <see cref="Retain"/>; when a subscriber drains the frame and
/// is done with the buffer it calls <see cref="Release"/> (or <see cref="Dispose"/>, which
/// aliases <see cref="Release"/>).
/// </para>
///
/// <para>
/// <b>Pooling.</b> §heavy-media-fixes phase 7 — wrapper instances themselves are pooled in a
/// process-wide bounded queue. Decoder hot paths that previously allocated one wrapper per
/// frame (60 alloc/s on 60 fps content; multiplied by clone fan-out) now reuse instances.
/// Callers that already do <c>new RefCountedVideoBuffer(...)</c> keep working — the constructor
/// no longer caches the instance, but <see cref="Rent"/> / <see cref="Release"/> together do.
/// </para>
///
/// <para>
/// <see cref="IDisposable"/> is implemented so existing code using
/// <c>frame.MemoryOwner?.Dispose()</c> keeps working — <see cref="Dispose"/> forwards to
/// <see cref="Release"/>.
/// </para>
/// </summary>
public sealed class RefCountedVideoBuffer : IDisposable
{
    // Bounded pool — decoder fan-outs reuse wrappers across the typical
    // (≤ subscription-count × buffer-depth) working set. Bag is lock-free
    // for the steady-state case; the cap prevents pathological growth if
    // some caller accidentally over-rents without releasing.
    private const int PoolCapacity = 64;
    private static readonly ConcurrentQueue<RefCountedVideoBuffer> s_pool = new();
    private static int s_pooledCount;

    private IDisposable? _inner;
    private int _refs;
    // Tracks whether this instance currently belongs to the pool. Defends
    // against a double-Release rerouting an instance into the pool twice.
    private int _pooled;

    /// <summary>
    /// Creates a new ref-counted wrapper with an initial refcount of 1.
    /// Prefer <see cref="Rent"/> on hot paths so the wrapper itself is
    /// pooled.
    /// </summary>
    /// <param name="inner">The underlying rental (e.g. an <c>ArrayPoolOwner&lt;byte&gt;</c>). May be <see langword="null"/>.</param>
    public RefCountedVideoBuffer(IDisposable? inner)
    {
        _inner = inner;
        _refs = 1;
    }

    /// <summary>
    /// §heavy-media-fixes phase 7 — pooled factory. Returns a wrapper with
    /// refcount 1 around <paramref name="inner"/>. The wrapper is recycled
    /// back to the pool when the refcount reaches zero (via <see cref="Release"/> /
    /// <see cref="Dispose"/>).
    /// </summary>
    public static RefCountedVideoBuffer Rent(IDisposable? inner)
    {
        if (s_pool.TryDequeue(out var w))
        {
            Interlocked.Decrement(ref s_pooledCount);
            w._inner = inner;
            // Refcount must be 0 (set by Release before returning to pool).
            // Going from 0 → 1 here re-arms the instance for a new lifetime.
            Volatile.Write(ref w._refs, 1);
            Volatile.Write(ref w._pooled, 0);
            return w;
        }
        return new RefCountedVideoBuffer(inner);
    }

    /// <summary>Current refcount (for diagnostics / tests only).</summary>
    public int RefCount => Volatile.Read(ref _refs);

    /// <summary>Increments the refcount. Call before publishing to an additional subscriber.</summary>
    public void Retain()
    {
        int r = Interlocked.Increment(ref _refs);
        if (r <= 1)
            throw new ObjectDisposedException(nameof(RefCountedVideoBuffer),
                "Cannot Retain a buffer whose refcount has already reached zero.");
    }

    /// <summary>Decrements the refcount; disposes the inner rental when it reaches zero.</summary>
    public void Release()
    {
        int r = Interlocked.Decrement(ref _refs);
        if (r == 0)
        {
            var inner = _inner;
            _inner = null;
            inner?.Dispose();
            // Recycle the wrapper itself if there's pool headroom and we
            // weren't already pooled (defends against accidental double-
            // release re-pooling the same instance twice).
            if (Interlocked.Exchange(ref _pooled, 1) == 0)
            {
                if (Interlocked.Increment(ref s_pooledCount) <= PoolCapacity)
                {
                    s_pool.Enqueue(this);
                }
                else
                {
                    Interlocked.Decrement(ref s_pooledCount);
                    Volatile.Write(ref _pooled, 0);
                }
            }
        }
        else if (r < 0)
        {
            throw new InvalidOperationException($"{nameof(RefCountedVideoBuffer)} over-released (refs = {r}).");
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Release();
}

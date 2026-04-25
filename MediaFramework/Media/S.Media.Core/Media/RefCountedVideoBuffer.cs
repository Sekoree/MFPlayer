namespace S.Media.Core.Media;

/// <summary>
/// Reference-counted wrapper around a pooled buffer (<see cref="IDisposable"/>). Allows a single
/// decoded <see cref="VideoFrame"/> to be shared by multiple subscribers (a pull endpoint and
/// one or more push endpoints, for example) without per-consumer copies. The underlying rental
/// is returned to its pool the moment the refcount reaches zero.
///
/// <para>
/// <b>Lifecycle:</b> the constructor starts the refcount at 1 — that first ref is "consumed"
/// by the producer (e.g. stored in a subscription queue). Each additional subscriber calls
/// <see cref="Retain"/>; when a subscriber drains the frame and is done with the buffer it
/// calls <see cref="Release"/> (or <see cref="Dispose"/>, which aliases <see cref="Release"/>).
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
    private readonly IDisposable? _inner;
    private int _refs;

    /// <summary>Creates a new ref-counted wrapper with an initial refcount of 1.</summary>
    /// <param name="inner">The underlying rental (e.g. an <c>ArrayPoolOwner&lt;byte&gt;</c>). May be <see langword="null"/>.</param>
    public RefCountedVideoBuffer(IDisposable? inner)
    {
        _inner = inner;
        _refs = 1;
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
            _inner?.Dispose();
        else if (r < 0)
            throw new InvalidOperationException($"{nameof(RefCountedVideoBuffer)} over-released (refs = {r}).");
    }

    /// <inheritdoc/>
    public void Dispose() => Release();
}


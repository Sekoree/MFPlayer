using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Atomic single-slot container for the "latest video frame" pattern used by
/// clone / preview sinks that hold one frame for the render thread.
///
/// <para>
/// Semantics:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <see cref="Set"/> installs a new frame and <b>disposes</b> the previously held
///   one, closing the common leak window where a fresh frame would overwrite a
///   pool-rented buffer without returning it.
///   </description></item>
///   <item><description>
///   <see cref="Peek"/> returns the current frame without transferring ownership;
///   the caller must not dispose it. Safe to call from a render thread that just
///   wants to display the latest frame every tick.
///   </description></item>
///   <item><description>
///   <see cref="TryTake"/> atomically removes and returns the current frame,
///   transferring ownership of the <see cref="VideoFrame.MemoryOwner"/> to the caller.
///   </description></item>
///   <item><description>
///   <see cref="Clear"/> disposes the held frame and leaves the slot empty —
///   call from <c>Dispose</c> to release the final rental.
///   </description></item>
/// </list>
///
/// <para>
/// Internally guarded by a <see cref="Lock"/> so producer and consumer threads
/// can touch the slot without a TOCTOU race between peek and dispose.
/// </para>
/// </summary>
public sealed class VideoFrameSlot
{
    private readonly Lock _gate = new();
    private VideoFrame? _frame;

    /// <summary>True when the slot currently holds a frame.</summary>
    public bool HasFrame
    {
        get { lock (_gate) return _frame.HasValue; }
    }

    /// <summary>
    /// Installs <paramref name="frame"/> as the current frame, disposing the
    /// previously held one. The slot takes ownership of
    /// <see cref="VideoFrame.MemoryOwner"/>.
    /// </summary>
    public void Set(in VideoFrame frame)
    {
        VideoFrame? previous;
        lock (_gate)
        {
            previous = _frame;
            _frame   = frame;
        }

        if (previous.HasValue && !ReferenceEquals(previous.Value.MemoryOwner, frame.MemoryOwner))
            previous.Value.MemoryOwner?.Dispose();
    }

    /// <summary>
    /// Returns the current frame without transferring ownership.
    /// Caller must not dispose <see cref="VideoFrame.MemoryOwner"/>.
    /// </summary>
    public VideoFrame? Peek()
    {
        lock (_gate) return _frame;
    }

    /// <summary>
    /// Atomically removes and returns the current frame, transferring ownership
    /// of the <see cref="VideoFrame.MemoryOwner"/> to the caller.
    /// </summary>
    public bool TryTake(out VideoFrame frame)
    {
        lock (_gate)
        {
            if (_frame.HasValue)
            {
                frame = _frame.Value;
                _frame = null;
                return true;
            }
        }
        frame = default;
        return false;
    }

    /// <summary>
    /// Disposes the held frame (if any) and clears the slot.
    /// Call from the owner's <c>Dispose</c>.
    /// </summary>
    public void Clear()
    {
        VideoFrame? previous;
        lock (_gate)
        {
            previous = _frame;
            _frame   = null;
        }
        previous?.MemoryOwner?.Dispose();
    }
}


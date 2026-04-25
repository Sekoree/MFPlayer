using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A private view into an <see cref="IVideoChannel"/>'s frame stream. Each subscription owns
/// a dedicated bounded queue, so multiple consumers (pull endpoint + N push endpoints) do not
/// race for frames on a shared ring. The channel fans out each decoded frame to every active
/// subscription, obeying the per-subscription <see cref="VideoSubscriptionOptions.OverflowPolicy"/>.
///
/// <para>
/// <see cref="VideoFrame.MemoryOwner"/> buffers are ref-counted (<see cref="Media.RefCountedVideoBuffer"/>);
/// draining consumers should <c>Dispose()</c> / <c>Release()</c> the owner exactly once per frame
/// they read.
/// </para>
///
/// <para>
/// <b>Disposal:</b> unregisters from the owning channel, drains queued frames (releases their refs),
/// and cancels any waiting publisher.
/// </para>
/// </summary>
public interface IVideoSubscription : IDisposable
{
    /// <summary>Pull up to <paramref name="frameCount"/> queued frames. Returns the number filled.</summary>
    int FillBuffer(Span<VideoFrame> dest, int frameCount);

    /// <summary>Non-allocating single-frame read. Returns <see langword="false"/> when the queue is empty.</summary>
    bool TryRead(out VideoFrame frame);

    /// <summary>Number of frames currently queued.</summary>
    int Count { get; }

    /// <summary>Maximum number of frames the queue can hold before the overflow policy triggers.</summary>
    int Capacity { get; }

    /// <summary><see langword="true"/> when the publisher has stopped and the queue has drained.</summary>
    bool IsCompleted { get; }

    /// <summary>Raised on the publisher's thread when the queue is empty after at least one frame has been seen.</summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
}


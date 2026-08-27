using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.Routing;
using S.Media.Time;

namespace S.Media.NDI;

/// <summary>
/// Builds leased receiver views for the provider's <c>ndi://</c> opens. A PAIRED open
/// (<see cref="LeasePair"/> - the atomic <c>OpenAsync</c> path every player uses) creates ONE
/// <see cref="NDISource"/> delivering both audio and video, anchored together on the single
/// audio-driven ingest clock, instead of two independently anchored receivers (the ~startup A/V
/// offset <c>NDIAVCorrelationProbe</c> measured). Pairing is explicit by construction - both leases
/// come out of one call - never inferred from ambient state: the old thread-id marker could join an
/// unrelated same-name open that happened to land on a recycled pool thread, whose
/// <c>RebaseToLatest</c> would then drain the stranger's buffers. Single-stream opens
/// (<see cref="LeaseVideo"/> / <see cref="LeaseAudio"/>) always get their own receiver.
/// </summary>
/// <remarks>
/// The network-bound open (discovery + first-frame wait, seconds) runs with NO lock held: an entry
/// is shared only through the leases its creating call returns, so there is nothing for another
/// thread to observe early - and one absent source no longer stalls every other <c>ndi://</c> open
/// behind a global gate. A paired receiver is torn down when its last leased adapter is disposed.
/// </remarks>
internal sealed class SharedNDISourceCache(Func<string, NDISource> open)
{
    public IVideoSource LeaseVideo(string sourceKey) => new VideoLease(CreateEntry(sourceKey, leases: 1));

    public IAudioSource LeaseAudio(string sourceKey) => new AudioLease(CreateEntry(sourceKey, leases: 1));

    /// <summary>Leases the requested streams from ONE shared receiver (at least one must be requested).</summary>
    public (IVideoSource? Video, IAudioSource? Audio) LeasePair(string sourceKey, bool video, bool audio)
    {
        if (!video && !audio)
            throw new ArgumentException("a paired NDI lease must request video, audio, or both");

        var entry = CreateEntry(sourceKey, leases: (video ? 1 : 0) + (audio ? 1 : 0));
        return (video ? new VideoLease(entry) : null, audio ? new AudioLease(entry) : null);
    }

    private Entry CreateEntry(string name, int leases)
    {
        // The lease count is fixed before anyone can release: every lease this entry will ever have
        // is created by this same call, so a plain interlocked count is the entire lifetime protocol.
        var source = open(name);
        return new Entry(source, leases);
    }

    private sealed class Entry(NDISource source, int leases)
    {
        private int _leases = leases;

        public NDISource Source { get; } = source;

        public void Release()
        {
            if (Interlocked.Decrement(ref _leases) > 0)
                return;

            // Receiver teardown stops a capture thread and can block - callers dispose leases from
            // ordinary consumer threads, never under a cache-wide lock (there is none).
            Source.Dispose();
        }
    }

    /// <summary>A leased view of the shared source's video adapter - delegates everything, and on dispose
    /// releases its reference (the receiver is torn down only when the last lease is disposed).</summary>
    private sealed class VideoLease(Entry entry) : ILiveVideoSource, IDisposable
    {
        private int _disposed;
        private ILiveVideoSource Inner => (ILiveVideoSource)entry.Source.Video;

        public VideoFormat Format => Inner.Format;
        public IReadOnlyList<PixelFormat> NativePixelFormats => Inner.NativePixelFormats;
        public bool IsExhausted => Inner.IsExhausted;
        public void SelectOutputFormat(PixelFormat format) => Inner.SelectOutputFormat(format);
        public bool TryReadNextFrame(out VideoFrame frame) => Inner.TryReadNextFrame(out frame);
        public void RebaseToLatest(TimeSpan playClockNow) => Inner.RebaseToLatest(playClockNow);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                entry.Release();
        }
    }

    /// <summary>A leased view of the shared source's audio adapter (see <see cref="VideoLease"/>).</summary>
    private sealed class AudioLease(Entry entry) : IAudioSource, IIngestPacedSource, IDisposable
    {
        private int _disposed;
        private IAudioSource Inner => entry.Source.Audio;

        /// <summary>Forwards the shared receiver's ingest-pacing opt-in (null unless the descriptor asked
        /// for it) so the player sees it through the lease.</summary>
        public IPlaybackClock? IngestPacingClock => (Inner as IIngestPacedSource)?.IngestPacingClock;

        public AudioFormat Format => Inner.Format;
        public bool IsExhausted => Inner.IsExhausted;
        public bool TryReadNextFrame(out AudioFrame frame) => Inner.TryReadNextFrame(out frame);
        public int ReadInto(Span<float> destination) => Inner.ReadInto(destination);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                entry.Release();
        }
    }
}

using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.NDI.Video;

namespace HaCue2.Engine;

/// <summary>Which half of a shared NDI sender a lease claims.</summary>
public enum NdiSenderRole
{
    Video,
    Audio,
}

/// <summary>
/// One NDI sender per SOURCE NAME, shared between the video outputs and the audio bay.
/// </summary>
/// <remarks>
/// <para>
/// A linked A/V output is one source on the network, and NDI source names are how receivers find
/// it - two senders with the same name (one from the video side, one from the bay) would race for
/// the identity and receivers would see whichever won. The hub makes the carrier a single
/// <see cref="NDIOutput"/> whose video half and audio half are leased independently: the video
/// outputs re-sync on every edit while the bay restarts only on APPLY, and neither may tear the
/// other's stream down.
/// </para>
/// <para>
/// Each half has at most ONE holder. The native sender's video staging (ping-pong buffers, async
/// send in flight) and audio packing buffer are single-writer by construction, so a second video
/// output or audio line resolving to the same name is refused loudly here and lands in that
/// output/line's failure report - never silently interleaved into somebody else's stream.
/// </para>
/// <para>
/// Senders here are created with the shared egress presentation timeline
/// (<see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/>) in independent-domain mode: the
/// video half stamps composition time, the audio half stamps bay stream position, and the timeline
/// maps both onto one wall-clock epoch so a receiver of a linked carrier gets timestamps that
/// A/V-sync against each other - which is the point of carrying both halves on one source. An edit
/// re-sync re-maps only the video stream; a bay APPLY re-maps only the audio stream
/// (<see cref="NDIOutput.ResetAudioTimecodeAnchor"/> on re-claim). Video is never SDK-clocked: the
/// composition is the cadence owner.
/// </para>
/// </remarks>
public sealed class NdiSenderHub : IDisposable
{
    private sealed class Entry
    {
        public required NDIOutput Sender { get; init; }
        public int Refs;
        public bool VideoClaimed;
        public bool AudioClaimed;
    }

    private readonly Dictionary<string, Entry> _senders = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>One holder's claim on a shared sender's half. Disposing releases the claim; the LAST
    /// release disposes the sender itself (both halves).</summary>
    public sealed class Lease : IDisposable
    {
        private readonly NdiSenderHub _hub;
        private readonly string _name;
        private readonly NdiSenderRole _role;
        private int _disposed;

        internal Lease(NdiSenderHub hub, string name, NdiSenderRole role, NDIOutput sender)
        {
            _hub = hub;
            _name = name;
            _role = role;
            Sender = sender;
        }

        public NDIOutput Sender { get; }

        /// <summary>The sender's video half. Cached by the sender - never dispose it directly;
        /// dispose the lease.</summary>
        public IVideoOutput Video => Sender.Video;

        /// <summary>The sender's audio half at <paramref name="format"/>. Idempotent for the same
        /// format; a different format while the sender is alive throws (the caller reports it).</summary>
        public IAudioOutput EnableAudio(AudioFormat format) => Sender.EnableAudio(format);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _hub.Release(_name, _role);
        }
    }

    /// <summary>
    /// Acquires (or joins) the sender named <paramref name="sourceName"/>, claiming its
    /// <paramref name="role"/> half. Throws when that half already has a live holder - two outputs
    /// or lines resolving to one sender name would otherwise pump one non-thread-safe native sender
    /// from two threads.
    /// </summary>
    public Lease Acquire(string sourceName, NdiSenderRole role)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_senders.TryGetValue(sourceName, out var entry))
            {
                entry = new Entry
                {
                    Sender = new NDIOutput(
                        sourceName,
                        clockVideo: false,
                        clockAudio: true,
                        videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks,
                        independentAvTimecodeDomains: true),
                };
                _senders[sourceName] = entry;
            }

            var alreadyClaimed = role == NdiSenderRole.Video ? entry.VideoClaimed : entry.AudioClaimed;
            if (alreadyClaimed)
                throw new InvalidOperationException(
                    $"NDI source '{sourceName}' already has a live {(role == NdiSenderRole.Video ? "video" : "audio")} feed - " +
                    "two outputs or audio lines resolve to the same sender name; rename one of them.");

            if (role == NdiSenderRole.Video)
            {
                entry.VideoClaimed = true;
            }
            else
            {
                entry.AudioClaimed = true;
                // A RE-claimed audio half (bay APPLY under a running video half) re-maps the audio
                // stream onto the carrier's wall epoch - its sample counter stood still during the
                // restart, and without this its timecodes would lag the video's by that gap.
                entry.Sender.ResetAudioTimecodeAnchor();
            }

            entry.Refs++;
            return new Lease(this, sourceName, role, entry.Sender);
        }
    }

    private void Release(string sourceName, NdiSenderRole role)
    {
        NDIOutput? dispose = null;
        lock (_gate)
        {
            if (!_senders.TryGetValue(sourceName, out var entry))
                return;
            if (role == NdiSenderRole.Video)
                entry.VideoClaimed = false;
            else
                entry.AudioClaimed = false;
            if (--entry.Refs <= 0)
            {
                _senders.Remove(sourceName);
                dispose = entry.Sender;
            }
        }

        try
        {
            dispose?.Dispose();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A sender that will not close must not throw out of an edit or a bay teardown.
        }
    }

    public void Dispose()
    {
        List<NDIOutput> remaining;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            remaining = [.. _senders.Values.Select(entry => entry.Sender)];
            _senders.Clear();
        }

        foreach (var sender in remaining)
        {
            try
            {
                sender.Dispose();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
            }
        }
    }
}

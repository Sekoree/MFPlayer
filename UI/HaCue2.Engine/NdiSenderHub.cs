using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.NDI.Video;

namespace HaCue2.Engine;

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
/// Senders here are created with the shared egress presentation timeline
/// (<see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/>), so a receiver of a linked
/// carrier gets timestamps that A/V-sync against each other - which is the point of carrying both
/// halves on one source. Video is never SDK-clocked: the composition is the cadence owner.
/// </para>
/// </remarks>
public sealed class NdiSenderHub : IDisposable
{
    private sealed class Entry
    {
        public required NDIOutput Sender { get; init; }
        public int Refs;
    }

    private readonly Dictionary<string, Entry> _senders = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>One holder's claim on a shared sender. Disposing releases the claim; the LAST
    /// release disposes the sender itself (both halves).</summary>
    public sealed class Lease : IDisposable
    {
        private readonly NdiSenderHub _hub;
        private readonly string _name;
        private int _disposed;

        internal Lease(NdiSenderHub hub, string name, NDIOutput sender)
        {
            _hub = hub;
            _name = name;
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
                _hub.Release(_name);
        }
    }

    /// <summary>Acquires (or joins) the sender named <paramref name="sourceName"/>.</summary>
    public Lease Acquire(string sourceName)
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
                        videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks),
                };
                _senders[sourceName] = entry;
            }

            entry.Refs++;
            return new Lease(this, sourceName, entry.Sender);
        }
    }

    private void Release(string sourceName)
    {
        NDIOutput? dispose = null;
        lock (_gate)
        {
            if (!_senders.TryGetValue(sourceName, out var entry))
                return;
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

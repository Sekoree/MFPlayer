
namespace S.Media.Routing;

/// <summary>
/// Paces <see cref="AudioRouter"/> production from an <see cref="IPlaybackClock"/> media timeline
/// (for example <c>NDIIngestPlaybackClock</c>) instead of wall clock or PortAudio.
/// </summary>
internal sealed class PlaybackSlavedRouterClock : IRouterClock
{
    private readonly IPlaybackClock _master;
    private readonly TimeSpan _chunkDuration;
    private TimeSpan _nextChunkDeadline;
    private long _masterEpochId;

    public PlaybackSlavedRouterClock(IPlaybackClock master, int sampleRate, int chunkSamples)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (chunkSamples <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        _master = master;
        _chunkDuration = TimeSpan.FromSeconds((double)chunkSamples / sampleRate);
        _nextChunkDeadline = _chunkDuration;
        _masterEpochId = master.Read().EpochId;
    }

    public void Reset()
    {
        _nextChunkDeadline = _chunkDuration;
        _masterEpochId = _master.Read().EpochId;
    }

    /// <summary>After a master-timeline jump/stall larger than this many chunks, re-anchor instead of
    /// bursting the whole backlog back-to-back (matches <see cref="S.Media.Core.Clock.MediaClock"/>).</summary>
    private const int MaxCatchupChunks = 64;

    public bool WaitForNextChunk(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var reading = _master.Read();
            var elapsed = reading.Elapsed;
            if (reading.EpochId != _masterEpochId)
            {
                // An ingest seek/receiver reattach may restart elapsed below the previous deadline. Waiting
                // for that old coordinate would stall the router until the new epoch caught all the way up.
                // Drop the old schedule and require one fresh chunk of progress in the new epoch instead.
                _masterEpochId = reading.EpochId;
                _nextChunkDeadline = elapsed + _chunkDuration;
            }
            if (elapsed >= _nextChunkDeadline)
            {
                // Cap catch-up after a large master jump/stall: drop the excess backlog and re-anchor.
                if (elapsed - _nextChunkDeadline > _chunkDuration * MaxCatchupChunks)
                    _nextChunkDeadline = elapsed;
                _nextChunkDeadline += _chunkDuration;
                return true;
            }

            token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(2));
        }

        return false;
    }
}

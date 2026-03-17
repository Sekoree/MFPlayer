using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Events;

namespace Seko.OwnAudioNET.Video.Sources;

/// <summary>
/// Contract for a clock-aware video source that can participate in the same transport and
/// synchronization flows as OwnAudio audio sources.
/// </summary>
public interface IVideoSource : IDisposable, ISynchronizable
{
    /// <summary>Unique identifier for this source instance.</summary>
    Guid Id { get; }

    /// <summary>Current playback state.</summary>
    VideoPlaybackState State { get; }

    /// <summary>Metadata describing the underlying stream.</summary>
    VideoStreamInfo StreamInfo { get; }

    /// <summary>Current presentation position in seconds relative to the start of the media stream.</summary>
    double Position { get; }

    /// <summary>Total media duration in seconds when known; otherwise zero.</summary>
    double Duration { get; }

    /// <summary><see langword="true"/> once the source has consumed all frames for the active playback pass.</summary>
    bool IsEndOfStream { get; }

    /// <summary><see langword="true"/> when hardware-accelerated decoding is active.</summary>
    bool IsHardwareDecoding { get; }

    /// <summary>
    /// Start position of the source on the shared master timeline, in seconds.
    /// </summary>
    double StartOffset { get; set; }

    /// <summary><see langword="true"/> when a master clock is currently attached.</summary>
    bool IsAttachedToClock { get; }

    /// <summary>Raised when playback state changes.</summary>
    event EventHandler<VideoPlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when the source encounters an error.</summary>
    event EventHandler<VideoErrorEventArgs>? Error;

    /// <summary>Raised when a new current frame is promoted.</summary>
    event EventHandler<VideoFrameReadyEventArgs>? FrameReady;

    /// <summary>Zero-allocation alternative to <see cref="FrameReady"/>.</summary>
    event Action<VideoFrame, double>? FrameReadyFast;

    /// <summary>Raised when decoder stream metadata changes at runtime.</summary>
    event EventHandler<VideoStreamInfoChangedEventArgs>? StreamInfoChanged;

    /// <summary>Attempts to provide the frame that should be shown at the given master-clock time.</summary>
    bool TryGetFrameAtTime(double masterTimestamp, out VideoFrame frame);

    /// <summary>Requests the current frame using the attached master clock.</summary>
    bool RequestNextFrame(out VideoFrame frame);

    /// <summary>Seeks to the given media position.</summary>
    bool Seek(double positionInSeconds);

    /// <summary>Seeks to an absolute frame index. Returns <see langword="false"/> when out of range.</summary>
    bool SeekToFrame(long frameIndex);

    /// <summary>Seeks to the start of the stream (frame 0).</summary>
    bool SeekToStart();

    /// <summary>Seeks to the final presentable frame when known.</summary>
    bool SeekToEnd();

    /// <summary>Transitions the source into the playing state.</summary>
    void Play();

    /// <summary>Transitions the source into the paused state.</summary>
    void Pause();

    /// <summary>Stops playback and rewinds to the beginning of the media stream.</summary>
    void Stop();

    /// <summary>Attaches this source to a master clock for synchronized playback.</summary>
    void AttachToClock(MasterClock clock);

    /// <summary>Detaches this source from the current master clock.</summary>
    void DetachFromClock();
}


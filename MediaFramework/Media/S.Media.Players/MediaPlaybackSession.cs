using S.Media.Core.Audio;
using S.Media.Time;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>
/// Holds <see cref="VideoPlayer"/> + <see cref="IMediaClock"/> (+ optional audio router/clock) for coordinated transport.
/// </summary>
internal sealed class MediaPlaybackSession : IAvPlaybackSession
{
    public MediaPlaybackSession(
        VideoPlayer video,
        IMediaClock clock,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        string? audioSourceId = null)
    {
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        AudioRouter = audioRouter;
        AudioClock = audioClock;
        AudioSourceId = audioSourceId;
        _start = new VoiceStartPolicy(Video, AudioRouter, AudioClock, AudioSourceId);
    }

    /// <summary>This voice's start discipline, and its memory of how it started last time. One per
    /// session, because the clock/router/source it decides over are created together by one open and
    /// never change.</summary>
    private readonly VoiceStartPolicy _start;

    public VideoPlayer Video { get; }
    public IMediaClock Clock { get; }
    public AudioRouter? AudioRouter { get; }
    public MediaClock? AudioClock { get; }
    public string? AudioSourceId { get; }

    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        PreparePlay(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill)();

    public Action PreparePlay(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        _start.Prepare(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill);

    public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.Pause(Video, AudioRouter, AudioClock, cancellationToken, flushSharedMuxAfterPause);

    public void Seek(TimeSpan position) =>
        AvPlaybackCoordinator.Seek(Video, AudioRouter, AudioClock, AudioSourceId, position);

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.SeekCoordinated(Video, AudioRouter, AudioClock, AudioSourceId, position, cancellationToken,
            flushSharedMuxAfterPause);
}

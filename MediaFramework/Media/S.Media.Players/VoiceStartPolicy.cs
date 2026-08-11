using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Time;

namespace S.Media.Players;

/// <summary>
/// How ONE voice gets started, and the memory of how it was started last time.
/// </summary>
/// <remarks>
/// <para>
/// A voice's start is not one procedure with options - it is one of five <em>disciplines</em>, picked
/// from the shape of the voice's graph and from what this policy did on the previous start:
/// </para>
/// <list type="number">
/// <item><see cref="GenlockFirstStart"/> - a silent voice (audio routed nowhere) mastered to the show
/// clock and held back by the measured audio pipeline depth.</item>
/// <item><see cref="GenlockResume"/> - the same voice resuming: re-anchor, do NOT defer.</item>
/// <item><see cref="PreRollRelease"/> - a sounding voice whose primary output can be held out of the
/// mix, so the start edge is a release rather than a race.</item>
/// <item><see cref="PlainStart"/> - a sounding voice with no pre-rollable output.</item>
/// <item><see cref="VideoOnlyStart"/> - no audio router at all.</item>
/// </list>
/// <para>
/// These used to be five branches inside one 190-line static method taking eight optional parameters,
/// where which branch ran depended on which combination of them happened to be non-null. The state that
/// separates (1) from (2) had nowhere to live, so it sat in a <em>static</em>
/// <c>ConditionalWeakTable&lt;MediaClock, IPlaybackClock&gt;</c> keyed on the voice's clock - a global
/// side table standing in for one instance field. Owning the graph makes it that field
/// (<see cref="_appliedGenlock"/>), and makes each discipline a named method that can be read, and
/// tested, on its own.
/// </para>
/// <para>
/// One policy per <see cref="MediaPlaybackSession"/>, so one per voice: the clock, router and source id
/// are all created together by the same open and never change for the life of the player.
/// </para>
/// </remarks>
internal sealed class VoiceStartPolicy
{
    private static readonly ILogger Trace =
        MediaDiagnostics.CreateLogger("S.Media.Core.Playback.VoiceStartPolicy");

    /// <summary>Upper bound on the silent-voice start deferral (see <see cref="GenlockFirstStart"/>).
    /// With master pacing the device ring runs full, so a legitimate pipeline depth is
    /// ring (~85 ms) + pump (~80 ms) + device (~180 ms observed); the cap only exists to stop a
    /// pathological measurement from parking a picture indefinitely.</summary>
    private static readonly TimeSpan MaxStartDeferral = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan SyncStartVideoOutputTimeout = TimeSpan.FromMilliseconds(250);

    private readonly VideoPlayer _video;
    private readonly AudioRouter? _audioRouter;
    private readonly MediaClock? _audioClock;
    private readonly string? _audioSourceId;

    /// <summary>
    /// The show clock <see cref="GenlockFirstStart"/> mastered this voice to, or null if it never did.
    /// Pause/resume must be able to tell the two kinds of master apart: a voice's own producer clock
    /// FREEZES and flushes with the voice, so <c>MediaClock.Start</c>'s fold of same-epoch master drift
    /// across a pause is exactly right for it - while the shared show clock keeps advancing through this
    /// one voice's pause and never changes epoch for it, so the same fold turns the whole pause duration
    /// into a forward position jump (a 30 s pause = a 30 s skip; the measured bug). The session's resume
    /// path calls plain Play with no <c>videoOnlyMaster</c>, so the resume cannot recognise the genlock
    /// from its arguments - this field is the explicit "I applied that genlock" record. Deliberately not
    /// type-sniffing <c>MediaClock.Master</c>: producer leases and the show clock can share a type.
    /// </summary>
    private IPlaybackClock? _appliedGenlock;

    public VoiceStartPolicy(
        VideoPlayer video,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        string? audioSourceId)
    {
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _audioRouter = audioRouter;
        _audioClock = audioClock;
        _audioSourceId = audioSourceId;
    }

    /// <summary>
    /// The slow half of a start - prefill, hardware start, decode spin-up, the video buffer wait and the
    /// sync-frame present - WITHOUT starting the clocks. The returned action is the start edge: it starts
    /// the router and clock and is cheap enough to call for a whole group of voices back-to-back.
    /// </summary>
    /// <remarks>
    /// The split exists for group fires. A batch of siblings must start on ONE edge, but their commits
    /// run serially on the session dispatcher - and when this slow half lived in the same call as the
    /// clock start, every voice queued behind a video sibling started late by that sibling's present-sync
    /// (up to 250 ms each; measured half-second staggers, in per-run-random order). Prepare each voice
    /// first, then fire the returned starters together.
    /// </remarks>
    public Action Prepare(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null)
    {
        Trace.LogDebug(
            "Prepare: hasAudio={HasAudio} hasPrefill={HasPrefill} hasStartHw={HasStartHw} hasVoMaster={HasVoMaster}",
            _audioRouter is not null, prefillBeforeHardware is not null, startHardware is not null,
            videoOnlyMaster is not null);

        if (AlreadyRunning())
            return static () => { };

        prefillBeforeHardware?.Invoke();
        startHardware?.Invoke();

        if (_audioRouter is not { } router || _audioClock is not { } clock)
            return VideoOnlyStart(videoOnlyMaster, verifyPrebufferAfterPrefill);

        // Realign audio before video.Play() - video starts the decode thread and may start the shared
        // clock; audio Position tracks emitted samples (approximately the clock at pause) so a drift
        // threshold would skip realign even when video decode is ~700 ms ahead.
        if (!clock.IsRunning)
            AvPlaybackCoordinator.RealignAudioSourceBeforeStart(router, clock, _audioSourceId);

        _video.Play();

        WaitForVideoBufferBeforeStartingAudio(verifyPrebufferAfterPrefill);
        if (!_video.TryPresentBufferedFrameForSync(clock.CurrentPosition, SyncStartVideoOutputTimeout))
        {
            Trace.LogDebug(
                "Prepare: sync video presentation did not complete before audio start (timeout={Timeout}, queued={Queued}, latestDecoded={Latest})",
                SyncStartVideoOutputTimeout, _video.QueuedFrameCount, _video.LatestDecodedPresentationTime);
        }

        return SelectAudioDiscipline(router, clock, videoOnlyMaster);
    }

    /// <summary>
    /// Which of the four audio-path disciplines applies. Read top to bottom: the two genlock cases are
    /// about a voice with no authoritative clock of its own, the pre-roll case is about a voice that has
    /// one and can be held out of the mix, and the last is everything else.
    /// </summary>
    private Action SelectAudioDiscipline(AudioRouter router, MediaClock clock, IPlaybackClock? videoOnlyMaster)
    {
        // Was THIS policy's genlock the thing that mastered the clock? If the host re-mastered it since
        // (a real pacing primary was promoted, or the master was cleared), the record is stale - drop it
        // and treat the clock as producer-mastered from here on.
        if (_appliedGenlock is not null && !ReferenceEquals(clock.Master, _appliedGenlock))
            _appliedGenlock = null;

        if (videoOnlyMaster is not null && clock.Master is null)
            return GenlockFirstStart(router, clock, videoOnlyMaster);

        if (_appliedGenlock is not null)
            return GenlockResume(router, clock, videoOnlyMaster ?? _appliedGenlock);

        if (router.PrimaryOutputId is { } primaryId
            && router.TryGetOutput(primaryId, out var primaryOutput)
            && primaryOutput is IPreRollableOutput preRollable)
        {
            return PreRollRelease(router, clock, preRollable);
        }

        return PlainStart(router, clock);
    }

    /// <summary>
    /// A clip can HAVE an audio router and still have no authoritative clock: its audio reaches no
    /// clocked output, so nothing promoted a pacing primary and this MediaClock free-runs on a Stopwatch.
    /// That is the silent video cue - a .mov whose audio is routed nowhere - and it means such a voice is
    /// timed by WALL time while every sounding voice is timed by the audio DEVICE. The two are never the
    /// same crystal, so the picture slides away from the sound without bound (measured 0.72 %/s on the
    /// incident rig: seconds apart within minutes). Genlock it to the show's clock instead.
    /// </summary>
    private Action GenlockFirstStart(AudioRouter router, MediaClock clock, IPlaybackClock showClock)
    {
        clock.SetMaster(showClock);
        _appliedGenlock = showClock;
        Trace.LogDebug(
            "Prepare: audio router has no pacing primary - mastering this voice's clock to the show clock ({ClockType}) so its video cannot drift against the audio device",
            showClock.GetType().Name);

        var deferAgainstProgramme = showClock as AudibleClientClock;
        return () =>
        {
            // Rate alone is not enough: the show clock is already ADVANCING, so an anchor taken at Start
            // makes this voice's timeline move immediately - while a sounding voice's producer clock
            // holds at zero until its first sample actually leaves the speaker, one audio-pipeline depth
            // later. Without the deferral the picture led the programme by exactly that depth (measured
            // ~150-500 ms), rate-locked, for the whole cue. Measured HERE, at the start edge, not at
            // prepare: a group fire prepares its siblings first, and the fill transient during those
            // preparations reads far above the depth the siblings will actually start with (measuring at
            // prepare landed the picture ~170 ms BEHIND the stems). Capped as a safety net - the cap sits
            // above any steady-state depth this pipeline produces.
            if (deferAgainstProgramme?.CurrentPipelineLead is { Ticks: > 0 } lead)
            {
                var deferral = lead <= MaxStartDeferral ? lead : MaxStartDeferral;
                clock.DeferStart(deferral);
                Trace.LogDebug(
                    "Prepare: deferred this voice's start by the audio pipeline depth ({DeferMs:0}ms of {LeadMs:0}ms measured) so its picture lands on the audible programme",
                    deferral.TotalMilliseconds, lead.TotalMilliseconds);
            }

            router.Start();
            clock.Start();
        };
    }

    /// <summary>
    /// This clock is still mastered to the shared show clock <see cref="GenlockFirstStart"/> attached.
    /// That master kept advancing (and kept its epoch) all through this voice's pause, so the drift fold
    /// in <c>MediaClock.Start</c> - which is CORRECT for a producer-mastered voice whose clock froze and
    /// flushed with it - would count the entire pause duration as forward progress (measured: a 30 s
    /// pause resumed 30 s ahead). Re-running SetMaster discards the pause-time master reading and
    /// re-anchors at the CURRENT position, so Start folds nothing and the voice resumes where it paused.
    /// <para>The pipeline-lead deferral is deliberately NOT re-applied. It exists to hold a brand-new
    /// picture back so frame 0 lands when sample 0 of the sounding siblings becomes audible - a
    /// start-of-audio edge. A resume has no such edge: its frames are already on screen, and DeferStart
    /// would rewind the playhead by the lead (~150-500 ms), visibly re-showing frames to chase a sibling
    /// refill transient that the resume prefill largely hides anyway.</para>
    /// </summary>
    private Action GenlockResume(AudioRouter router, MediaClock clock, IPlaybackClock showClock)
    {
        clock.SetMaster(showClock);
        _appliedGenlock = showClock;
        Trace.LogDebug(
            "Prepare: genlocked resume - re-anchored this voice's clock on the show clock ({ClockType}) at {Position} so the pause duration is not folded in",
            showClock.GetType().Name, clock.CurrentPosition);
        return () =>
        {
            router.Start();
            clock.Start();
        };
    }

    /// <summary>
    /// Group-fire alignment: when the pacing primary can be held out of the mix, start the router NOW -
    /// the producer ring fills with the clip's first samples while the bus skips it and the voice's clock
    /// stays frozen - and make the start edge a release. Every sibling released in one tight pass joins
    /// the same (or the adjacent) mix chunk, which is what "an all-together group starts together"
    /// actually requires: without this the audio still raced decode → ring → bus after the edge, and that
    /// race's length was scheduler weather (measured 0-180 ms of stem scatter, quantized by pump depth).
    /// </summary>
    private static Action PreRollRelease(AudioRouter router, MediaClock clock, IPreRollableOutput preRollable)
    {
        preRollable.BeginPreRoll();
        router.Start();
        return () =>
        {
            preRollable.EndPreRoll();
            clock.Start();
        };
    }

    private static Action PlainStart(AudioRouter router, MediaClock clock) => () =>
    {
        router.Start();
        clock.Start();
    };

    private Action VideoOnlyStart(IPlaybackClock? videoOnlyMaster, Func<bool>? verifyPrebufferAfterPrefill)
    {
        _video.Play();

        if (verifyPrebufferAfterPrefill is not null && !verifyPrebufferAfterPrefill())
            throw new InvalidOperationException(
                "VoiceStartPolicy.Prepare: verifyPrebufferAfterPrefill returned false.");

        if (videoOnlyMaster is not null)
            _video.Clock.SetMaster(videoOnlyMaster);

        var clock = _video.Clock;
        return () =>
        {
            if (!clock.IsRunning)
                clock.Start();
        };
    }

    /// <summary>
    /// A start on an ALREADY-RUNNING transport is a cheap no-op. Hosts do issue them - the session's
    /// seek/resume paths start unconditionally - and re-running the slow half mid-stream is actively
    /// harmful: the sync present steals a queued frame from the live jitter buffer (a visible glitch),
    /// and the pre-roll discipline would BeginPreRoll a LIVE producer (muting it) and then EndPreRoll
    /// with a fresh pacing-target baseline plus a mid-run re-anchor (~200 ms position step).
    /// <para>"Running" is the clock AND the router for the audio path: at natural EOF the router's run
    /// loop has exited (CompletedNaturally) while the clock object keeps its running flag, and a restart
    /// (seek + start) from that state still needs the full path so <c>router.Start()</c> actually spins
    /// production back up.</para>
    /// </summary>
    private bool AlreadyRunning()
    {
        if (_audioRouter is not null && _audioClock is not null)
        {
            if (!_audioClock.IsRunning || !_audioRouter.IsRunning)
                return false;
            Trace.LogDebug("Prepare: transport already running (clock={Position}) - no-op", _audioClock.CurrentPosition);
            return true;
        }

        if (!_video.Clock.IsRunning)
            return false;
        Trace.LogDebug("Prepare: video clock already running (clock={Position}) - no-op", _video.Clock.CurrentPosition);
        return true;
    }

    /// <summary>
    /// After a seek/resume, the compositor/scaler path can decode far slower than realtime. Hold audio
    /// until the video jitter buffer has enough frames stamped at/after the sync target.
    /// </summary>
    private void WaitForVideoBufferBeforeStartingAudio(Func<bool>? verify)
    {
        var clock = _video.Clock;
        var target = clock.CurrentPosition;
        var waitStart = Environment.TickCount64;

        var satisfied = _video.WaitForFrames(
            () => verify is not null
                ? verify()
                : AvPlaybackCoordinator.IsVideoBufferReadyForSync(_video, target)
                  || AvPlaybackCoordinator.NoVideoToAwait(_video),
            VideoBufferWaitTimeout);

        if (!satisfied && verify is not null && !verify())
        {
            throw new InvalidOperationException(
                "VoiceStartPolicy.Prepare: verifyPrebufferAfterPrefill returned false after waiting for the video buffer.");
        }

        if (!Trace.IsEnabled(LogLevel.Debug))
            return;

        var playhead = target - _video.PlayheadOffset;
        var lead = _video.SyncStartupLead;
        TimeSpan? masterElapsed = null;
        if (clock is MediaClock mc && mc.Master is { } master)
            masterElapsed = master.ElapsedSinceStart;
        Trace.LogDebug(
            "WaitForVideoBuffer: waitedMs={WaitMs} target={Target} queued={Queued} lifetimePending={LifetimePending} latestDecoded={Latest} clock={Clock} syncReady={SyncReady} leadMs={LeadMs} masterElapsed={MasterElapsed}",
            Environment.TickCount64 - waitStart, target, _video.QueuedFrameCount, _video.PendingBufferedCount,
            _video.LatestDecodedPresentationTime, clock.CurrentPosition,
            _video.HasFrameWithinLeadOf(playhead, lead), lead.TotalMilliseconds, masterElapsed);
    }

    private static readonly TimeSpan VideoBufferWaitTimeout = TimeSpan.FromSeconds(8);
}

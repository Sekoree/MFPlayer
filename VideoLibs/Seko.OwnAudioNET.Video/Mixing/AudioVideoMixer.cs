using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Diagnostics;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Combines an audio mixer and a video mixer so both domains share the audio-led master clock.
/// </summary>
public sealed class AudioVideoMixer : IAudioVideoMixer
{
    public readonly record struct DiagnosticsSnapshot(
        long DriftCorrectionSuppressedTickCount,
        long DriftHardResyncAttemptCount,
        long DriftHardResyncSuccessCount,
        long DriftHardResyncFailureCount);

    private readonly bool _ownsAudioMixer;
    private readonly bool _ownsVideoMixer;
    private readonly IExternalClock? _externalClock;
    private readonly Timer _driftCorrectionTimer;
    private readonly AudioVideoDriftCorrectionConfig _driftCorrectionConfig;
    private readonly DiagnosticsCounterStore _diagCounters = new();
    private int _driftCorrectionTickActive;
    private long _driftCorrectionSuppressedUntilMs;
    private bool _disposed;

    private const string DriftSuppressedTickCounter = "drift.suppressedTicks";
    private const string DriftHardResyncAttemptCounter = "drift.hardResync.attempt";
    private const string DriftHardResyncSuccessCounter = "drift.hardResync.success";
    private const string DriftHardResyncFailureCounter = "drift.hardResync.failure";

    public AudioVideoMixer(AudioMixer audioMixer, IVideoMixer videoMixer, bool ownsAudioMixer = false, bool ownsVideoMixer = false)
        : this(audioMixer, videoMixer, driftCorrectionConfig: null, externalClock: null, ownsAudioMixer, ownsVideoMixer)
    {
    }

    public AudioVideoMixer(
        AudioMixer audioMixer,
        IVideoMixer videoMixer,
        AudioVideoDriftCorrectionConfig? driftCorrectionConfig,
        IExternalClock? externalClock = null,
        bool ownsAudioMixer = false,
        bool ownsVideoMixer = false)
    {
        AudioMixer = audioMixer ?? throw new ArgumentNullException(nameof(audioMixer));
        VideoMixer = videoMixer ?? throw new ArgumentNullException(nameof(videoMixer));
        _ownsAudioMixer = ownsAudioMixer;
        _ownsVideoMixer = ownsVideoMixer;
        _externalClock = externalClock ?? videoMixer.ExternalClock;
        _driftCorrectionConfig = (driftCorrectionConfig ?? new AudioVideoDriftCorrectionConfig()).CloneNormalized();

        AudioMixer.SourceError += OnAudioSourceError;
        VideoMixer.SourceError += OnVideoSourceError;
        VideoMixer.ActiveSourceChanged += OnActiveVideoSourceChanged;

        _driftCorrectionTimer = new Timer(
            static state => ((AudioVideoMixer)state!).DriftCorrectionTick(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public AudioVideoDriftCorrectionConfig DriftCorrectionConfig => _driftCorrectionConfig;

    public AudioMixer AudioMixer { get; }

    public IVideoMixer VideoMixer { get; }

    public MasterClock MasterClock => AudioMixer.MasterClock;

    public double Position => _externalClock?.CurrentSeconds ?? MasterClock.CurrentTimestamp;

    public IExternalClock? ExternalClock => _externalClock;

    public bool IsRunning => AudioMixer.IsRunning || VideoMixer.IsRunning;

    public int AudioSourceCount => AudioMixer.SourceCount;

    public int VideoSourceCount => VideoMixer.SourceCount;


    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return new DiagnosticsSnapshot(
            _diagCounters.Read(DriftSuppressedTickCounter),
            _diagCounters.Read(DriftHardResyncAttemptCounter),
            _diagCounters.Read(DriftHardResyncSuccessCounter),
            _diagCounters.Read(DriftHardResyncFailureCounter));
    }

    public event EventHandler<AudioErrorEventArgs>? AudioSourceError;

    public event EventHandler<VideoErrorEventArgs>? VideoSourceError;

    public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveVideoSourceChanged;

    public bool AddAudioSource(IAudioSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return AudioMixer.AddSource(source);
    }

    public bool RemoveAudioSource(IAudioSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return AudioMixer.RemoveSource(source);
    }

    public IAudioSource[] GetAudioSources()
    {
        ThrowIfDisposed();
        return AudioMixer.GetSources();
    }

    public void ClearAudioSources()
    {
        ThrowIfDisposed();
        AudioMixer.ClearSources();
    }

    public bool AddVideoSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.AddSource(source);
    }

    public bool RemoveVideoSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.RemoveSource(source);
    }

    public VideoStreamSource[] GetVideoSources()
    {
        ThrowIfDisposed();
        return VideoMixer.GetSources();
    }

    public void ClearVideoSources()
    {
        ThrowIfDisposed();
        VideoMixer.ClearSources();
    }

    public VideoStreamSource? ActiveVideoSource
    {
        get
        {
            ThrowIfDisposed();
            return VideoMixer.ActiveSource;
        }
    }

    public bool SetActiveVideoSource(VideoStreamSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.SetActiveSource(source);
    }

    public void Start()
    {
        ThrowIfDisposed();
        AudioMixer.Start();
        VideoMixer.Start();

        if (_driftCorrectionConfig.Enabled)
            _driftCorrectionTimer.Change(_driftCorrectionConfig.CorrectionIntervalMs, _driftCorrectionConfig.CorrectionIntervalMs);
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _driftCorrectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        AudioMixer.Pause();
        VideoMixer.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        _driftCorrectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        AudioMixer.Stop();
        VideoMixer.Stop();
        ResetVideoDriftCorrections();
    }

    public void Seek(double positionInSeconds)
    {
        Seek(positionInSeconds, AudioVideoSeekMode.Auto);
    }

    public void Seek(double positionInSeconds, AudioVideoSeekMode seekMode)
    {
        ThrowIfDisposed();
        var target = Math.Max(0, positionInSeconds);
        var current = Position;
        var safeSeek = ResolveSafeSeek(seekMode, target, current);
        var wasRunning = IsRunning;

        if (safeSeek && wasRunning)
            Pause();

        try
        {
            PerformCoordinatedSeek(target, wasRunning);
        }
        finally
        {
            if (safeSeek && wasRunning)
                Start();
        }
    }

    private void PerformCoordinatedSeek(double targetTimelineSeconds, bool resumePlayingSources)
    {
        MasterClock.SeekTo(targetTimelineSeconds);
        SeekAudioSources(targetTimelineSeconds, resumePlayingSources);
        VideoMixer.Seek(targetTimelineSeconds, safeSeek: false);
        ResetVideoDriftCorrections();
        SuppressDriftCorrectionForSeekWindow();
    }

    private void SeekAudioSources(double timelinePositionSeconds, bool resumePlayingSources)
    {
        foreach (var audioSource in AudioMixer.GetSources())
        {
            var wasPlaying = audioSource.State == AudioState.Playing;
            var trackPosition = ResolveAudioTrackPosition(audioSource, timelinePositionSeconds);
            var clampedTrackPosition = Math.Max(0, trackPosition);

            if (audioSource.Duration > 0 && clampedTrackPosition > audioSource.Duration)
                clampedTrackPosition = audioSource.Duration;

            if (audioSource.State == AudioState.Stopped)
                continue;

            if (audioSource.Duration > 0 && timelinePositionSeconds >= audioSource.Duration && clampedTrackPosition >= audioSource.Duration)
                continue;

            try
            {
                audioSource.Seek(clampedTrackPosition);

                if (resumePlayingSources && wasPlaying)
                    audioSource.Play();
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static double ResolveAudioTrackPosition(IAudioSource audioSource, double timelinePositionSeconds)
    {
        return audioSource is AudioStreamSource ffAudioSource
            ? timelinePositionSeconds - ffAudioSource.StartOffset
            : timelinePositionSeconds;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        AudioMixer.SourceError -= OnAudioSourceError;
        VideoMixer.SourceError -= OnVideoSourceError;
        VideoMixer.ActiveSourceChanged -= OnActiveVideoSourceChanged;
        _driftCorrectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _driftCorrectionTimer.Dispose();

        try
        {
            if (_ownsVideoMixer)
                VideoMixer.Dispose();
        }
        finally
        {
            if (_ownsAudioMixer)
                AudioMixer.Dispose();

            _disposed = true;
        }
    }

    private void OnAudioSourceError(object? sender, AudioErrorEventArgs e)
    {
        AudioSourceError?.Invoke(sender, e);
    }

    private void OnVideoSourceError(object? sender, VideoErrorEventArgs e)
    {
        VideoSourceError?.Invoke(sender, e);
    }

    private void OnActiveVideoSourceChanged(object? sender, VideoActiveSourceChangedEventArgs e)
    {
        ActiveVideoSourceChanged?.Invoke(this, e);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioVideoMixer));
    }

    private void ResetVideoDriftCorrections()
    {
        foreach (var videoSource in VideoMixer.GetSources())
            videoSource.ResetDriftCorrectionOffset();
    }

    private bool ResolveSafeSeek(AudioVideoSeekMode seekMode, double target, double current)
    {
        return seekMode switch
        {
            AudioVideoSeekMode.Fast => false,
            AudioVideoSeekMode.Safe => true,
            _ => target < current - 1e-6
        };
    }

    private void SuppressDriftCorrectionForSeekWindow()
    {
        var suppressionMs = _driftCorrectionConfig.PostSeekSuppressionMs;
        if (suppressionMs <= 0)
            return;

        var nowMs = Environment.TickCount64;
        Interlocked.Exchange(ref _driftCorrectionSuppressedUntilMs, nowMs + suppressionMs);
    }

    private void DriftCorrectionTick()
    {
        if (_disposed || !IsRunning)
            return;

        if (!_driftCorrectionConfig.Enabled)
            return;

        var nowMs = Environment.TickCount64;
        if (nowMs < Interlocked.Read(ref _driftCorrectionSuppressedUntilMs))
        {
            _diagCounters.Increment(DriftSuppressedTickCounter);
            return;
        }

        if (Interlocked.Exchange(ref _driftCorrectionTickActive, 1) != 0)
            return;

        try
        {
            var masterTimestamp = MasterClock.CurrentTimestamp;
            foreach (var videoSource in VideoMixer.GetSources())
            {
                // Correct only active source.
                if (!ReferenceEquals(VideoMixer.ActiveSource, videoSource))
                    continue;

                if (videoSource.State != VideoPlaybackState.Playing)
                    continue;

                var frameTimestamp = videoSource.CurrentFramePtsSeconds;
                if (double.IsNaN(frameTimestamp) || double.IsInfinity(frameTimestamp))
                    continue;

                var expectedVideoTimestamp = masterTimestamp - videoSource.StartOffset;
                var driftSeconds = frameTimestamp - expectedVideoTimestamp;
                var absoluteDrift = Math.Abs(driftSeconds);

                if (absoluteDrift <= _driftCorrectionConfig.DeadbandSeconds)
                    continue;

                if (absoluteDrift >= _driftCorrectionConfig.HardResyncThresholdSeconds)
                {
                    var targetVideoPosition = Math.Max(0, expectedVideoTimestamp);
                    _diagCounters.Increment(DriftHardResyncAttemptCounter);
                    try
                    {
                        videoSource.Seek(targetVideoPosition);
                        _diagCounters.Increment(DriftHardResyncSuccessCounter);
                    }
                    catch
                    {
                        _diagCounters.Increment(DriftHardResyncFailureCounter);
                        // Ignore.
                    }

                    videoSource.ResetDriftCorrectionOffset();
                    SuppressDriftCorrectionForSeekWindow();
                    continue;
                }

                var correctionStep = Math.Clamp(
                    -driftSeconds * _driftCorrectionConfig.CorrectionGain,
                    -_driftCorrectionConfig.MaxStepSeconds,
                    _driftCorrectionConfig.MaxStepSeconds);
                videoSource.ApplyDriftCorrectionDelta(correctionStep, _driftCorrectionConfig.MaxAbsoluteCorrectionSeconds);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _driftCorrectionTickActive, 0);
        }
    }
}


using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// Combines an audio mixer and a video mixer so both domains share the audio-led master clock.
/// </summary>
public sealed class AudioVideoMixer : IAudioVideoMixer
{
    private readonly bool _ownsAudioMixer;
    private readonly bool _ownsVideoMixer;
    private readonly Timer _driftCorrectionTimer;
    private readonly AudioVideoDriftCorrectionConfig _driftCorrectionConfig;
    private int _driftCorrectionTickActive;
    private bool _disposed;

    public AudioVideoMixer(AudioMixer audioMixer, IVideoMixer videoMixer, bool ownsAudioMixer = false, bool ownsVideoMixer = false)
        : this(audioMixer, videoMixer, driftCorrectionConfig: null, ownsAudioMixer, ownsVideoMixer)
    {
    }

    public AudioVideoMixer(
        AudioMixer audioMixer,
        IVideoMixer videoMixer,
        AudioVideoDriftCorrectionConfig? driftCorrectionConfig,
        bool ownsAudioMixer = false,
        bool ownsVideoMixer = false)
    {
        AudioMixer = audioMixer ?? throw new ArgumentNullException(nameof(audioMixer));
        VideoMixer = videoMixer ?? throw new ArgumentNullException(nameof(videoMixer));
        _ownsAudioMixer = ownsAudioMixer;
        _ownsVideoMixer = ownsVideoMixer;
        _driftCorrectionConfig = (driftCorrectionConfig ?? new AudioVideoDriftCorrectionConfig()).CloneNormalized();

        AudioMixer.SourceError += OnAudioSourceError;
        VideoMixer.SourceError += OnVideoSourceError;
        VideoMixer.OutputSourceChanged += OnVideoOutputSourceChanged;

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

    public double Position => MasterClock.CurrentTimestamp;

    public bool IsRunning => AudioMixer.IsRunning || VideoMixer.IsRunning;

    public int AudioSourceCount => AudioMixer.SourceCount;

    public int VideoSourceCount => VideoMixer.SourceCount;

    public int VideoOutputCount => VideoMixer.OutputCount;

    public event EventHandler<AudioErrorEventArgs>? AudioSourceError;

    public event EventHandler<VideoErrorEventArgs>? VideoSourceError;

    public event EventHandler<VideoOutputSourceChangedEventArgs>? VideoOutputSourceChanged;

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

    public bool AddVideoSource(FFVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.AddSource(source);
    }

    public bool RemoveVideoSource(FFVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.RemoveSource(source);
    }

    public FFVideoSource[] GetVideoSources()
    {
        ThrowIfDisposed();
        return VideoMixer.GetSources();
    }

    public void ClearVideoSources()
    {
        ThrowIfDisposed();
        VideoMixer.ClearSources();
    }

    public bool AddVideoOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return VideoMixer.AddOutput(output);
    }

    public bool RemoveVideoOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return VideoMixer.RemoveOutput(output);
    }

    public IVideoOutput[] GetVideoOutputs()
    {
        ThrowIfDisposed();
        return VideoMixer.GetOutputs();
    }

    public void ClearVideoOutputs()
    {
        ThrowIfDisposed();
        VideoMixer.ClearOutputs();
    }

    public bool BindVideoOutputToSource(IVideoOutput output, FFVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.BindOutputToSource(output, source);
    }

    public bool UnbindVideoOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return VideoMixer.UnbindOutput(output);
    }

    public IVideoOutput[] GetVideoOutputsForSource(FFVideoSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        return VideoMixer.GetOutputsForSource(source);
    }

    public FFVideoSource? GetVideoSourceForOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return VideoMixer.GetSourceForOutput(output);
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
        ThrowIfDisposed();
        var target = Math.Max(0, positionInSeconds);

        MasterClock.SeekTo(target);

        foreach (var audioSource in AudioMixer.GetSources())
        {
            if (audioSource.State != AudioState.EndOfStream)
                continue;

            if (target >= audioSource.Duration)
                continue;

            try
            {
                audioSource.Seek(target);
                audioSource.Play();
            }
            catch
            {
                // Ignore.
            }
        }

        VideoMixer.Seek(target, safeSeek: false);
        ResetVideoDriftCorrections();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        AudioMixer.SourceError -= OnAudioSourceError;
        VideoMixer.SourceError -= OnVideoSourceError;
        VideoMixer.OutputSourceChanged -= OnVideoOutputSourceChanged;
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

    private void OnVideoOutputSourceChanged(object? sender, VideoOutputSourceChangedEventArgs e)
    {
        VideoOutputSourceChanged?.Invoke(this, e);
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

    private void DriftCorrectionTick()
    {
        if (_disposed || !IsRunning)
            return;

        if (!_driftCorrectionConfig.Enabled)
            return;

        if (Interlocked.Exchange(ref _driftCorrectionTickActive, 1) != 0)
            return;

        try
        {
            var masterTimestamp = MasterClock.CurrentTimestamp;
            foreach (var videoSource in VideoMixer.GetSources())
            {
                // Correct only routed sources.
                if (VideoMixer.GetOutputsForSource(videoSource).Length == 0)
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
                    try
                    {
                        videoSource.Seek(targetVideoPosition);
                    }
                    catch
                    {
                        // Ignore.
                    }

                    videoSource.ResetDriftCorrectionOffset();
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


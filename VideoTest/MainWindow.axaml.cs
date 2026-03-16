using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Ownaudio.Core;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Avalonia;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Sources;

namespace VideoTest;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5.0;

    private IAudioEngine? _engine;
    private AVMixer? _mixer;
    private FFAudioSource? _audioSource;
    private FFVideoSource? _videoSource;
    private VideoGL[] _videoViews = [];
    private DispatcherTimer? _videoStatsTimer;
    private bool _isDisposed;
    private bool _started;
    private long _lastDecodedFrames;
    private long _lastPresentedFrames;
    private long _lastDroppedFrames;
    private readonly Lock _seekLock = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            case Key.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-SeekStepSeconds);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(SeekStepSeconds);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void TogglePlayPause()
    {
        var running = _mixer?.IsRunning ?? false;
        if (running)
        {
            _mixer?.Pause();
            ConsolePrintLine("[Video] Paused.");
        }
        else
        {
            _mixer?.Start();
            ConsolePrintLine("[Video] Playing.");
        }
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (_mixer == null || !_seekLock.TryEnter())
            return;

        try
        {
            var newTimelinePosition = Math.Max(0.0, _mixer.MasterClock.CurrentTimestamp + deltaSeconds);
            _mixer.Seek(newTimelinePosition, safeSeek: true);
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_started)
            return;

        _started = true;

        const string testFile = "/run/media/seko/New Stuff/Other_Content/shootingstar_0611_1.mov";
        //const string testFile = "/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4";

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();

        _engine = AudioEngineFactory.CreateDefault();
        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };

        _engine.Initialize(config);
        _engine.Start();

        _audioSource = new FFAudioSource(testFile, config);
        _videoSource = new FFVideoSource(testFile, new FFVideoSourceOptions
        {
            UseDedicatedDecodeThread = true,
            QueueCapacity = 30,
            DecoderOptions = new FFVideoDecoderOptions
            {
                EnableHardwareDecoding = true,
                ThreadCount = GetSafeVideoThreadCount(),
                PreferredOutputPixelFormats =
                [
                    VideoPixelFormat.Yuv422p10le,  // ProRes native – zero-cost direct copy
                    VideoPixelFormat.Yuv422p,
                    VideoPixelFormat.Nv12,
                    VideoPixelFormat.Yuv420p,
                    VideoPixelFormat.Rgba32
                ],
                PreferSourcePixelFormatWhenSupported = true,
                PreferLowestConversionCost = true
            },
            //EnableDriftCorrection = false,
            //DriftCorrectionDeadZoneSeconds = 0.006,
            //DriftCorrectionRate = 0.03,
            //MaxCorrectionStepSeconds = 0.003
        });
        _mixer = new AVMixer(_engine);
        _mixer.AddAudioSource(_audioSource);
        _mixer.AddVideoSource(_videoSource);

        _videoViews =
        [
            new VideoGL(_videoSource),
            new VideoGL(_videoSource, false),
            new VideoGL(_videoSource, false),
            new VideoGL(_videoSource, false)
        ];
        VideoControl.Content = _videoViews[0];
        VideoControl2.Content = _videoViews[1];
        VideoControl3.Content = _videoViews[2];
        VideoControl4.Content = _videoViews[3];

        _videoStatsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_videoSource == null || _audioSource == null || _mixer is not { IsRunning: true })
                return;

            // Stop logging once playback has finished.
            if (_videoSource.IsEndOfStream && _audioSource.IsEndOfStream)
            {
                _videoStatsTimer?.Stop();
                ConsolePrintLine("[Video] Playback finished.");
                return;
            }

            var decodedFrames = _videoSource.DecodedFrameCount;
            var presentedFrames = _videoSource.PresentedFrameCount;
            var droppedFrames = _videoSource.DroppedFrameCount;
            var masterTimestamp = _mixer.MasterClock.CurrentTimestamp;
            var audioTimestamp = _audioSource.Position;
            var videoTimestamp = _videoSource.CurrentFramePtsSeconds;
            var correctionOffsetMs = _videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
            var expectedVideoTimestamp = masterTimestamp - _videoSource.StartOffset;
            var expectedAudioTimestamp = masterTimestamp - _audioSource.StartOffset;
            var audioMasterDriftMs = (audioTimestamp - expectedAudioTimestamp) * 1000.0;
            var videoMasterDriftMs = double.IsNaN(videoTimestamp)
                ? double.NaN
                : (videoTimestamp - expectedVideoTimestamp) * 1000.0;
            var avDriftMs = double.IsNaN(videoTimestamp)
                ? double.NaN
                : (videoTimestamp - audioTimestamp) * 1000.0;

            var decodedDelta = decodedFrames - _lastDecodedFrames;
            var presentedDelta = presentedFrames - _lastPresentedFrames;
            var droppedDelta = droppedFrames - _lastDroppedFrames;

            // Pixel-format conversion info: "yuv422p10le" (direct) or "yuv422p10le→nv12" (converted)
            var srcFmt = FmtName(_videoSource.DecoderSourcePixelFormatName);
            var dstFmt = FmtName(_videoSource.DecoderOutputPixelFormatName);
            var fmtInfo = string.Equals(srcFmt, dstFmt, StringComparison.OrdinalIgnoreCase)
                ? srcFmt
                : $"{srcFmt}→{dstFmt}";

            Title = $"VideoTest - {_videoSource.StreamInfo.FrameRate:F1} fps | pres {presentedDelta} dec {decodedDelta} drop {droppedDelta} | q {_videoSource.QueueDepth} | hw {_videoSource.IsHardwareDecoding} | {fmtInfo} | a-v {avDriftMs:F1}ms | v-m {videoMasterDriftMs:F1}ms | corr {correctionOffsetMs:F1}ms";

            ConsoleOverwriteLine(
                $"[Video] {_videoSource.StreamInfo.FrameRate:F1}fps" +
                $"  pres={presentedDelta} dec={decodedDelta} drop={droppedDelta}" +
                $"  q={_videoSource.QueueDepth}  hw={_videoSource.IsHardwareDecoding}" +
                $"  fmt={fmtInfo}" +
                $"  m={masterTimestamp:F3}s a={audioTimestamp:F3}s v={videoTimestamp:F3}s" +
                $"  a-m={audioMasterDriftMs:+0.0;-0.0}ms v-m={videoMasterDriftMs:+0.0;-0.0}ms a-v={avDriftMs:+0.0;-0.0}ms" +
                $"  corr={correctionOffsetMs:+0.0;-0.0}ms");

            _lastDecodedFrames = decodedFrames;
            _lastPresentedFrames = presentedFrames;
            _lastDroppedFrames = droppedFrames;
        });
        _videoStatsTimer.Start();

        _mixer.Start();
        _audioSource.Play();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_isDisposed)
        {
            base.OnClosed(e);
            return;
        }

        _isDisposed = true;

        try
        {
            _mixer?.Stop();
            if (_audioSource != null)
                _mixer?.RemoveAudioSource(_audioSource);
        }
        catch
        {
            // Best effort during shutdown.
        }

        foreach (var view in _videoViews)
            view.Dispose();
        _videoViews = [];
        _videoStatsTimer?.Stop();
        _videoStatsTimer = null;
        _videoSource?.Dispose();
        _audioSource?.Dispose();
        _mixer?.Dispose();

        if (_engine != null)
        {
            try
            {
                _engine.Stop();
            }
            catch
            {
                // Best effort during shutdown.
            }

            _engine.Dispose();
        }

        base.OnClosed(e);
    }
}
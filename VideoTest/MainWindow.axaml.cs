using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using Seko.OwnAudioSharp.Video.Decoders;
using Seko.OwnAudioSharp.Video.Sources;

namespace VideoTest;

public partial class MainWindow : Window
{
    private IAudioEngine? _engine;
    private AudioMixer? _mixer;
    private FFAudioSource? _audioSource;
    private FFVideoSource? _videoSource;
    private VideoGL? _videoGl;
    private VideoGL? _videoGl2;
    private VideoGL? _videoGl3;
    private VideoGL? _videoGl4;
    private Timer? _videoPumpTimer;
    private DispatcherTimer? _videoStatsTimer;
    private bool _isDisposed;
    private bool _started;
    private long _lastDecodedFrames;
    private long _lastPresentedFrames;
    private long _lastDroppedFrames;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F11)
        {
            this.WindowState = this.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_started)
            return;

        _started = true;

        const string testFile = "/run/media/seko/New Stuff/Other_Content/shootingstar_0611_1.mov";

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
            QueueCapacity = 6,
            DecoderOptions = new FFVideoDecoderOptions
            {
                EnableHardwareDecoding = false,
                ThreadCount = GetSafeVideoThreadCount()
            },
            EnableDriftCorrection = true
        });
        _mixer = new AudioMixer(_engine);

        _mixer.AddSource(_audioSource);
        _audioSource.AttachToClock(_mixer.MasterClock);
        _videoSource.AttachToClock(_mixer.MasterClock);

        _videoGl = new VideoGL(_videoSource);
        _videoGl2 = new VideoGL(_videoSource);
        _videoGl3 = new VideoGL(_videoSource);
        _videoGl4 = new VideoGL(_videoSource);
        VideoControl.Content = _videoGl;
        VideoControl2.Content = _videoGl2;
        VideoControl3.Content = _videoGl3;
        VideoControl4.Content = _videoGl4;

        _videoPumpTimer = new Timer(
            _ =>
            {
                if (_videoSource != null)
                    _videoSource.RequestNextFrame(out var _);
            },
            null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromMilliseconds(8));

        _videoStatsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_videoSource == null || _audioSource == null || _mixer == null)
                return;

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

            Title = $"VideoTest - target {_videoSource.StreamInfo.FrameRate:F1} fps | presented {presentedDelta} | decoded {decodedDelta} | dropped {droppedDelta} | queue {_videoSource.QueueDepth} | hw {_videoSource.IsHardwareDecoding} | a-v {avDriftMs:F1}ms | v-m {videoMasterDriftMs:F1}ms | corr {correctionOffsetMs:F1}ms";
            Console.WriteLine($"[Video] target={_videoSource.StreamInfo.FrameRate:F1} fps, presented={presentedDelta}, decoded={decodedDelta}, dropped={droppedDelta}, queue={_videoSource.QueueDepth}, hw={_videoSource.IsHardwareDecoding}, master={masterTimestamp:F3}s, audio={audioTimestamp:F3}s, video={videoTimestamp:F3}s, a-m={audioMasterDriftMs:F1}ms, v-m={videoMasterDriftMs:F1}ms, a-v={avDriftMs:F1}ms, corr={correctionOffsetMs:F1}ms");

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
                _mixer?.RemoveSource(_audioSource);
        }
        catch
        {
            // Best effort during shutdown.
        }

        _videoGl?.Dispose();
        _videoGl2?.Dispose();
        _videoGl3?.Dispose();
        _videoGl4?.Dispose();
        _videoPumpTimer?.Dispose();
        _videoPumpTimer = null;
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

    private static int GetSafeVideoThreadCount()
    {
        var suggested = Math.Max(2, Environment.ProcessorCount / 4);
        return Math.Min(8, suggested);
    }
}
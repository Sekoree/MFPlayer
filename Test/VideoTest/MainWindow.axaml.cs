using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Avalonia;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;

namespace VideoTest;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5.0;

    private FFVideoDecoder? _videoDecoder;
    private VideoEngine? _videoEngine;
    private VideoGL[] _videoViews = [];
    private DispatcherTimer? _videoStatsTimer;
    private bool _isDisposed;
    private bool _started;
    private long _lastDecodedFrames;
    private long _lastSubmittedFrames;
    private long _lastDroppedFrames;
    private VideoGL.VideoGlDiagnostics[] _lastVideoGlDiagnosticsPerView = [];
    private readonly Lock _seekLock = new();
    private Thread? _playbackThread;
    private volatile bool _stopPlaybackThread;
    private bool _isPlaying = true;
    private bool _decodePrimePending;
    private bool _isEndOfStream;
    private long _timelineSecondsBits;
    private long _decodedFrames;
    private long _submittedFrames;
    private long _droppedFrames;

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
            case Key.Home:
                SeekToStart();
                e.Handled = true;
                break;
            case Key.End:
                SeekToEnd();
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
        if (_videoDecoder == null)
            return;

        var isPlaying = Volatile.Read(ref _isPlaying);
        if (isPlaying)
        {
            Volatile.Write(ref _isPlaying, false);
            ConsolePrintLine("[Video] Paused.");
        }
        else
        {
            Volatile.Write(ref _isPlaying, true);
            ConsolePrintLine("[Video] Playing.");
        }
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (_videoDecoder == null || !_seekLock.TryEnter())
            return;

        try
        {
            PerformSeek(Math.Max(0.0, GetTimelineSeconds() + deltaSeconds));
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void SeekToStart()
    {
        if (_videoDecoder == null || !_seekLock.TryEnter())
            return;

        try
        {
            PerformSeek(0);
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void SeekToEnd()
    {
        if (_videoDecoder == null || !_seekLock.TryEnter())
            return;

        try
        {
            PerformSeek(Math.Max(0, _videoDecoder.StreamInfo.Duration.TotalSeconds - 0.001));
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void BindViewsToEngine()
    {
        if (_videoEngine == null)
            throw new InvalidOperationException("Video engine has not been initialized.");

        var primaryView = new VideoGL(_videoEngine)
        {
            KeepAspectRatio = true,
            PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
        };

        _videoViews =
        [
            primaryView,
            VideoGL.CreateMirror(primaryView),
            VideoGL.CreateMirror(primaryView),
            VideoGL.CreateMirror(primaryView)
        ];

        foreach (var view in _videoViews)
        {
            view.KeepAspectRatio = true;
            view.PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync;
        }

        if (_videoEngine.OutputCount != 1)
            throw new InvalidOperationException($"Expected exactly one engine-bound output for VideoTest mirroring, but found {_videoEngine.OutputCount}.");

        ConsolePrintLine($"[VideoTest] Routed decoder→engine with 1 primary VideoGL mirrored across {_videoViews.Length} Avalonia views.");
    }

    private double GetTimelineSeconds()
    {
        return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _timelineSecondsBits));
    }

    private void SetTimelineSeconds(double value)
    {
        Interlocked.Exchange(ref _timelineSecondsBits, BitConverter.DoubleToInt64Bits(Math.Max(0, value)));
    }

    private void PerformSeek(double positionSeconds)
    {
        if (_videoDecoder == null)
            return;

        var target = Math.Max(0, positionSeconds);
        if (_videoDecoder.TrySeek(TimeSpan.FromSeconds(target), out var seekError))
        {
            SetTimelineSeconds(target);
            _isEndOfStream = false;
            Volatile.Write(ref _decodePrimePending, true);
        }
        else
        {
            ConsolePrintLine($"[Video] Seek failed: {seekError}");
        }
    }

    private void PlaybackLoop()
    {
        while (!_stopPlaybackThread)
        {
            var decoder = _videoDecoder;
            var engine = _videoEngine;
            if (decoder == null || engine == null)
                break;

            if (Volatile.Read(ref _isPlaying) || Volatile.Read(ref _decodePrimePending))
            {
                if (!_seekLock.TryEnter())
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    if (decoder.TryDecodeNextFrame(out var frame, out var decodeError))
                    {
                        using (frame)
                        {
                            SetTimelineSeconds(frame.PtsSeconds);
                            var timelineSeconds = GetTimelineSeconds();
                            if (engine.PushFrame(frame, timelineSeconds))
                                Interlocked.Increment(ref _submittedFrames);
                            else
                                Interlocked.Increment(ref _droppedFrames);
                        }

                        Interlocked.Increment(ref _decodedFrames);
                        _isEndOfStream = false;
                        Volatile.Write(ref _decodePrimePending, false);
                    }
                    else if (decoder.IsEndOfStream)
                    {
                        if (!_isEndOfStream)
                            ConsolePrintLine("[Video] End of stream reached.");

                        _isEndOfStream = true;
                        Volatile.Write(ref _isPlaying, false);
                        Volatile.Write(ref _decodePrimePending, false);
                    }
                    else if (!string.IsNullOrWhiteSpace(decodeError))
                    {
                        ConsolePrintLine($"[Video] Decode error: {decodeError}");
                        Thread.Sleep(5);
                    }
                }
                finally
                {
                    _seekLock.Exit();
                }
            }

            Thread.Sleep(Volatile.Read(ref _isPlaying) ? 0 : 10);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_started)
            return;

        _started = true;

        var testFile = ResolveTestMediaPath(Program.LaunchArgs);
        if (string.IsNullOrWhiteSpace(testFile))
        {
            Title = "VideoTest - no demo video found";
            ConsolePrintLine("[VideoTest] No demo video was found. Pass a file path as the first argument or set MFPLAYER_TEST_VIDEO.");
            return;
        }

        var ffmpegRoot = ResolveFfmpegRootPath();
        if (!string.IsNullOrWhiteSpace(ffmpegRoot))
            ffmpeg.RootPath = ffmpegRoot;

        DynamicallyLoadedBindings.Initialize();

        FFVideoDecoder decoder;
        try
        {
            decoder = new FFVideoDecoder(testFile, new FFVideoDecoderOptions
            {
                EnableHardwareDecoding = true,
                ThreadCount = GetSafeVideoThreadCount(),
                UseDedicatedDecodeThread = true,
                QueueCapacity = 30,
                PreferredOutputPixelFormats =
                [
                    VideoPixelFormat.Yuv422p10le,
                    VideoPixelFormat.Yuv422p,
                    VideoPixelFormat.Nv12,
                    VideoPixelFormat.Yuv420p,
                    VideoPixelFormat.Rgba32
                ],
                PreferSourcePixelFormatWhenSupported = true,
                PreferLowestConversionCost = true
            });
        }
        catch (Exception ex)
        {
            Title = "VideoTest - decoder init failed";
            ConsolePrintLine($"[VideoTest] Failed to open '{testFile}': {ex.Message}");
            return;
        }

        _videoDecoder = decoder;
        _videoEngine = new VideoEngine(new VideoEngineConfig
        {
            FpsLimit = decoder.StreamInfo.FrameRate > 0 ? decoder.StreamInfo.FrameRate : null,
            PixelFormatPolicy = VideoEnginePixelFormatPolicy.Auto,
            DropRejectedFrames = false
        });
        SetTimelineSeconds(0);
        _isPlaying = true;
        _decodePrimePending = false;
        _isEndOfStream = false;
        Interlocked.Exchange(ref _decodedFrames, 0);
        Interlocked.Exchange(ref _submittedFrames, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        
        BindViewsToEngine();

        _lastVideoGlDiagnosticsPerView = new VideoGL.VideoGlDiagnostics[_videoViews.Length];
        VideoControl.Content = _videoViews[0];
        VideoControl2.Content = _videoViews[1];
        VideoControl3.Content = _videoViews[2];
        VideoControl4.Content = _videoViews[3];

        _stopPlaybackThread = false;
        _playbackThread = new Thread(PlaybackLoop)
        {
            Name = "VideoTest.PlaybackLoop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _playbackThread.Start();

        _videoStatsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_videoDecoder == null || _videoEngine == null)
                return;

            var decodedFrames = Interlocked.Read(ref _decodedFrames);
            var submittedFrames = Interlocked.Read(ref _submittedFrames);
            var droppedFrames = Interlocked.Read(ref _droppedFrames);
            var masterTimestamp = GetTimelineSeconds();
            var videoTimestamp = masterTimestamp;
            const double correctionOffsetMs = 0;
            const double videoMasterDriftMs = 0;

            var decodedDelta = decodedFrames - _lastDecodedFrames;
            var submittedDelta = submittedFrames - _lastSubmittedFrames;
            var droppedDelta = droppedFrames - _lastDroppedFrames;

            if (_lastVideoGlDiagnosticsPerView.Length != _videoViews.Length)
                _lastVideoGlDiagnosticsPerView = new VideoGL.VideoGlDiagnostics[_videoViews.Length];

            var perViewDiagText = new string[_videoViews.Length];
            for (var i = 0; i < _videoViews.Length; i++)
            {
                var currentDiag = _videoViews[i].GetDiagnosticsSnapshot();
                var lastDiag = _lastVideoGlDiagnosticsPerView[i];
                var advanceTicksDelta = currentDiag.AdvanceTicks - lastDiag.AdvanceTicks;
                var advanceSuccessDelta = currentDiag.AdvanceSuccess - lastDiag.AdvanceSuccess;
                var frameReadyDelta = currentDiag.FrameReadyEvents - lastDiag.FrameReadyEvents;
                var renderDelta = currentDiag.RenderCalls - lastDiag.RenderCalls;
                var renderPostedDelta = currentDiag.RenderRequestPosted - lastDiag.RenderRequestPosted;
                var renderCoalescedDelta = currentDiag.RenderRequestCoalesced - lastDiag.RenderRequestCoalesced;

                perViewDiagText[i] =
                    $"v{i + 1}[tick={advanceTicksDelta} ok={advanceSuccessDelta} ready={frameReadyDelta} ren={renderDelta} rq+={renderPostedDelta} rq={renderCoalescedDelta}]";
                _lastVideoGlDiagnosticsPerView[i] = currentDiag;
            }

            var srcFmt = FmtName(_videoDecoder.LastSourcePixelFormatName);
            var dstFmt = FmtName(_videoDecoder.LastOutputPixelFormatName);
            var fmtInfo = string.Equals(srcFmt, dstFmt, StringComparison.OrdinalIgnoreCase)
                ? srcFmt
                : $"{srcFmt}→{dstFmt}";

            var engineOutputCount = _videoEngine.OutputCount;

            Title = $"VideoTest - engine 1→{engineOutputCount}, UI x{_videoViews.Length} | {_videoDecoder.StreamInfo.FrameRate:F1} fps | sub {submittedDelta} dec {decodedDelta} drop {droppedDelta} | hw {_videoDecoder.IsHardwareDecoding} | {fmtInfo} | v-m {videoMasterDriftMs:F1}ms | corr {correctionOffsetMs:F1}ms";

            ConsoleOverwriteLine(
                $"[Video] {_videoDecoder.StreamInfo.FrameRate:F1}fps" +
                $"  sub={submittedDelta} dec={decodedDelta} drop={droppedDelta}" +
                $"  q=0  hw={_videoDecoder.IsHardwareDecoding}" +
                $"  fmt={fmtInfo}" +
                $"  m={masterTimestamp:F3}s v={videoTimestamp:F3}s" +
                $"  v-m={videoMasterDriftMs:+0.0;-0.0}ms" +
                $"  corr={correctionOffsetMs:+0.0;-0.0}ms" +
                $"  {string.Join(" ", perViewDiagText)}");

            _lastDecodedFrames = decodedFrames;
            _lastSubmittedFrames = submittedFrames;
            _lastDroppedFrames = droppedFrames;
        });
        _videoStatsTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_isDisposed)
        {
            base.OnClosed(e);
            return;
        }

        _isDisposed = true;
        _stopPlaybackThread = true;

        if (_playbackThread is { IsAlive: true })
        {
            try
            {
                _playbackThread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best effort during shutdown.
            }
        }

        try
        {
            if (_videoViews.Length > 0)
                _videoEngine?.RemoveOutput(_videoViews[0]);
        }
        catch
        {
            // Best effort during shutdown.
        }

        foreach (var view in _videoViews)
            view.Dispose();
        _videoViews = [];
        _lastVideoGlDiagnosticsPerView = [];
        _videoStatsTimer?.Stop();
        _videoStatsTimer = null;
        _videoDecoder?.Dispose();
        _videoEngine?.Dispose();

        base.OnClosed(e);
    }
}


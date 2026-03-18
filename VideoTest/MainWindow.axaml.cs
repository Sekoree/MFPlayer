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
using Seko.OwnAudioNET.Video.Sources;

namespace VideoTest;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5.0;

    private IVideoEngine? _videoEngine;
    private FFVideoSource? _videoSource;
    private VideoGL[] _videoViews = [];
    private DispatcherTimer? _videoStatsTimer;
    private bool _isDisposed;
    private bool _started;
    private long _lastDecodedFrames;
    private long _lastPresentedFrames;
    private long _lastDroppedFrames;
    private VideoGL.VideoGlDiagnostics[] _lastVideoGlDiagnosticsPerView = [];
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
        var running = _videoEngine?.IsRunning ?? false;
        if (running)
        {
            _videoEngine?.Pause();
            ConsolePrintLine("[Video] Paused.");
        }
        else
        {
            _videoEngine?.Start();
            ConsolePrintLine("[Video] Playing.");
        }
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (_videoEngine == null || !_seekLock.TryEnter())
            return;

        try
        {
            var newTimelinePosition = Math.Max(0.0, _videoEngine.Position + deltaSeconds);
            _videoEngine.Seek(newTimelinePosition, safeSeek: true);
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void SeekToStart()
    {
        if (_videoEngine == null || !_seekLock.TryEnter())
            return;

        try
        {
            _videoEngine.Seek(0, safeSeek: true);
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void SeekToEnd()
    {
        if (_videoEngine == null || _videoSource == null || !_seekLock.TryEnter())
            return;

        try
        {
            if (!_videoSource.SeekToEnd())
                return;

            var timelineTarget = Math.Max(0, _videoSource.Position + _videoSource.StartOffset);
            _videoEngine.Seek(timelineTarget, safeSeek: true);
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

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();

        _videoSource = new FFVideoSource(testFile, new FFVideoSourceOptions
        {
            UseDedicatedDecodeThread = true,
            QueueCapacity = 30,
            LateDropThresholdSeconds = 0.050,
            LateDropFrameMultiplier = 3.0,
            MaxDropsPerRequest = 1,
            DecoderOptions = new FFVideoDecoderOptions
            {
                EnableHardwareDecoding = true,
                ThreadCount = GetSafeVideoThreadCount(),
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
            },
            EnableDriftCorrection = true,
            DriftCorrectionDeadZoneSeconds = 0.006,
            DriftCorrectionRate = 0.03,
            MaxCorrectionStepSeconds = 0.003
        });

        _videoEngine = new VideoEngine();
        _videoEngine.AddVideoSource(_videoSource);

        _videoViews =
        [
            new VideoGL(_videoEngine),
            new VideoGL(_videoEngine),
            new VideoGL(_videoEngine),
            new VideoGL(_videoEngine)
        ];
        _lastVideoGlDiagnosticsPerView = new VideoGL.VideoGlDiagnostics[_videoViews.Length];
        VideoControl.Content = _videoViews[0];
        VideoControl2.Content = _videoViews[1];
        VideoControl3.Content = _videoViews[2];
        VideoControl4.Content = _videoViews[3];

        _videoStatsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_videoSource == null || _videoEngine == null)
                return;

            if (_videoSource.IsEndOfStream)
            {
                _videoStatsTimer?.Stop();
                ConsolePrintLine("[Video] Playback finished.");
                return;
            }

            var decodedFrames = _videoSource.DecodedFrameCount;
            var presentedFrames = _videoSource.PresentedFrameCount;
            var droppedFrames = _videoSource.DroppedFrameCount;
            var masterTimestamp = _videoEngine.Position;
            var videoTimestamp = _videoSource.CurrentFramePtsSeconds;
            var correctionOffsetMs = _videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
            var expectedVideoTimestamp = masterTimestamp - _videoSource.StartOffset;
            var videoMasterDriftMs = double.IsNaN(videoTimestamp)
                ? double.NaN
                : (videoTimestamp - expectedVideoTimestamp) * 1000.0;

            var decodedDelta = decodedFrames - _lastDecodedFrames;
            var presentedDelta = presentedFrames - _lastPresentedFrames;
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

            var srcFmt = FmtName(_videoSource.DecoderSourcePixelFormatName);
            var dstFmt = FmtName(_videoSource.DecoderOutputPixelFormatName);
            var fmtInfo = string.Equals(srcFmt, dstFmt, StringComparison.OrdinalIgnoreCase)
                ? srcFmt
                : $"{srcFmt}→{dstFmt}";

            Title = $"VideoTest - {_videoSource.StreamInfo.FrameRate:F1} fps | pres {presentedDelta} dec {decodedDelta} drop {droppedDelta} | q {_videoSource.QueueDepth} | hw {_videoSource.IsHardwareDecoding} | {fmtInfo} | v-m {videoMasterDriftMs:F1}ms | corr {correctionOffsetMs:F1}ms";

            ConsoleOverwriteLine(
                $"[Video] {_videoSource.StreamInfo.FrameRate:F1}fps" +
                $"  pres={presentedDelta} dec={decodedDelta} drop={droppedDelta}" +
                $"  q={_videoSource.QueueDepth}  hw={_videoSource.IsHardwareDecoding}" +
                $"  fmt={fmtInfo}" +
                $"  m={masterTimestamp:F3}s v={videoTimestamp:F3}s" +
                $"  v-m={videoMasterDriftMs:+0.0;-0.0}ms" +
                $"  corr={correctionOffsetMs:+0.0;-0.0}ms" +
                $"  {string.Join(" ", perViewDiagText)}");

            _lastDecodedFrames = decodedFrames;
            _lastPresentedFrames = presentedFrames;
            _lastDroppedFrames = droppedFrames;
        });
        _videoStatsTimer.Start();

        _videoEngine.Start();
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
            _videoEngine?.Stop();
            if (_videoSource != null)
                _videoEngine?.RemoveVideoSource(_videoSource);
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
        _videoSource?.Dispose();
        _videoEngine?.Dispose();

        base.OnClosed(e);
    }
}


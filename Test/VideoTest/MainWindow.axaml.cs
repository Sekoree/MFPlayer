using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Avalonia;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.Sources;
using AudioPlaybackEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace VideoTest;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5.0;

    private IAudioEngine? _audioEngine;
    private AudioMixer? _audioMixer;
    private IAudioVideoMixer? _playbackMixer;
    private FFVideoSource? _videoSource;
    private FFAudioSource? _audioSource;
    private FFSharedDemuxSession? _sharedDemuxSession;
    private VideoGL[] _videoViews = [];
    private DispatcherTimer? _videoStatsTimer;
    private bool _isDisposed;
    private bool _started;
    private long _lastDecodedFrames;
    private long _lastSubmittedFrames;
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
        if (_playbackMixer == null)
            return;

        if (_playbackMixer.IsRunning)
        {
            _playbackMixer.Pause();
            ConsolePrintLine("[Video] Paused.");
        }
        else
        {
            _playbackMixer.Start();
            ConsolePrintLine("[Video] Playing.");
        }
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (_videoSource == null || _playbackMixer == null || !_seekLock.TryEnter())
            return;

        try
        {
            PerformSeek(Math.Max(0.0, _playbackMixer.Position + deltaSeconds));
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void SeekToStart()
    {
        if (_videoSource == null || !_seekLock.TryEnter())
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
        if (_videoSource == null || !_seekLock.TryEnter())
            return;

        try
        {
            PerformSeek(Math.Max(0, _videoSource.StreamInfo.Duration.TotalSeconds - 0.001));
        }
        finally
        {
            _seekLock.Exit();
        }
    }

    private void BindViewsToEngine()
    {
        if (_playbackMixer == null || _videoSource == null)
            throw new InvalidOperationException("Playback mixer has not been initialized.");

        var primaryView = new VideoGL
        {
            KeepAspectRatio = true,
            PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
        };

        if (!_playbackMixer.AddVideoOutput(primaryView))
            throw new InvalidOperationException("Failed to add primary VideoGL output to playback mixer.");

        if (!_playbackMixer.BindVideoOutputToSource(primaryView, _videoSource))
            throw new InvalidOperationException("Failed to bind primary VideoGL output to video source.");

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

        if (_playbackMixer.VideoOutputCount != 1)
            throw new InvalidOperationException($"Expected exactly one mixer-bound output for VideoTest mirroring, but found {_playbackMixer.VideoOutputCount}.");

        ConsolePrintLine($"[VideoTest] Routed decoder→engine with 1 primary VideoGL mirrored across {_videoViews.Length} Avalonia views.");
    }

    private void PerformSeek(double positionSeconds)
    {
        if (_playbackMixer == null)
            return;

        var target = Math.Max(0, positionSeconds);
        try
        {
            _playbackMixer.Seek(target);
        }
        catch (Exception seekError)
        {
            ConsolePrintLine($"[Video] Seek failed: {seekError.Message}");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_started)
            return;

        _started = true;

        var testFile = "/home/seko/Videos/shootingstar_0611_1.mov";
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

        if (!MediaStreamCatalog.TryGetFirstStream(testFile, MediaStreamKind.Video, out var videoStream))
        {
            Title = "VideoTest - no video stream";
            ConsolePrintLine($"[VideoTest] No video stream found in '{testFile}'.");
            return;
        }

        if (!MediaStreamCatalog.TryGetFirstStream(testFile, MediaStreamKind.Audio, out var audioStream))
        {
            Title = "VideoTest - no audio stream";
            ConsolePrintLine($"[VideoTest] No audio stream found in '{testFile}'.");
            return;
        }

        var useSharedDemux = IsSharedDemuxEnabled();
        FFVideoDecoder videoDecoder;
        FFAudioDecoder audioDecoder;
        var requestedAudioConfig = AudioConfig.Default;
        try
        {
            _audioEngine = AudioPlaybackEngineFactory.CreateEngine(requestedAudioConfig);
            var startResult = _audioEngine.Start();
            if (startResult < 0)
                throw new InvalidOperationException($"Failed to start audio engine. Error code: {startResult}");

            var negotiatedBufferSize = _audioEngine.FramesPerBuffer > 0
                ? _audioEngine.FramesPerBuffer
                : requestedAudioConfig.BufferSize;

            var audioConfig = new AudioConfig
            {
                SampleRate = requestedAudioConfig.SampleRate,
                Channels = requestedAudioConfig.Channels,
                BufferSize = negotiatedBufferSize
            };

            var decoderOptions = new FFVideoDecoderOptions
            {
                PreferredStreamIndex = videoStream.Index,
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
            };

            if (useSharedDemux)
            {
                _sharedDemuxSession = FFSharedDemuxSession.OpenFile(testFile, new FFSharedDemuxSessionOptions
                {
                    InitialStreamIndices = [videoStream.Index, audioStream.Index],
                    PacketQueueCapacityPerStream = 200
                });
                videoDecoder = new FFVideoDecoder(_sharedDemuxSession, decoderOptions);
                audioDecoder = new FFAudioDecoder(_sharedDemuxSession, audioConfig.SampleRate, audioConfig.Channels, audioStream.Index);
            }
            else
            {
                videoDecoder = new FFVideoDecoder(testFile, decoderOptions);
                audioDecoder = new FFAudioDecoder(testFile, audioConfig.SampleRate, audioConfig.Channels, audioStream.Index);
            }

            _videoSource = new FFVideoSource(
                videoDecoder,
                new FFVideoSourceOptions
                {
                    HoldLastFrameOnEndOfStream = true
                },
                ownsDecoder: true);

            _audioSource = new FFAudioSource(audioDecoder, audioConfig, ownsDecoder: true);
            _audioMixer = new AudioMixer(_audioEngine, negotiatedBufferSize);

            var videoTransportConfig = new VideoTransportEngineConfig
            {
                PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync
            }.CloneNormalized();
            videoTransportConfig.ClockSyncMode = VideoTransportClockSyncMode.AudioLed;

            var videoClock = new MasterClockVideoClockAdapter(_audioMixer.MasterClock);
            var videoTransport = new VideoTransportEngine(videoClock, videoTransportConfig, ownsClock: false);
            var videoMixer = new VideoMixer(videoTransport, ownsEngine: true);
            var driftCorrectionConfig = new AudioVideoDriftCorrectionConfig
            {
                Enabled = true
            };
            _playbackMixer = new AudioVideoMixer(_audioMixer, videoMixer, driftCorrectionConfig, ownsAudioMixer: false, ownsVideoMixer: true);

            _playbackMixer.AudioSourceError += static (_, args) => ConsolePrintLine($"[Audio] {args.Message}");
            _playbackMixer.VideoSourceError += static (_, args) => ConsolePrintLine($"[Video] {args.Message}");

            _audioSource.AttachToClock(_audioMixer.MasterClock);
            if (!_playbackMixer.AddAudioSource(_audioSource))
                throw new InvalidOperationException("Failed to add audio source to playback mixer.");

            if (!_playbackMixer.AddVideoSource(_videoSource))
                throw new InvalidOperationException("Failed to add video source to playback mixer.");

            _audioSource.Play();
        }
        catch (Exception ex)
        {
            Title = "VideoTest - decoder init failed";
            ConsolePrintLine($"[VideoTest] Failed to open '{testFile}': {ex.Message}");
            CleanupPlaybackResources();
            return;
        }

        ConsolePrintLine(useSharedDemux
            ? $"[VideoTest] Shared demux enabled for stream {videoStream.Index}."
            : $"[VideoTest] Shared demux disabled; using separate decode session for stream {videoStream.Index}.");
        
        BindViewsToEngine();

        _lastVideoGlDiagnosticsPerView = new VideoGL.VideoGlDiagnostics[_videoViews.Length];
        VideoControl.Content = _videoViews[0];
        VideoControl2.Content = _videoViews[1];
        VideoControl3.Content = _videoViews[2];
        VideoControl4.Content = _videoViews[3];

        _playbackMixer!.Start();

        _videoStatsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_videoSource == null || _audioSource == null || _playbackMixer == null)
                return;

            var decodedFrames = _videoSource.DecodedFrameCount;
            var submittedFrames = _videoSource.PresentedFrameCount;
            var droppedFrames = _videoSource.DroppedFrameCount;
            var masterTimestamp = _playbackMixer.Position;
            var videoTimestamp = _videoSource.CurrentFramePtsSeconds;
            var audioTimestamp = _audioSource.Position;
            var correctionOffsetMs = _videoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
            var expectedVideoTimestamp = masterTimestamp - _videoSource.StartOffset;
            var videoMasterDriftMs = double.IsNaN(videoTimestamp) ? 0 : (videoTimestamp - expectedVideoTimestamp) * 1000.0;
            var videoAudioDriftMs = double.IsNaN(videoTimestamp) ? 0 : (videoTimestamp - audioTimestamp) * 1000.0;

            var decodedDelta = decodedFrames - _lastDecodedFrames;
            var submittedDelta = submittedFrames - _lastSubmittedFrames;
            var droppedDelta = droppedFrames - _lastDroppedFrames;

            if (_lastVideoGlDiagnosticsPerView.Length != _videoViews.Length)
                _lastVideoGlDiagnosticsPerView = new VideoGL.VideoGlDiagnostics[_videoViews.Length];

            var perViewDiagText = new string[_videoViews.Length];
            long totalUploadPlaneDelta = 0;
            long totalUploadFrameDelta = 0;
            long totalStridedPlaneDelta = 0;
            long totalStridedFrameDelta = 0;
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
                var uploadPlanesDelta = currentDiag.UploadPlanes - lastDiag.UploadPlanes;
                var stridedPlanesDelta = currentDiag.StridedUploadPlanes - lastDiag.StridedUploadPlanes;
                var stridedFramesDelta = currentDiag.StridedUploadFrames - lastDiag.StridedUploadFrames;
                var uploadFramesDelta = renderDelta;
                var stridedFrameRatio = uploadFramesDelta > 0
                    ? (stridedFramesDelta / (double)uploadFramesDelta) * 100.0
                    : 0;
                var stridedPlaneRatio = uploadPlanesDelta > 0
                    ? (stridedPlanesDelta / (double)uploadPlanesDelta) * 100.0
                    : 0;
                totalUploadPlaneDelta += uploadPlanesDelta;
                totalUploadFrameDelta += uploadFramesDelta;
                totalStridedPlaneDelta += stridedPlanesDelta;
                totalStridedFrameDelta += stridedFramesDelta;

                perViewDiagText[i] =
                    $"v{i + 1}[tick={advanceTicksDelta} ok={advanceSuccessDelta} ready={frameReadyDelta} ren={renderDelta} rq+={renderPostedDelta} rq={renderCoalescedDelta} up={uploadFramesDelta} upP={uploadPlanesDelta} strF={stridedFramesDelta}({stridedFrameRatio:0.0}%) strP={stridedPlanesDelta}({stridedPlaneRatio:0.0}%)]";
                _lastVideoGlDiagnosticsPerView[i] = currentDiag;
            }

            var aggregateStridedFrameRatio = totalUploadFrameDelta > 0
                ? (totalStridedFrameDelta / (double)totalUploadFrameDelta) * 100.0
                : 0;
            var aggregateStridedPlaneRatio = totalUploadPlaneDelta > 0
                ? (totalStridedPlaneDelta / (double)totalUploadPlaneDelta) * 100.0
                : 0;

            var srcFmt = FmtName(_videoSource.DecoderSourcePixelFormatName);
            var dstFmt = FmtName(_videoSource.DecoderOutputPixelFormatName);
            var fmtInfo = string.Equals(srcFmt, dstFmt, StringComparison.OrdinalIgnoreCase)
                ? srcFmt
                : $"{srcFmt}→{dstFmt}";

            var engineOutputCount = _playbackMixer.VideoOutputCount;

            Title = $"VideoTest - engine 1→{engineOutputCount}, UI x{_videoViews.Length} | {_videoSource.StreamInfo.FrameRate:F1} fps | pres {submittedDelta} dec {decodedDelta} drop {droppedDelta} | up {totalUploadFrameDelta} upP {totalUploadPlaneDelta} | strF {totalStridedFrameDelta} ({aggregateStridedFrameRatio:0.0}%) strP {totalStridedPlaneDelta} ({aggregateStridedPlaneRatio:0.0}%) | hw {_videoSource.IsHardwareDecoding} | {fmtInfo} | v-m {videoMasterDriftMs:F1}ms | v-a {videoAudioDriftMs:F1}ms | corr {correctionOffsetMs:F1}ms";

            ConsoleOverwriteLine(
                $"[A/V] {_videoSource.StreamInfo.FrameRate:F1}fps" +
                $"  pres={submittedDelta} dec={decodedDelta} drop={droppedDelta}" +
                $"  q={_videoSource.QueueDepth}  hw={_videoSource.IsHardwareDecoding}" +
                $"  up={totalUploadFrameDelta} upP={totalUploadPlaneDelta}" +
                $"  strF={totalStridedFrameDelta}({aggregateStridedFrameRatio:0.0}%) strP={totalStridedPlaneDelta}({aggregateStridedPlaneRatio:0.0}%)" +
                $"  fmt={fmtInfo}" +
                $"  m={masterTimestamp:F3}s a={audioTimestamp:F3}s v={videoTimestamp:F3}s" +
                $"  v-m={videoMasterDriftMs:+0.0;-0.0}ms" +
                $"  v-a={videoAudioDriftMs:+0.0;-0.0}ms" +
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
        CleanupPlaybackResources();

        base.OnClosed(e);
    }

    private void CleanupPlaybackResources()
    {
        _videoStatsTimer?.Stop();
        _videoStatsTimer = null;

        try
        {
            if (_playbackMixer != null && _videoViews.Length > 0)
                _playbackMixer.RemoveVideoOutput(_videoViews[0]);
        }
        catch
        {
        }

        foreach (var view in _videoViews)
            view.Dispose();
        _videoViews = [];
        _lastVideoGlDiagnosticsPerView = [];

        try
        {
            _playbackMixer?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _playbackMixer = null;
        }

        _videoSource?.Dispose();
        _videoSource = null;
        _audioSource?.Dispose();
        _audioSource = null;
        _sharedDemuxSession?.Dispose();
        _sharedDemuxSession = null;

        _audioMixer?.Dispose();
        _audioMixer = null;

        if (_audioEngine != null)
        {
            try
            {
                if (_audioEngine.OwnAudioEngineStopped() == 0)
                    _audioEngine.Stop();
            }
            catch
            {
            }

            _audioEngine.Dispose();
            _audioEngine = null;
        }
    }
}


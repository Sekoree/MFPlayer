using FFmpeg.AutoGen;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using SDL3;
using System.Collections.Concurrent;
using System.Runtime;
using Ownaudio.Native;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.NDI;
using Seko.OwnAudioNET.Video.Probing;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;

namespace AudioEx;

internal static class Program
{
    private const double SeekStepSeconds = 5.0;

    private enum ControlActionKind
    {
        TogglePlayPause,
        SeekAbsolute,
        ToggleFullscreen,
        ToggleHud
    }

    private readonly struct ControlAction
    {
        public ControlAction(ControlActionKind kind, double positionSeconds)
        {
            Kind = kind;
            PositionSeconds = positionSeconds;
        }

        public ControlActionKind Kind { get; }

        public double PositionSeconds { get; }
    }

    private sealed class PlaybackInputState
    {
        public double TimelineSeconds;

        public double DurationSeconds;
    }

    private sealed class PlaylistMedia : IDisposable
    {
        private bool _disposed;

        public PlaylistMedia(
            string filePath,
            VideoStreamSource videoSource,
            AudioStreamSource audioSource,
            FFSharedDemuxSession? sharedDemuxSession,
            double startOffsetSeconds,
            double durationSeconds)
        {
            Label = Path.GetFileName(filePath);
            VideoSource = videoSource;
            AudioSource = audioSource;
            SharedDemuxSession = sharedDemuxSession;
            StartOffsetSeconds = startOffsetSeconds;
            DurationSeconds = durationSeconds;
            EndOffsetSeconds = startOffsetSeconds + durationSeconds;
        }

        public string Label { get; }

        public VideoStreamSource VideoSource { get; }

        public AudioStreamSource AudioSource { get; }

        public FFSharedDemuxSession? SharedDemuxSession { get; }

        public double StartOffsetSeconds { get; }

        public double DurationSeconds { get; }

        public double EndOffsetSeconds { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            VideoSource.Dispose();
            AudioSource.Dispose();
            SharedDemuxSession?.Dispose();
            _disposed = true;
        }
    }

    private sealed class BurstTenSecondsAccumulator
    {
        private DateTime _windowStartUtc;
        private long _audioHardSeek;
        private long _audioHardSuppressed;
        private long _audioHardFailure;
        private long _videoResyncAttempt;
        private long _videoResyncSuccess;
        private long _videoResyncFailure;
        private long _videoSuppressedTicks;
        private double _videoMasterDriftMin = double.PositiveInfinity;
        private double _videoMasterDriftMax = double.NegativeInfinity;
        private double _videoAudioDriftMin = double.PositiveInfinity;
        private double _videoAudioDriftMax = double.NegativeInfinity;

        public BurstTenSecondsAccumulator(DateTime windowStartUtc)
        {
            _windowStartUtc = windowStartUtc;
        }

        public void AddCounters(
            long audioHardSeek,
            long audioHardSuppressed,
            long audioHardFailure,
            long videoSuppressedTicks,
            long videoResyncAttempt,
            long videoResyncSuccess,
            long videoResyncFailure)
        {
            _audioHardSeek += audioHardSeek;
            _audioHardSuppressed += audioHardSuppressed;
            _audioHardFailure += audioHardFailure;
            _videoSuppressedTicks += videoSuppressedTicks;
            _videoResyncAttempt += videoResyncAttempt;
            _videoResyncSuccess += videoResyncSuccess;
            _videoResyncFailure += videoResyncFailure;
        }

        public void AddDriftSamples(double videoMasterDriftMs, double videoAudioDriftMs)
        {
            if (!double.IsNaN(videoMasterDriftMs) && !double.IsInfinity(videoMasterDriftMs))
            {
                _videoMasterDriftMin = Math.Min(_videoMasterDriftMin, videoMasterDriftMs);
                _videoMasterDriftMax = Math.Max(_videoMasterDriftMax, videoMasterDriftMs);
            }

            if (!double.IsNaN(videoAudioDriftMs) && !double.IsInfinity(videoAudioDriftMs))
            {
                _videoAudioDriftMin = Math.Min(_videoAudioDriftMin, videoAudioDriftMs);
                _videoAudioDriftMax = Math.Max(_videoAudioDriftMax, videoAudioDriftMs);
            }
        }

        public bool TryBuildSummary(DateTime nowUtc, out string summary)
        {
            if ((nowUtc - _windowStartUtc).TotalSeconds < 10)
            {
                summary = string.Empty;
                return false;
            }

            summary =
                $"[Burst10s] a_hseek={_audioHardSeek} a_hsup={_audioHardSuppressed} a_hfail={_audioHardFailure}" +
                $" | v_rseek={_videoResyncAttempt} v_rok={_videoResyncSuccess} v_rfail={_videoResyncFailure} v_rsup={_videoSuppressedTicks}" +
                $" | v-m={FormatRange(_videoMasterDriftMin, _videoMasterDriftMax)} v-a={FormatRange(_videoAudioDriftMin, _videoAudioDriftMax)}";

            Reset(nowUtc);
            return true;
        }

        private void Reset(DateTime nowUtc)
        {
            _windowStartUtc = nowUtc;
            _audioHardSeek = 0;
            _audioHardSuppressed = 0;
            _audioHardFailure = 0;
            _videoResyncAttempt = 0;
            _videoResyncSuccess = 0;
            _videoResyncFailure = 0;
            _videoSuppressedTicks = 0;
            _videoMasterDriftMin = double.PositiveInfinity;
            _videoMasterDriftMax = double.NegativeInfinity;
            _videoAudioDriftMin = double.PositiveInfinity;
            _videoAudioDriftMax = double.NegativeInfinity;
        }

        private static string FormatRange(double min, double max)
        {
            return !double.IsInfinity(min) && !double.IsInfinity(max)
                ? $"{min:+0.0;-0.0}..{max:+0.0;-0.0}ms"
                : "n/a";
        }
    }

    private readonly record struct VideoTickDeltas(
        long Decoded,
        long Presented,
        long Dropped,
        long UploadedFrames,
        long UploadedPlanes,
        long StridedUploadedFrames,
        long StridedUploadedPlanes,
        double StridedFrameRatio,
        double StridedPlaneRatio);

    private readonly record struct SyncTickDeltas(
        long HardSeek,
        long HardSuppressed,
        long HardFailure,
        long DriftSuppressed,
        long DriftResyncAttempt,
        long DriftResyncSuccess,
        long DriftResyncFailure);

    private static VideoTickDeltas ComputeVideoTickDeltas(
        long decodedFrames,
        long presentedFrames,
        long droppedFrames,
        long uploadedFrames,
        long uploadedPlanes,
        long stridedUploadedFrames,
        long stridedUploadedPlanes,
        long lastDecodedFrames,
        long lastPresentedFrames,
        long lastDroppedFrames,
        long lastUploadedFrames,
        long lastUploadedPlanes,
        long lastStridedUploadedFrames,
        long lastStridedUploadedPlanes)
    {
        var decodedDelta = decodedFrames - lastDecodedFrames;
        var presentedDelta = presentedFrames - lastPresentedFrames;
        var droppedDelta = droppedFrames - lastDroppedFrames;
        var uploadedFrameDelta = uploadedFrames - lastUploadedFrames;
        var uploadedPlaneDelta = uploadedPlanes - lastUploadedPlanes;
        var stridedUploadedFrameDelta = stridedUploadedFrames - lastStridedUploadedFrames;
        var stridedUploadedPlaneDelta = stridedUploadedPlanes - lastStridedUploadedPlanes;
        var stridedFrameRatio = uploadedFrameDelta > 0
            ? (stridedUploadedFrameDelta / (double)uploadedFrameDelta) * 100.0
            : 0;
        var stridedPlaneRatio = uploadedPlaneDelta > 0
            ? (stridedUploadedPlaneDelta / (double)uploadedPlaneDelta) * 100.0
            : 0;

        return new VideoTickDeltas(
            decodedDelta,
            presentedDelta,
            droppedDelta,
            uploadedFrameDelta,
            uploadedPlaneDelta,
            stridedUploadedFrameDelta,
            stridedUploadedPlaneDelta,
            stridedFrameRatio,
            stridedPlaneRatio);
    }

    private static SyncTickDeltas ComputeSyncTickDeltas(
        AudioStreamSource.DiagnosticsSnapshot audioDiag,
        AudioVideoMixer.DiagnosticsSnapshot driftDiag,
        long lastAudioHardSyncSeekCount,
        long lastAudioHardSyncSuppressedCount,
        long lastAudioHardSyncFailureCount,
        long lastDriftSuppressedTickCount,
        long lastDriftHardResyncAttemptCount,
        long lastDriftHardResyncSuccessCount,
        long lastDriftHardResyncFailureCount)
    {
        return new SyncTickDeltas(
            audioDiag.HardSyncSeekCount - lastAudioHardSyncSeekCount,
            audioDiag.HardSyncSeekSuppressedCount - lastAudioHardSyncSuppressedCount,
            audioDiag.HardSyncSeekFailureCount - lastAudioHardSyncFailureCount,
            driftDiag.DriftCorrectionSuppressedTickCount - lastDriftSuppressedTickCount,
            driftDiag.DriftHardResyncAttemptCount - lastDriftHardResyncAttemptCount,
            driftDiag.DriftHardResyncSuccessCount - lastDriftHardResyncSuccessCount,
            driftDiag.DriftHardResyncFailureCount - lastDriftHardResyncFailureCount);
    }

    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
            Console.Out.Flush();
        };

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex}");
            Console.Out.Flush();
        }
    }

    private static void Run(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        var testFile60Fps = "/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4";
        var testFile60Fps2 = "/home/seko/Videos/おねがいダーリン_0611.mov";
        var testFile60Fps3 = "/home/seko/Videos/shootingstar_0611_1.mov";
        var inputFiles = args.Length > 0
            ? args.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray()
            : [testFile60Fps, testFile60Fps2, testFile60Fps3];

        if (inputFiles.Length == 0)
        {
            Console.WriteLine("[AudioEx] No media input provided.");
            return;
        }

        foreach (var file in inputFiles)
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"[AudioEx] Missing media file: {file}");
                return;
            }
        }

        var ffmpegRoot = ResolveFfmpegRootPath();
        if (!string.IsNullOrWhiteSpace(ffmpegRoot))
            ffmpeg.RootPath = ffmpegRoot;

        DynamicallyLoadedBindings.Initialize();
        Console.WriteLine($"FFmpeg version: {ffmpeg.av_version_info()}");

        var streamSelections = new (string FilePath, MediaStreamInfoEntry VideoStream, MediaStreamInfoEntry AudioStream)[inputFiles.Length];
        for (var i = 0; i < inputFiles.Length; i++)
        {
            var file = inputFiles[i];
            if (!MediaStreamCatalog.TryGetFirstStream(file, MediaStreamKind.Video, out var videoStream))
            {
                Console.WriteLine($"[AudioEx] No video stream found in '{file}'.");
                return;
            }

            if (!MediaStreamCatalog.TryGetFirstStream(file, MediaStreamKind.Audio, out var audioStream))
            {
                Console.WriteLine($"[AudioEx] No audio stream found in '{file}'.");
                return;
            }

            streamSelections[i] = (file, videoStream, audioStream);
        }
        
        var enableNdiOutputs = !string.Equals(
            Environment.GetEnvironmentVariable("AUDIOEX_ENABLE_NDI"),
            "0",
            StringComparison.Ordinal);
        var ndiOutputs = CreateNdiOutputs(enableNdiOutputs, ResolveNdiSenderNames());

        var decoderThreadCount = GetSafeVideoThreadCount(streamSelections.Select(static selection => selection.VideoStream));
        var requestedAudioConfig = AudioConfig.Default;

        using var audioEngine = new NativeAudioEngine();
        audioEngine.Initialize(requestedAudioConfig);

        try
        {
            var startResult = audioEngine.Start();
            if (startResult < 0)
                throw new InvalidOperationException($"Failed to start audio engine. Error code: {startResult}");

            var negotiatedBufferSize = audioEngine.FramesPerBuffer > 0
                ? audioEngine.FramesPerBuffer
                : requestedAudioConfig.BufferSize;

            var audioConfig = new AudioConfig
            {
                SampleRate = requestedAudioConfig.SampleRate,
                Channels = requestedAudioConfig.Channels,
                BufferSize = negotiatedBufferSize
            };

            var useSharedDemux = !string.Equals(
                Environment.GetEnvironmentVariable("AUDIOEX_USE_SHARED_DEMUX"),
                "0",
                StringComparison.Ordinal);
            using var audioMixer = new AudioMixer(audioEngine, negotiatedBufferSize);

            var videoTransportConfig = new VideoEngineConfig
            {
                PresentationSyncMode = VideoPresentationSyncMode.PreferVSync
            }.CloneNormalized();
            videoTransportConfig.ClockSyncMode = VideoClockSyncMode.AudioLed;

            var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
            
            
            var videoMultiplexer = new BroadcastVideoEngine(new VideoEngineConfig()
            {
                PixelFormatPolicy = VideoEnginePixelFormatPolicy.Auto
            });
            foreach (var ndiOutput in ndiOutputs)
            {
                if (!videoMultiplexer.AddOutput(ndiOutput.VideoOutput))
                    ConsolePrintLine($"[NDI] Failed to register output '{ndiOutput.Config.SenderName}' in multiplexer.");
            }
            
            using var videoMixer = new VideoMixer(videoMultiplexer, videoClock, videoTransportConfig);
            var driftCorrectionConfig = new AudioVideoDriftCorrectionConfig
            {
                Enabled = true
            };
            using var playbackMixer = new AudioVideoMixer(audioMixer, videoMixer, driftCorrectionConfig, ownsAudioMixer: false, ownsVideoMixer: false);

            playbackMixer.AudioSourceError += static (_, e) => ConsolePrintLine($"[Audio] {e.Message}");
            playbackMixer.VideoSourceError += static (_, e) => ConsolePrintLine($"[Video] {e.Message}");

            var playlist = new List<PlaylistMedia>(streamSelections.Length);
            try
            {
                var timelineOffsetSeconds = 0.0;
                foreach (var selection in streamSelections)
                {
                    var decoderOptions = CreateDemoDecoderOptions(
                        decoderThreadCount,
                        selection.VideoStream.Index,
                        forceRgbaOutput: true);
                    FFSharedDemuxSession? sharedDemux = null;
                    FFVideoDecoder videoDecoder;
                    FFAudioDecoder audioDecoder;

                    if (useSharedDemux)
                    {
                        sharedDemux = FFSharedDemuxSession.OpenFile(selection.FilePath, new FFSharedDemuxSessionOptions
                        {
                            InitialStreamIndices = [selection.VideoStream.Index, selection.AudioStream.Index],
                            PacketQueueCapacityPerStream = 200
                        });
                        videoDecoder = new FFVideoDecoder(sharedDemux, decoderOptions);
                        audioDecoder = new FFAudioDecoder(sharedDemux, audioConfig.SampleRate, audioConfig.Channels, selection.AudioStream.Index);
                    }
                    else
                    {
                        videoDecoder = new FFVideoDecoder(selection.FilePath, decoderOptions);
                        audioDecoder = new FFAudioDecoder(selection.FilePath, audioConfig.SampleRate, audioConfig.Channels, selection.AudioStream.Index);
                    }

                    var videoSource = new VideoStreamSource(
                        videoDecoder,
                        new VideoStreamSourceOptions
                        {
                            HoldLastFrameOnEndOfStream = true
                        },
                        ownsDecoder: true)
                    {
                        StartOffset = timelineOffsetSeconds
                    };

                    var audioSource = new AudioStreamSource(
                        audioDecoder,
                        audioConfig,
                        ownsDecoder: true)
                    {
                        StartOffset = timelineOffsetSeconds
                    };

                    audioSource.AttachToClock(audioMixer.MasterClock);

                    if (!playbackMixer.AddAudioSource(audioSource))
                        throw new InvalidOperationException($"Failed to add audio source for '{selection.FilePath}'.");

                    if (!playbackMixer.AddVideoSource(videoSource))
                        throw new InvalidOperationException($"Failed to add video source for '{selection.FilePath}'.");

                    audioSource.Play();

                    var durationSeconds = ResolvePlaybackDurationSeconds(videoSource.Duration, audioSource.Duration);
                    var playlistItem = new PlaylistMedia(selection.FilePath, videoSource, audioSource, sharedDemux, timelineOffsetSeconds, durationSeconds);
                    playlist.Add(playlistItem);
                    timelineOffsetSeconds += durationSeconds;

                    Console.WriteLine(
                        $"[Audio] {playlistItem.Label} | stream={selection.AudioStream.Index} codec={selection.AudioStream.Codec} | {audioConfig.SampleRate}Hz/{audioConfig.Channels}ch buffer={audioConfig.BufferSize} | start={playlistItem.StartOffsetSeconds:F3}s dur={playlistItem.DurationSeconds:F3}s");
                    Console.WriteLine($"[Video] {playlistItem.Label} | {playlistItem.VideoSource.StreamInfo} | start={playlistItem.StartOffsetSeconds:F3}s");
                }

                var playlistDurationSeconds = playlist.Count == 0 ? 0 : playlist[^1].EndOffsetSeconds;
                var videoRenderer = new VideoSDL
                {
                    EnableHudOverlay = false,
                    KeepAspectRatio = true
                };
                

                try
                {
                    if (!videoRenderer.Initialize(1280, 720, "MFPlayer", out var glError))
                    {
                        Console.WriteLine($"VideoSDL init failed: {glError}");
                        return;
                    }

                    videoRenderer.Start();
                    videoMultiplexer.AddOutput(videoRenderer);

                    var activeMedia = playlist[0];
                    if (!playbackMixer.SetActiveVideoSource(activeMedia.VideoSource))
                        throw new InvalidOperationException($"Failed to set active source '{activeMedia.Label}'.");

                    var inputState = new PlaybackInputState
                    {
                        TimelineSeconds = 0,
                        DurationSeconds = playlistDurationSeconds
                    };

                    PlaylistMedia ResolveActiveMedia(double timelinePositionSeconds)
                    {
                        foreach (var item in playlist)
                        {
                            if (timelinePositionSeconds < item.EndOffsetSeconds)
                                return item;
                        }

                        return playlist[^1];
                    }

                    void SwitchActiveMedia(PlaylistMedia nextMedia)
                    {
                        if (ReferenceEquals(activeMedia, nextMedia))
                            return;

                        if (!playbackMixer.SetActiveVideoSource(nextMedia.VideoSource))
                            throw new InvalidOperationException($"Failed to set active source '{nextMedia.Label}'.");

                        activeMedia = nextMedia;
                    }

                    void PrimeVisibleSource()
                    {
                        try
                        {
                            if (activeMedia.VideoSource.RequestNextFrame(out _))
                                return;

                            activeMedia.VideoSource.TryGetFrameAtTime(playbackMixer.Position, out _);
                        }
                        catch
                        {
                            // Best-effort prime only; normal playback loop will continue requesting frames.
                        }
                    }

                    void UpdateRendererDiagnostics()
                    {
                        var masterTs = playbackMixer.Position;
                        var videoTs = activeMedia.VideoSource.CurrentFramePtsSeconds;
                        var expectedVideoTimestamp = masterTs - activeMedia.VideoSource.StartOffset;
                        var videoMasterDriftMs = double.IsNaN(videoTs)
                            ? 0
                            : (videoTs - expectedVideoTimestamp) * 1000.0;

                        videoRenderer.UpdateFormatInfo(
                            activeMedia.VideoSource.DecoderSourcePixelFormatName,
                            activeMedia.VideoSource.DecoderOutputPixelFormatName,
                            activeMedia.VideoSource.StreamInfo.FrameRate);
                        videoRenderer.UpdateHudDiagnostics(
                            queueDepth: activeMedia.VideoSource.QueueDepth,
                            uploadMsPerFrame: 0,
                            avDriftMs: videoMasterDriftMs,
                            isHardwareDecoding: activeMedia.VideoSource.IsHardwareDecoding,
                            droppedFrames: activeMedia.VideoSource.DroppedFrameCount);
                    }

                    PrimeVisibleSource();
                    UpdateRendererDiagnostics();
                    playbackMixer.Start();
                    if (ndiOutputs.Count > 0)
                        Console.WriteLine($"[NDI] Active senders: {string.Join(", ", ndiOutputs.Select(static output => output.Config.SenderName))}");
                    Console.WriteLine($"Audio/video mixer initialised successfully for {playlist.Count} media item(s).");

                    var controlActions = new ConcurrentQueue<ControlAction>();
                    var seekLock = new Lock();
                    var playbackFinished = false;
                    videoRenderer.KeyDown += key =>
                    {
                        switch (key)
                        {
                            case SDL.Keycode.Space:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.TogglePlayPause, 0));
                                break;
                            case SDL.Keycode.Left:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, inputState.TimelineSeconds - SeekStepSeconds));
                                break;
                            case SDL.Keycode.Right:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, inputState.TimelineSeconds + SeekStepSeconds));
                                break;
                            case SDL.Keycode.Home:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, 0));
                                break;
                            case SDL.Keycode.End:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.SeekAbsolute, Math.Max(0, inputState.DurationSeconds - 0.001)));
                                break;
                            case SDL.Keycode.F11:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleFullscreen, 0));
                                break;
                            case SDL.Keycode.H:
                                controlActions.Enqueue(new ControlAction(ControlActionKind.ToggleHud, 0));
                                break;
                        }
                    };

                    void PerformSeek(double positionSeconds)
                    {
                        if (!seekLock.TryEnter())
                            return;

                        try
                        {
                            var target = Math.Clamp(positionSeconds, 0, Math.Max(0, playlistDurationSeconds));
                            playbackFinished = false;
                            playbackMixer.Seek(target);
                            SwitchActiveMedia(ResolveActiveMedia(target));
                            PrimeVisibleSource();
                            UpdateRendererDiagnostics();
                        }
                        finally
                        {
                            seekLock.Exit();
                        }
                    }

                    var lastDecodedFrames = 0L;
                    var lastPresentedFrames = 0L;
                    var lastDroppedFrames = 0L;
                    var lastUploadedFrames = 0L;
                    var lastUploadedPlanes = 0L;
                    var lastStridedUploadedPlanes = 0L;
                    var lastStridedUploadedFrames = 0L;
                    var lastAudioHardSyncSeekCount = 0L;
                    var lastAudioHardSyncSuppressedCount = 0L;
                    var lastAudioHardSyncFailureCount = 0L;
                    var lastDriftSuppressedTickCount = 0L;
                    var lastDriftHardResyncAttemptCount = 0L;
                    var lastDriftHardResyncSuccessCount = 0L;
                    var lastDriftHardResyncFailureCount = 0L;
                    var lastStatsLogTime = DateTime.UtcNow;
                    var burstAccumulator = new BurstTenSecondsAccumulator(lastStatsLogTime);
                    var playbackTailToleranceSeconds = Math.Max(0.050, negotiatedBufferSize / (double)audioConfig.SampleRate * 2.0);

                    bool HasPlaybackFinished()
                    {
                        if (playlist.All(static item => item.VideoSource.IsEndOfStream && item.AudioSource.IsEndOfStream))
                            return true;

                        if (playlistDurationSeconds <= 0)
                            return false;

                        if (playbackMixer.Position < Math.Max(0, playlistDurationSeconds - playbackTailToleranceSeconds))
                            return false;

                        var lastItem = playlist[^1];
                        var lastAudioNearTail = lastItem.AudioSource.Duration <= 0 ||
                                                lastItem.AudioSource.Position >= Math.Max(0, lastItem.AudioSource.Duration - playbackTailToleranceSeconds);
                        return lastItem.VideoSource.IsEndOfStream && lastAudioNearTail;
                    }

                    var loop = true;
                    while (loop)
                    {
                        inputState.TimelineSeconds = playbackMixer.Position;

                        while (controlActions.TryDequeue(out var action))
                        {
                            switch (action.Kind)
                            {
                                case ControlActionKind.TogglePlayPause:
                                    if (playbackMixer.IsRunning)
                                    {
                                        playbackMixer.Pause();
                                        ConsolePrintLine("[Playback] Paused.");
                                    }
                                    else
                                    {
                                        playbackMixer.Start();
                                        PrimeVisibleSource();
                                        ConsolePrintLine("[Playback] Playing.");
                                    }
                                    break;

                                case ControlActionKind.SeekAbsolute:
                                    PerformSeek(action.PositionSeconds);
                                    break;

                                case ControlActionKind.ToggleFullscreen:
                                    var window = videoRenderer.SdlWindowPtr;
                                    if (window != nint.Zero)
                                    {
                                        var isFullscreen = (SDL.GetWindowFlags(window) & SDL.WindowFlags.Fullscreen) != 0;
                                        SDL.SetWindowFullscreen(window, !isFullscreen);
                                    }

                                    break;

                                case ControlActionKind.ToggleHud:
                                    videoRenderer.EnableHudOverlay = !videoRenderer.EnableHudOverlay;
                                    ConsolePrintLine(videoRenderer.EnableHudOverlay
                                        ? "[Video] HUD enabled."
                                        : "[Video] HUD disabled.");
                                    break;
                            }
                        }

                        if (!videoRenderer.IsRunning)
                        {
                            loop = false;
                            continue;
                        }

                        var nextActive = ResolveActiveMedia(playbackMixer.Position);
                        if (!ReferenceEquals(nextActive, activeMedia))
                        {
                            SwitchActiveMedia(nextActive);
                            PrimeVisibleSource();
                            UpdateRendererDiagnostics();
                        }

                        if (!playbackFinished && HasPlaybackFinished())
                        {
                            playbackMixer.Pause();
                            UpdateRendererDiagnostics();
                            ConsolePrintLine("[Playback] End of stream reached for all playlist media.");
                            playbackFinished = true;
                            loop = false;
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        if (!playbackFinished && (now - lastStatsLogTime).TotalSeconds >= 1)
                        {
                            UpdateRendererDiagnostics();

                            var decodedFrames = activeMedia.VideoSource.DecodedFrameCount;
                            var presentedFrames = activeMedia.VideoSource.PresentedFrameCount;
                            var droppedFrames = activeMedia.VideoSource.DroppedFrameCount;
                            var masterTs = playbackMixer.Position;
                            var audioTs = activeMedia.AudioSource.Position;
                            var videoTs = activeMedia.VideoSource.CurrentFramePtsSeconds;
                            var correctionOffsetMs = activeMedia.VideoSource.CurrentDriftCorrectionOffsetSeconds * 1000.0;
                            var expectedVideoTimestamp = masterTs - activeMedia.VideoSource.StartOffset;
                            var videoMasterDriftMs = double.IsNaN(videoTs)
                                ? double.NaN
                                : (videoTs - expectedVideoTimestamp) * 1000.0;
                            var videoAudioDriftMs = double.IsNaN(videoTs)
                                ? double.NaN
                                : (videoTs - audioTs) * 1000.0;

                            var diag = videoRenderer.GetDiagnosticsSnapshot();
                            var uploadedFrames = diag.FramesRendered;
                            var uploadedPlanes = diag.UploadPlanes;
                            var stridedUploadedPlanes = diag.StridedUploadPlanes;
                            var stridedUploadedFrames = diag.StridedUploadFrames;
                            var videoDeltas = ComputeVideoTickDeltas(
                                decodedFrames,
                                presentedFrames,
                                droppedFrames,
                                uploadedFrames,
                                uploadedPlanes,
                                stridedUploadedFrames,
                                stridedUploadedPlanes,
                                lastDecodedFrames,
                                lastPresentedFrames,
                                lastDroppedFrames,
                                lastUploadedFrames,
                                lastUploadedPlanes,
                                lastStridedUploadedFrames,
                                lastStridedUploadedPlanes);

                            var audioDiag = activeMedia.AudioSource.GetDiagnosticsSnapshot();
                            var driftDiag = playbackMixer.GetDiagnosticsSnapshot();
                            var syncDeltas = ComputeSyncTickDeltas(
                                audioDiag,
                                driftDiag,
                                lastAudioHardSyncSeekCount,
                                lastAudioHardSyncSuppressedCount,
                                lastAudioHardSyncFailureCount,
                                lastDriftSuppressedTickCount,
                                lastDriftHardResyncAttemptCount,
                                lastDriftHardResyncSuccessCount,
                                lastDriftHardResyncFailureCount);

                            burstAccumulator.AddCounters(
                                syncDeltas.HardSeek,
                                syncDeltas.HardSuppressed,
                                syncDeltas.HardFailure,
                                syncDeltas.DriftSuppressed,
                                syncDeltas.DriftResyncAttempt,
                                syncDeltas.DriftResyncSuccess,
                                syncDeltas.DriftResyncFailure);
                            burstAccumulator.AddDriftSamples(videoMasterDriftMs, videoAudioDriftMs);

                            ConsoleOverwriteLine(
                                $"[A/V] {activeMedia.Label}" +
                                $" | tl={masterTs:F3}/{playlistDurationSeconds:F3}s" +
                                $" | render={videoRenderer.RenderFps:F1}fps src={activeMedia.VideoSource.StreamInfo.FrameRate:F1}fps fmt={videoRenderer.PixelFormatInfo}" +
                                $" | pres={videoDeltas.Presented} dec={videoDeltas.Decoded} drop={videoDeltas.Dropped} q={activeMedia.VideoSource.QueueDepth}" +
                                $" | up={videoDeltas.UploadedFrames} upP={videoDeltas.UploadedPlanes} strF={videoDeltas.StridedUploadedFrames} ({videoDeltas.StridedFrameRatio:0.0}%) strP={videoDeltas.StridedUploadedPlanes} ({videoDeltas.StridedPlaneRatio:0.0}%) hw={activeMedia.VideoSource.IsHardwareDecoding}" +
                                $" | a_hseek={syncDeltas.HardSeek}/{audioDiag.HardSyncSeekCount} a_hsup={syncDeltas.HardSuppressed}/{audioDiag.HardSyncSeekSuppressedCount} a_hfail={syncDeltas.HardFailure}/{audioDiag.HardSyncSeekFailureCount}" +
                                $" | v_rsup={syncDeltas.DriftSuppressed}/{driftDiag.DriftCorrectionSuppressedTickCount} v_rseek={syncDeltas.DriftResyncAttempt}/{driftDiag.DriftHardResyncAttemptCount} v_rok={syncDeltas.DriftResyncSuccess}/{driftDiag.DriftHardResyncSuccessCount} v_rfail={syncDeltas.DriftResyncFailure}/{driftDiag.DriftHardResyncFailureCount}" +
                                $" | m={masterTs:F3}s a={audioTs:F3}s v={videoTs:F3}s" +
                                $" v-m={videoMasterDriftMs:+0.0;-0.0}ms v-a={videoAudioDriftMs:+0.0;-0.0}ms corr={correctionOffsetMs:+0.0;-0.0}ms");

                            lastDecodedFrames = decodedFrames;
                            lastPresentedFrames = presentedFrames;
                            lastDroppedFrames = droppedFrames;

                            lastUploadedFrames = uploadedFrames;
                            lastUploadedPlanes = uploadedPlanes;
                            lastStridedUploadedPlanes = stridedUploadedPlanes;
                            lastStridedUploadedFrames = stridedUploadedFrames;
                            lastAudioHardSyncSeekCount = audioDiag.HardSyncSeekCount;
                            lastAudioHardSyncSuppressedCount = audioDiag.HardSyncSeekSuppressedCount;
                            lastAudioHardSyncFailureCount = audioDiag.HardSyncSeekFailureCount;
                            lastDriftSuppressedTickCount = driftDiag.DriftCorrectionSuppressedTickCount;
                            lastDriftHardResyncAttemptCount = driftDiag.DriftHardResyncAttemptCount;
                            lastDriftHardResyncSuccessCount = driftDiag.DriftHardResyncSuccessCount;
                            lastDriftHardResyncFailureCount = driftDiag.DriftHardResyncFailureCount;
                            lastStatsLogTime = now;

                            if (burstAccumulator.TryBuildSummary(now, out var burstSummary))
                            {
                                ConsolePrintLine(burstSummary);
                            }
                        }

                        Thread.Sleep(playbackMixer.IsRunning ? 0 : 10);
                    }

                    videoRenderer.Stop();
                }
                finally
                {
                    videoRenderer.Dispose();
                }
            }
            finally
            {
                foreach (var item in playlist)
                {
                    try
                    {
                        playbackMixer.RemoveVideoSource(item.VideoSource);
                    }
                    catch
                    {
                        // Best-effort teardown.
                    }

                    try
                    {
                        playbackMixer.RemoveAudioSource(item.AudioSource);
                    }
                    catch
                    {
                        // Best-effort teardown.
                    }

                    item.Dispose();
                }
            }
        }
        finally
        {
            if (audioEngine.OwnAudioEngineStopped() == 0)
            {
                try
                {
                    audioEngine.Stop();
                }
                catch
                {
                    // Best-effort engine shutdown.
                }
            }

            foreach (var ndiOutput in ndiOutputs)
            {
                try
                {
                    ndiOutput.Dispose();
                }
                catch
                {
                    // Best-effort NDI teardown.
                }
            }
        }
    }

    private static List<NDIVideoEngine> CreateNdiOutputs(bool enabled, IReadOnlyList<string> senderNames)
    {
        var outputs = new List<NDIVideoEngine>();
        if (!enabled)
            return outputs;

        try
        {
            foreach (var senderName in senderNames)
            {
                var output = new NDIVideoEngine(new NDIEngineConfig
                {
                    SenderName = senderName,
                    AudioSampleRate = 48000,
                    AudioChannels = 2,
                    RgbaSendFormat = NDIVideoRgbaSendFormat.Auto,
                    UseIncomingVideoTimestamps = false
                });

                output.Start();
                outputs.Add(output);
            }

            if (outputs.Count == 0)
                ConsolePrintLine("[NDI] Enabled, but no sender names resolved. NDI output is disabled for this run.");
            else
                ConsolePrintLine($"[NDI] Started {outputs.Count} sender(s).");

            return outputs;
        }
        catch
        {
            foreach (var output in outputs)
            {
                try
                {
                    output.Dispose();
                }
                catch
                {
                    // Best-effort rollback during startup failure.
                }
            }

            throw;
        }
    }

    private static string[] ResolveNdiSenderNames()
    {
        var raw = Environment.GetEnvironmentVariable("AUDIOEX_NDI_SENDERS");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var names = raw
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length > 0)
                return names;
        }

        return ["TestSender", "TestSender2"];
    }

    private static int GetSafeVideoThreadCount(IEnumerable<MediaStreamInfoEntry> videoStreams)
    {
        const int fallbackThreads = 6;

        var envOverride = Environment.GetEnvironmentVariable("AUDIOEX_VIDEO_THREADS");
        if (int.TryParse(envOverride, out var overrideThreads) && overrideThreads > 0)
            return Math.Clamp(overrideThreads, 1, 32);

        var streamArray = videoStreams.ToArray();
        var heaviestScore = 0.0;
        foreach (var stream in streamArray)
        {
            var width = Math.Max(0, stream.Width ?? 0);
            var height = Math.Max(0, stream.Height ?? 0);
            var fps = stream.FrameRate.GetValueOrDefault(30);
            if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
                fps = 30;

            var basePixelsPerSecond = width * (double)height * fps;
            if (basePixelsPerSecond <= 0)
                continue;

            var codec = stream.Codec;
            var codecWeight = codec.Contains("prores", StringComparison.OrdinalIgnoreCase)
                ? 1.6
                : codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) || codec.Contains("h265", StringComparison.OrdinalIgnoreCase)
                    ? 1.35
                    : codec.Contains("h264", StringComparison.OrdinalIgnoreCase)
                        ? 1.0
                        : 1.1;

            var weightedScore = basePixelsPerSecond * codecWeight;
            if (weightedScore > heaviestScore)
                heaviestScore = weightedScore;
        }

        if (heaviestScore <= 0)
            return fallbackThreads;

        var isUltraHeavy = heaviestScore >= (3840d * 2160d * 60d * 1.25d);
        var targetFraction = isUltraHeavy ? 0.50 : 0.40;
        var reservedCores = isUltraHeavy ? 2 : 3;
        var availableCores = Math.Max(2, Environment.ProcessorCount - reservedCores);
        var suggested = (int)Math.Round(availableCores * targetFraction, MidpointRounding.AwayFromZero);
        var minThreads = isUltraHeavy ? 6 : 4;
        return Math.Clamp(Math.Max(minThreads, suggested), minThreads, 16);
    }

    private static FFVideoDecoderOptions CreateDemoDecoderOptions(int threadCount, int? streamIndex, bool forceRgbaOutput = false)
    {
        return new FFVideoDecoderOptions
        {
            PreferredStreamIndex = streamIndex,
            EnableHardwareDecoding = true,
            ThreadCount = threadCount,
            QueueCapacity = 24,
            PreferredOutputPixelFormats = forceRgbaOutput
                ? [VideoPixelFormat.Rgba32]
                :
                [
                    VideoPixelFormat.Nv12,
                    VideoPixelFormat.Yuv420p,
                    VideoPixelFormat.P010le,
                    VideoPixelFormat.Yuv420p10le,
                    VideoPixelFormat.Rgba32,
                    VideoPixelFormat.Yuv422p,
                    VideoPixelFormat.Yuv422p10le
                ],
            PreferSourcePixelFormatWhenSupported = !forceRgbaOutput,
            PreferLowestConversionCost = true
        };
    }

    private static double ResolvePlaybackDurationSeconds(params double[] candidates)
    {
        return candidates
            .Where(static value => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static void ConsoleOverwriteLine(string message)
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch
        {
            width = 0;
        }

        var line = width > 1 ? message.PadRight(width - 1) : message;
        Console.Write("\r" + line);
    }

    private static void ConsolePrintLine(string message)
    {
        Console.WriteLine("\n" + message);
    }

    private static string? ResolveFfmpegRootPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        string[] candidates =
        [
            "/lib",
            "/usr/lib",
            "/usr/local/lib",
            "/usr/lib/x86_64-linux-gnu"
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
